using System;
using System.IO;
using System.Net;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GungeonOnline
{
    // What we last heard about one other player.
    public class RemotePlayerState
    {
        public int Slot;              // 0 = host, 1-3 = guests
        public string Name = "";
        public bool Connected;        // in the game right now (host: said hello; guest: on the host's roster)
        public float PingMs = -1f;    // only for players we talk to directly (host <-> guests)
        public float LastHeard;       // host: when this guest's last packet arrived (for timeouts)

        public bool HasPlayer;
        public string Scene = "";
        public Vector2 Position;      // center of the character, used for the name tag
        public Vector2 Aim;
        public float ReceivedAt;

        // What the character looks like right now.
        public int Identity;          // PlayableCharacters (Pilot, Convict, ...)
        public bool AltCostume;
        public string CustomCharacter = ""; // Custom Characters mod character name, "" for a normal character
        public bool Visible;
        public string Clip = "";      // animation name, e.g. "run_right"
        public int Frame;             // frame within that animation
        public bool FlipX;
        public Vector2 FeetPosition;  // bottom-center of the sprite in world space (newest update)
        public float SentAt = -1f;    // partner's clock when the newest update was sent
        public readonly MotionBuffer Motion = new MotionBuffer(); // smoothed feet position over time

        public bool IsDead;           // a ghost waiting for a chest, or dead
        public bool OnBreak;          // Take a Break: their bot is playing
        public float Health;          // hearts (1 = one heart), for the party list
        public float MaxHealth;
        public float Armor;
        public int Blanks;            // blanks they carry
        public PlayerLook Look = new PlayerLook(); // ghost glow, status tints, on fire
        public Vector2 RootOffset;    // their player transform minus their feet (for warping to them)

        // Their companion (e.g. the Hunter's dog).
        public bool HasPet;
        public string PetGuid = "";   // the companion's enemy guid
        public string PetClip = "";
        public int PetFrame;
        public bool PetFlipX;
        public Vector2 PetFeet;
        public readonly MotionBuffer PetMotion = new MotionBuffer();

        // The gun in their hand.
        public bool HasGun;
        public int GunId = -1;        // PickupObjectId
        public int GunSpriteId;       // current frame of the gun (firing/reloading animations)
        public Vector2 GunOffset;     // gun position relative to the feet
        public float GunAngle;
        public bool GunFlipY;         // guns are mirrored vertically when aiming left
        public float GunHeight;       // gun HeightOffGround: decides in front of / behind the body

        // The blob shadow under their feet.
        public bool HasShadow;
        public Vector3 ShadowOffset;  // drawn center: x,y relative to the feet; z relative to the body's z

        // Forget everything about the player in this slot (they left).
        public void Reset()
        {
            Connected = false;
            Name = "";
            PingMs = -1f;
            HasPlayer = false;
            IsDead = false;
            HasPet = false;
            Scene = "";
            SentAt = -1f;
            Motion.Clear();
            PetMotion.Clear();
        }
    }

    // Runs one multiplayer session over whichever link is active (Steam or LAN):
    // says hello, measures ping, and swaps player positions.
    //
    // Up to Protocol.MaxPlayers players. Each has a slot: the host is 0, guests 1-3. Guests only talk to the
    // host; the host handles every guest message itself and passes it on to the other guests, so everybody
    // hears everybody. Each message carries its sender's slot, and while one is being handled, From and
    // Remote/PeerName refer to that sender.
    public class NetSession
    {
        public enum State { Offline, WaitingForPartner, Connecting, Connected }

        private const float StateSendInterval = 1f / 30f; // 30 times a second
        private const float PingInterval = 1f;
        private const float Timeout = 20f;       // silence (not counting our own loading) before giving up
        private const float HelloRetry = 2f;     // reconnecting joiner: hello every 2 s
        private float lastUpdateAt;
        private float nextHelloAt;

        // Everyone else, by slot (our own slot's entry stays unused).
        public readonly RemotePlayerState[] Players = new RemotePlayerState[Protocol.MaxPlayers];
        public readonly LanDiscovery Discovery = new LanDiscovery();

        public State Status = State.Offline;

        // The slot of the player whose message is being handled right now, or -1.
        public int From = -1;

        // The other players in the game right now (rebuilt when someone joins or leaves).
        public readonly System.Collections.Generic.List<RemotePlayerState> Remotes = new System.Collections.Generic.List<RemotePlayerState>();

        private readonly RemotePlayerState nobody = new RemotePlayerState();

        public NetSession()
        {
            for (int i = 0; i < Players.Length; i++)
            {
                Players[i] = new RemotePlayerState();
                Players[i].Slot = i;
            }
        }

        public int LocalSlot
        {
            get { return Protocol.LocalSlot; }
        }

        // While handling a message: its sender. Otherwise the first other player (the host, for a guest).
        public RemotePlayerState Remote
        {
            get
            {
                if (From >= 0 && From < Players.Length && From != LocalSlot)
                    return Players[From];
                return Remotes.Count > 0 ? Remotes[0] : nobody;
            }
        }

        public string PeerName
        {
            get { return Remote.Name; }
        }

        // Ping to the host (guest), or the worst ping among the guests (host).
        public float PingMs
        {
            get
            {
                float worst = -1f;
                foreach (RemotePlayerState p in Remotes)
                {
                    if (p.PingMs > worst)
                        worst = p.PingMs;
                }
                return worst;
            }
        }

        // "Alex", "Alex and Sam", "Alex, Sam and Kim".
        public string RemoteNames()
        {
            string names = "";
            for (int i = 0; i < Remotes.Count; i++)
            {
                if (i > 0)
                    names += i == Remotes.Count - 1 ? " and " : ", ";
                names += Remotes[i].Name;
            }
            return names;
        }

        private void RebuildRemotes()
        {
            Remotes.Clear();
            for (int i = 0; i < Players.Length; i++)
            {
                if (i != LocalSlot && Players[i].Connected)
                    Remotes.Add(Players[i]);
            }
        }

        // Lobby rule set by the host: can the players' bullets hurt each other?
        // The host's value comes from its PvP setting; a joiner gets it from the host.
        private bool joinerPvp;
        public bool PvpEnabled
        {
            get
            {
                if (Link == null)
                    return false;
                return Link.IsHost ? Plugin.Pvp.Value : joinerPvp;
            }
        }

        // Lobby rule set by the host: a boss's money counts 1/players while online (the purse is shared and the
        // boss drops money in every game). See LootSync.
        private bool joinerHalfMoney = true;
        public bool HalfMoney
        {
            get
            {
                if (Link == null)
                    return false;
                return Link.IsHost ? Plugin.HalfMoney.Value : joinerHalfMoney;
            }
        }

        // Lobby rule set by the host: when someone starts a fight, everyone else is brought into that room.
        private bool joinerFightPull = true;
        public bool FightPull
        {
            get
            {
                if (Link == null)
                    return false;
                return Link.IsHost ? Plugin.TeleportToFights.Value : joinerFightPull;
            }
        }

        // Host: tell the partner the current rules (on connect and whenever they change).
        public void SendRules()
        {
            if (Link == null || !Link.IsHost)
                return;
            bool pvp = Plugin.Pvp.Value;
            bool halfMoney = Plugin.HalfMoney.Value;
            bool fightPull = Plugin.TeleportToFights.Value;
            float healthFactor = DifficultySync.Factor;
            SendReliable(MsgType.Rules, delegate (BinaryWriter w) { w.Write(pvp); w.Write(halfMoney); w.Write(fightPull); w.Write(healthFactor); });
        }

        // The link in use right now, or null when offline.
        public Transport Link;
        // Created once Steam is ready; null if the game isn't running through Steam.
        public SteamLink Steam;

        private float lastReceivedAt;
        private float nextPingAt;
        private float nextStateAt;

        public bool SteamAvailable { get { return Steam != null; } }

        public void InitSteam()
        {
            SteamLobby lobby = new SteamLobby();
            lobby.Init();
            // Accepting a Steam invite can happen at any time, even while on LAN.
            // Subscribed before SteamLink is created so we switch links before it reports "ready".
            lobby.OnEntered += delegate
            {
                if (Link != null && Link != Steam)
                    Leave();
                Use(Steam);
                if (lobby.IsHost)
                    Status = State.WaitingForPartner;
                else
                    MultiplayerMenu.EnterBreachFromTitle(); // accepted an invite on the title screen
            };
            Steam = new SteamLink(lobby);
        }

        public void HostSteam()
        {
            if (Steam != null && Link == null)
                Steam.Lobby.Host();
        }

        public void JoinSteamLobby(CSteamID lobby)
        {
            if (Steam != null)
                Steam.Lobby.Join(lobby);
        }

        public bool HostLan(int port)
        {
            if (Link != null)
                return false;
            try
            {
                Use(LanLink.Host(port));
                Status = State.WaitingForPartner;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not host on LAN port " + port + ": " + e.Message);
                return false;
            }
        }

        public bool JoinLan(IPEndPoint host)
        {
            if (Link != null)
                return false;
            try
            {
                LanLink link = LanLink.Join(host);
                Use(link);
                link.Start();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not join LAN game at " + host + ": " + e.Message);
                return false;
            }
        }

        public void Leave()
        {
            if (Link == null)
                return;
            if (Status == State.Connected)
                Link.Send(Protocol.Build(MsgType.Bye, null), true);
            Link.Close();
            Link = null;
            ResetAll();
            Status = State.Offline;
        }

        public void Update()
        {
            // Steam packets are drained even when offline so stale ones don't pile up.
            if (Steam != null)
                Steam.Poll();
            if (Link != null && Link != Steam)
                Link.Poll();
            Discovery.Poll();

            if (Status != State.Connecting && Status != State.Connected)
                return;

            float now = Time.realtimeSinceStartup;

            // Loading a floor freezes the game for several seconds. That silence isn't the partner being gone:
            // don't count time while we're loading, or a long gap between frames (our own freeze).
            bool loading = GameManager.HasInstance && GameManager.Instance.IsLoadingLevel;
            if (loading || now - lastUpdateAt > 1f)
            {
                lastReceivedAt += now - lastUpdateAt;
                foreach (RemotePlayerState p in Players)
                    p.LastHeard += now - lastUpdateAt;
            }
            lastUpdateAt = now;
            if (Link.IsHost)
            {
                // Each guest times out on its own; the others play on.
                for (int i = 1; i < Players.Length; i++)
                {
                    if (Players[i].Connected && now - Players[i].LastHeard > Timeout)
                        Disconnect(i, "timed out");
                }
                if (Link == null)
                    return;
            }
            else if (now - lastReceivedAt > Timeout)
            {
                Disconnect(0, "timed out");
                return;
            }

            // A Steam joiner that lost contact stays in the lobby and keeps saying hello until the host answers.
            if (Status == State.Connecting && Link != null && !Link.IsHost && now >= nextHelloAt)
            {
                nextHelloAt = now + HelloRetry;
                SendHello();
            }

            if (Status != State.Connected)
                return;

            if (now >= nextPingAt)
            {
                nextPingAt = now + PingInterval;
                Link.Send(Protocol.Build(MsgType.Ping, delegate (BinaryWriter w) { w.Write(now); }), false);
            }

            if (now >= nextStateAt)
            {
                nextStateAt = now + StateSendInterval;
                SendPlayerState();
            }
        }

        private void Use(Transport link)
        {
            if (Link == link)
                return;
            Link = link;
            link.OnPacket = HandlePacket;
            link.OnReady = delegate
            {
                // Joiner: the host is reachable, so introduce ourselves.
                Status = State.Connecting;
                lastReceivedAt = Time.realtimeSinceStartup;
                SendHello();
            };
            link.OnLost = Disconnect;
            Protocol.LocalSlot = link.IsHost ? (byte)0 : Protocol.NoSlot;
        }

        private readonly MemoryStream recvStream = new MemoryStream(2048);
        private BinaryReader recvReaderField;
        private BinaryReader recvReader
        {
            get
            {
                if (recvReaderField == null)
                    recvReaderField = new BinaryReader(recvStream);
                return recvReaderField;
            }
        }

        // Messages that are only between a guest and the host, never passed on to the other guests.
        private static bool HostOnly(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello:
                case MsgType.Ping:
                case MsgType.Pong:
                case MsgType.Bye:
                case MsgType.Inventory:
                case MsgType.InventoryRequest:
                case MsgType.AtExit:
                case MsgType.RestartRequest:
                case MsgType.Wallet:          // guests' purse changes go to the host, which sends everyone the total
                    return true;
            }
            return false;
        }

        // Messages sent unreliably (frequent updates): passed on the same way.
        private static bool Unreliable(MsgType type)
        {
            switch (type)
            {
                case MsgType.PlayerState:
                case MsgType.Shot:
                case MsgType.EnemyStates:
                case MsgType.Orbitals:
                case MsgType.Companions:
                case MsgType.Courses:
                case MsgType.Beams:
                case MsgType.Reload:
                    return true;
            }
            return false;
        }

        private void HandlePacket(int linkSlot, byte[] data, int offset, int length)
        {
            if (length < 2 || Link == null)
                return;

            float arrived = Time.realtimeSinceStartup;
            lastReceivedAt = arrived;
            MsgType type = (MsgType)data[offset];
            NetStats.Received(type, length);
            int from;
            if (Link.IsHost)
            {
                // A guest can only speak for itself: its slot is the one the link knows it by.
                from = linkSlot;
                data[offset + 1] = (byte)linkSlot;
                Players[from].LastHeard = arrived;
                // Pass it on to the other guests (only once that guest has said hello).
                if (Players[from].Connected && !HostOnly(type))
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(data, offset, copy, 0, length);
                    bool reliable = !Unreliable(type);
                    for (int i = 1; i < Players.Length; i++)
                    {
                        if (i != from && Players[i].Connected)
                        {
                            NetStats.Sent(type, copy.Length, 1);
                            Link.Send(i, copy, reliable);
                        }
                    }
                }
                else if (!Players[from].Connected && type != MsgType.Hello)
                {
                    // Nothing but a hello until they've introduced themselves. A player we dropped (timed out)
                    // keeps playing as if connected: ask them to say hello again.
                    if (arrived >= helloAskedAt[from] + 1f)
                    {
                        helloAskedAt[from] = arrived;
                        Plugin.Log.LogInfo("Player " + (from + 1) + " is talking to us without having said hello: asking them to reconnect.");
                        SendHello(from, true);
                    }
                    return;
                }
            }
            else
            {
                from = data[offset + 1];
                if (from >= Players.Length || from == LocalSlot)
                    return;
            }

            // One reusable reader for incoming messages (a new one per message made a lot of garbage).
            recvStream.SetLength(0);
            recvStream.Write(data, offset + 2, length - 2);
            recvStream.Position = 0;
            BinaryReader r = recvReader;
            int outerFrom = From;
            From = from;
            try
            {
                Dispatch(type, r, from);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Error handling " + type + " from player " + from + ": " + e);
            }
            finally
            {
                From = outerFrom;
            }
        }

        private readonly float[] helloAskedAt = new float[Protocol.MaxPlayers];

        // While set, Send* go only to this slot (the host greeting one newcomer). -1 = everyone.
        private int onlyTo = Transport.Everyone;

        private void Dispatch(MsgType type, BinaryReader r, int from)
        {
            {
                switch (type)
                {
                    case MsgType.Hello:
                        int version = r.ReadInt32();
                        string name = r.ReadString();
                        byte yourSlot = r.ReadByte();
                        bool pleaseIntroduce = r.ReadBoolean();
                        if (version != Protocol.Version)
                        {
                            Plugin.Log.LogError(name + " runs protocol " + version + ", we run " + Protocol.Version + ".");
                            Disconnect(from, "version mismatch");
                            return;
                        }
                        if (Link.IsHost)
                        {
                            bool isNew = !Players[from].Connected;
                            StandIn.OnHello(from, name);
                            Players[from].Name = name;
                            Players[from].Connected = true;
                            Players[from].LastHeard = Time.realtimeSinceStartup;
                            RebuildRemotes();
                            Status = State.Connected;
                            // The host answers every hello: a guest reconnecting after a timeout needs the reply
                            // even if the host never noticed the gap. Everything sent while greeting goes to that
                            // guest only.
                            onlyTo = from;
                            try
                            {
                                SendHello(from);
                                SendRules();
                                SaveSync.SendProgress(isNew); // a newcomer always gets it, even right after another
                                LoopSync.SendState(true);
                                LevelSync.OnConnected();
                                InventorySync.OnConnected();
                                FloorCheck.OnConnected();
                                ModSettingsSync.OnConnected();
                            }
                            finally
                            {
                                onlyTo = Transport.Everyone;
                            }
                            SendRoster();
                            if (isNew)
                            {
                                Plugin.Log.LogInfo(name + " joined over " + Link.Kind + " (player " + (from + 1) + ")!");
                                Toast.Show(name + " joined the game.", 4f);
                            }
                        }
                        else
                        {
                            if (yourSlot == Protocol.NoSlot || yourSlot == 0 || yourSlot >= Players.Length)
                            {
                                Plugin.Log.LogWarning("Host's hello gave no slot; ignoring.");
                                return;
                            }
                            if (pleaseIntroduce)
                            {
                                // The host lost us (we timed out on its side) and asks us to join again.
                                Plugin.Log.LogInfo("The host asks us to reconnect.");
                                SendHello();
                            }
                            Protocol.LocalSlot = yourSlot;
                            Players[0].Name = name;
                            Players[0].Connected = true;
                            Players[yourSlot].Reset();
                            RebuildRemotes();
                            Status = State.Connected;
                            Plugin.Log.LogInfo("Connected to " + name + " over " + Link.Kind + " as player " + (yourSlot + 1) + "!");
                        }
                        break;

                    case MsgType.Roster:
                        if (!Link.IsHost)
                            ReadRoster(r);
                        break;

                    case MsgType.Ping:
                        float sentAt = r.ReadSingle();
                        Link.Send(from, Protocol.Build(MsgType.Pong, delegate (BinaryWriter w) { w.Write(sentAt); }), false);
                        break;

                    case MsgType.Pong:
                        Players[from].PingMs = (Time.realtimeSinceStartup - r.ReadSingle()) * 1000f;
                        break;

                    case MsgType.PlayerState:
                        ReadPlayerState(r, Players[from]);
                        break;

                    case MsgType.Bye:
                        Disconnect(from, Players[from].Name + " left");
                        break;

                    case MsgType.LevelLoad:
                        if (!Link.IsHost)
                            LevelSync.OnLevelLoad(r);
                        break;
                    case MsgType.EndRun:
                        if (!Link.IsHost)
                            LevelSync.OnEndRun();
                        break;
                    case MsgType.PlayerDied:
                        DeathSync.OnPartnerDied();
                        break;

                    case MsgType.Revive:
                        DeathSync.OnRevive(r);
                        break;

                    case MsgType.TeamWiped:
                        DeathSync.OnTeamWiped();
                        break;


                    case MsgType.Rules:
                        if (!Link.IsHost)
                        {
                            joinerPvp = r.ReadBoolean();
                            joinerHalfMoney = r.ReadBoolean();
                            joinerFightPull = r.ReadBoolean();
                            DifficultySync.SetFromHost(r.ReadSingle());
                            Plugin.Log.LogInfo("Host rules: PvP " + (joinerPvp ? "on" : "off") + ", split money " + (joinerHalfMoney ? "on" : "off")
                                + ", teleport to fights " + (joinerFightPull ? "on" : "off"));
                        }
                        break;
                    case MsgType.EnemyDamage:
                        EnemySync.OnEnemyDamage(r);
                        break;

                    case MsgType.EnemyDied:
                        EnemySync.OnEnemyDied(r);
                        break;

                    case MsgType.EnemyStates:
                        EnemySync.OnEnemyStates(r);
                        break;
                    case MsgType.AttackStart:
                        AttackSync.OnAttackStart(r);
                        break;
                    case MsgType.PullGhost:
                        EnemySync.OnPullGhost(r);
                        break;
                    case MsgType.Throw:
                        ThrowSync.OnThrow(r);
                        break;
                    case MsgType.PetStart:
                        PetSync.OnPetStart(r);
                        break;

                    case MsgType.PetStop:
                        PetSync.OnPetStop(r);
                        break;
                    case MsgType.Blank:
                        BlankSync.OnBlank(r);
                        break;
                    case MsgType.RoomCleared:
                        EnemySync.OnRoomCleared(r);
                        break;

                    case MsgType.Paused:
                        PauseSync.OnPaused(r);
                        break;
                    case MsgType.Break:
                        WorldSync.OnBreak(r);
                        break;

                    case MsgType.TableFlip:
                        WorldSync.OnTableFlip(r);
                        break;

                    case MsgType.TablePositions:
                        WorldSync.OnTablePositions(r);
                        break;
                    case MsgType.Kick:
                        WorldSync.OnKick(r);
                        break;









                    case MsgType.Shot:
                        BulletSync.OnShot(r);
                        break;


                    case MsgType.ChestOpened:
                        LootSync.OnChestOpened(r);
                        break;

                    case MsgType.HitInRoom:
                        LootSync.OnHitInRoom(r);
                        break;

                    case MsgType.GunDropped:
                        LootSync.OnGunDropped(r);
                        break;

                    case MsgType.GunTaken:
                        LootSync.OnGunTaken(r);
                        break;

                    case MsgType.ShopItemGone:
                        LootSync.OnShopItemGone(r);
                        break;

                    case MsgType.Wallet:
                        LootSync.OnWallet(r);
                        break;

                    case MsgType.Companions:
                        CompanionSync.OnCompanions(r);
                        break;
                    case MsgType.Motion:
                        BulletSync.OnMotion(r);
                        break;
                    case MsgType.EnemyEffect:
                        EffectSync.OnEnemyEffect(r);
                        break;
                    case MsgType.LoopStart:
                        if (!Link.IsHost)
                            LoopSync.OnLoopStart(r);
                        break;
                    case MsgType.LoopState:
                        if (!Link.IsHost)
                            LoopSync.OnLoopState(r);
                        break;

                    case MsgType.Orbitals:
                        OrbitalSync.OnOrbitals(r);
                        break;

                    case MsgType.Inventory:
                        if (Link.IsHost)
                            InventorySync.OnInventory(r);
                        break;

                    case MsgType.InventoryRequest:
                        if (Link.IsHost)
                            InventorySync.OnInventoryRequest(r);
                        break;

                    case MsgType.InventoryRestore:
                        if (!Link.IsHost)
                            InventorySync.OnInventoryRestore(r);
                        break;

                    case MsgType.RejoinOffer:
                        if (!Link.IsHost)
                            InventorySync.OnRejoinOffer(r);
                        break;

                    case MsgType.FloorPrint:
                        if (!Link.IsHost)
                            FloorCheck.OnFloorPrint(r);
                        break;

                    case MsgType.Reload:
                        BulletSync.OnReload(r);
                        break;

                    case MsgType.Courses:
                        BulletSync.OnCourses(r);
                        break;

                    case MsgType.Beams:
                        BeamSync.OnBeams(r);
                        break;

                    case MsgType.RestartRequest:
                        if (Link.IsHost)
                            LevelSync.OnRestartRequest();
                        break;

                    case MsgType.WalletState:
                        if (!Link.IsHost)
                            LootSync.OnWalletState(r);
                        break;

                    case MsgType.AtExit:
                        if (Link.IsHost)
                            LevelSync.OnPartnerAtExit();
                        break;

                    case MsgType.ModSettings:
                        if (!Link.IsHost)
                            ModSettingsSync.OnModSettings(r);
                        break;

                    case MsgType.Progress:
                        if (!Link.IsHost)
                            SaveSync.OnProgress(r);
                        break;

                    case MsgType.ReturnToFoyer:
                        if (!Link.IsHost)
                            LevelSync.OnReturnToFoyer();
                        break;
                }
            }
        }

        // For frequent updates where a lost one is replaced by the next. Ignored unless connected.
        public void SendUnreliable(MsgType type, Action<BinaryWriter> writeBody)
        {
            if (Status == State.Connected && Link != null)
                Link.Send(onlyTo, Counted(type, Protocol.Build(type, writeBody), onlyTo), false);
        }

        // Count a message for NetStats: sent once to one player, or to each other player.
        private byte[] Counted(MsgType type, byte[] packet, int to)
        {
            NetStats.Sent(type, packet.Length, to == Transport.Everyone ? Math.Max(1, Link.IsHost ? Remotes.Count : 1) : 1);
            return packet;
        }

        // For game events (level loads etc.) that must arrive. Ignored unless connected.
        public void SendReliable(MsgType type, Action<BinaryWriter> writeBody)
        {
            if (Status == State.Connected && Link != null)
                Link.Send(onlyTo, Counted(type, Protocol.Build(type, writeBody), onlyTo), true);
        }

        // Host: to one guest only (e.g. answering that guest). A guest can only reach the host.
        public void SendReliableTo(int slot, MsgType type, Action<BinaryWriter> writeBody)
        {
            if (Status == State.Connected && Link != null)
                Link.Send(Link.IsHost ? slot : 0, Counted(type, Protocol.Build(type, writeBody), slot), true);
        }

        // Guest: hello to the host. Host: hello back to one guest, telling it its slot.
        // askBack: the host asks a player it doesn't know (any more) to say hello.
        private void SendHello(int toSlot = 0, bool askBack = false)
        {
            string myName = Plugin.DisplayName();
            byte slotForThem = Link.IsHost ? (byte)toSlot : Protocol.NoSlot;
            Link.Send(Link.IsHost ? toSlot : 0, Protocol.Build(MsgType.Hello, delegate (BinaryWriter w)
            {
                w.Write(Protocol.Version);
                w.Write(myName);
                w.Write(slotForThem);
                w.Write(askBack);
            }), true);
        }

        // Host -> every guest: who is in the game.
        private void SendRoster()
        {
            if (Link == null || !Link.IsHost)
                return;
            SendRules(); // the enemy health multiplier follows the number of players
            string hostName = Plugin.DisplayName();
            Link.Send(Protocol.Build(MsgType.Roster, delegate (BinaryWriter w)
            {
                int standIn = StandIn.Slot;
                int count = 1;
                for (int i = 1; i < Players.Length; i++)
                {
                    if (Players[i].Connected || i == standIn)
                        count++;
                }
                w.Write((byte)count);
                w.Write((byte)0);
                w.Write(hostName);
                for (int i = 1; i < Players.Length; i++)
                {
                    if (!Players[i].Connected && i != standIn)
                        continue;
                    w.Write((byte)i);
                    w.Write(Players[i].Connected ? Players[i].Name : StandIn.Name);
                }
            }), true);
        }

        private void ReadRoster(BinaryReader r)
        {
            int count = r.ReadByte();
            bool[] present = new bool[Players.Length];
            for (int i = 0; i < count; i++)
            {
                int slot = r.ReadByte();
                string name = r.ReadString();
                if (slot >= Players.Length)
                    continue;
                present[slot] = true;
                if (slot == LocalSlot)
                    continue;
                if (!Players[slot].Connected)
                {
                    Plugin.Log.LogInfo(name + " is in the game (player " + (slot + 1) + ").");
                    if (slot != 0)
                        Toast.Show(name + " joined the game.", 4f);
                }
                Players[slot].Connected = true;
                Players[slot].Name = name;
            }
            for (int i = 1; i < Players.Length; i++)
            {
                if (i != LocalSlot && !present[i] && Players[i].Connected)
                {
                    Plugin.Log.LogInfo(Players[i].Name + " left the game.");
                    Toast.Show(Players[i].Name + " left the game.", 4f);
                    Players[i].Reset();
                }
            }
            RebuildRemotes();
        }

        private void SendPlayerState()
        {
            SendPlayerStateOf(GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null, Autopilot.Active);
        }

        // Host: a stand-in's state, as the slot of the player it plays for.
        public void SendStateAs(int slot, PlayerController player, bool onBreak)
        {
            if (Link == null || !Link.IsHost || player == null)
                return;
            Protocol.SendAs = slot;
            try
            {
                SendPlayerStateOf(player, onBreak);
            }
            finally
            {
                Protocol.SendAs = -1;
            }
        }

        public void SendRosterNow()
        {
            SendRoster();
        }

        private void SendPlayerStateOf(PlayerController player, bool onBreakNow)
        {
            bool hasPlayer = player != null;
            Vector2 pos = hasPlayer ? player.CenterPosition : Vector2.zero;
            Vector2 aim = hasPlayer ? (Vector2)player.unadjustedAimPoint : Vector2.zero;
            string scene = LevelSync.CurrentLevelKey();

            int identity = hasPlayer ? (int)player.characterIdentity : 0;
            bool altCostume = hasPlayer && player.IsUsingAlternateCostume;
            string customCharacter = CustomCharacterSupport.LocalName(player);
            bool visible = false;
            string clip = "";
            int frame = 0;
            bool flip = false;
            Vector2 feet = Vector2.zero;
            if (hasPlayer && player.sprite != null)
            {
                visible = player.sprite.renderer != null && player.sprite.renderer.enabled;
                flip = player.sprite.FlipX;
                feet = player.sprite.WorldBottomCenter;
                if (player.spriteAnimator != null && player.spriteAnimator.CurrentClip != null)
                {
                    clip = player.spriteAnimator.CurrentClip.name;
                    frame = player.spriteAnimator.CurrentFrame;
                }
            }

            // The gun in hand: which gun, its current frame, and where it sits relative to the feet.
            bool hasGun = false;
            int gunId = -1, gunSpriteId = 0;
            Vector2 gunOffset = Vector2.zero;
            float gunAngle = 0f, gunHeight = 0f;
            bool gunFlipY = false;
            Gun gun = hasPlayer ? player.CurrentGun : null;
            if (gun != null && gun.sprite != null && gun.gameObject.activeInHierarchy
                && gun.sprite.renderer != null && gun.sprite.renderer.enabled)
            {
                hasGun = true;
                gunId = gun.PickupObjectId;
                gunSpriteId = gun.sprite.spriteId;
                Transform t = gun.sprite.transform;
                gunOffset = (Vector2)t.position - feet;
                gunAngle = t.eulerAngles.z;
                gunFlipY = gun.sprite.FlipY;
                gunHeight = gun.sprite.HeightOffGround;
            }

            // The blob shadow under their feet.
            bool hasShadow = false;
            Vector3 shadowOffset = Vector3.zero;
            Renderer shadowRenderer = hasPlayer && player.ShadowObject != null && player.ShadowObject.activeInHierarchy
                ? player.ShadowObject.GetComponentInChildren<Renderer>() : null;
            if (shadowRenderer != null && shadowRenderer.enabled)
            {
                // The middle of what's actually drawn, not the object's anchor point.
                hasShadow = true;
                Vector3 sc = shadowRenderer.bounds.center;
                shadowOffset = new Vector3(sc.x - feet.x, sc.y - feet.y, shadowRenderer.transform.position.z - player.sprite.transform.position.z);
            }
            // Our companion (e.g. the Hunter's dog), if one is out and visible.
            AIActor pet = null;
            if (hasPlayer && player.companions != null)
            {
                foreach (AIActor c in player.companions)
                {
                    if (c != null && c.sprite != null && c.gameObject.activeInHierarchy && c.sprite.renderer != null && c.sprite.renderer.enabled)
                    {
                        pet = c;
                        break;
                    }
                }
            }
            bool hasPet = pet != null;
            string petGuid = hasPet ? pet.EnemyGuid : "";
            string petClip = "";
            int petFrame = 0;
            if (hasPet && pet.spriteAnimator != null && pet.spriteAnimator.CurrentClip != null)
            {
                petClip = pet.spriteAnimator.CurrentClip.name;
                petFrame = pet.spriteAnimator.CurrentFrame;
            }
            bool petFlip = hasPet && pet.sprite.FlipX;
            Vector2 petFeet = hasPet ? pet.sprite.WorldBottomCenter : Vector2.zero;

            Vector2 rootOffset = hasPlayer ? (Vector2)player.transform.position - feet : Vector2.zero;
            PlayerLook look = PlayerLook.Read(player);
            bool isDead = hasPlayer && (player.IsGhost || (player.healthHaver != null && player.healthHaver.IsDead));
            bool onBreak = onBreakNow;
            HealthHaver hh = hasPlayer ? player.healthHaver : null;
            float health = hh != null ? hh.GetCurrentHealth() : 0f;
            int blanks = hasPlayer ? Mathf.Clamp(player.Blanks, 0, 255) : 0;
            float maxHealth = hh != null ? hh.GetMaxHealth() : 0f;
            float armor = hh != null ? hh.Armor : 0f;
            float now = Time.realtimeSinceStartup;

            // Compact: what rarely changes (level, character, health, looks, shadow, companion kind) only when it
            // changed or once a second; animation names when they change or twice a second; positions as
            // half-pixel steps. The level's short code goes every time, so a level change is never missed.
            int cacheSlot = Mathf.Clamp(Protocol.SendAs >= 0 ? Protocol.SendAs : Protocol.LocalSlot, 0, Protocol.MaxPlayers - 1);
            SentState cache = sentStates[cacheSlot];
            string slowKey = scene + "|" + identity + "|" + altCostume + "|" + customCharacter + "|" + health + "|" + maxHealth + "|" + armor + "|" + blanks
                + "|" + look.SpecialFlags + "|" + look.FlatColor + "|" + look.OverrideColor + "|" + look.OnFire + "|" + Protocol.Q(rootOffset.x)
                + "," + Protocol.Q(rootOffset.y) + "|" + hasShadow + "|" + shadowOffset + "|" + petGuid;
            bool slow = slowKey != cache.SlowKey || now - cache.SlowAt > 1f;
            bool clips = clip != cache.Clip || petClip != cache.PetClip || now - cache.ClipAt > 0.5f;
            if (slow)
            {
                cache.SlowKey = slowKey;
                cache.SlowAt = now;
            }
            if (clips)
            {
                cache.Clip = clip;
                cache.PetClip = petClip;
                cache.ClipAt = now;
            }
            byte flags = (byte)((hasPlayer ? 1 : 0) | (slow ? 2 : 0) | (clips ? 4 : 0) | (visible ? 8 : 0) | (flip ? 16 : 0)
                | (isDead ? 32 : 0) | (onBreak ? 64 : 0) | (hasPet ? 128 : 0));
            int sceneCode = Protocol.Hash(scene);

            Link.Send(Counted(MsgType.PlayerState, Protocol.Build(MsgType.PlayerState, delegate (BinaryWriter w)
            {
                w.Write(now);
                w.Write(flags);
                w.Write(sceneCode);
                Protocol.WriteQ(w, pos);
                Protocol.WriteQ(w, aim);
                Protocol.WriteQ(w, feet);
                w.Write((byte)Mathf.Clamp(frame, 0, 255));
                if (slow)
                {
                    w.Write(scene);
                    w.Write(identity);
                    w.Write(altCostume);
                    w.Write(customCharacter);
                    w.Write(health); w.Write(maxHealth); w.Write(armor);
                    w.Write((byte)blanks);
                    w.Write(look.SpecialFlags.x); w.Write(look.SpecialFlags.y); w.Write(look.SpecialFlags.z); w.Write(look.SpecialFlags.w);
                    WriteColor(w, look.FlatColor);
                    WriteColor(w, look.OverrideColor);
                    w.Write(look.OnFire);
                    Protocol.WriteQ(w, rootOffset);
                    w.Write(hasShadow);
                    if (hasShadow)
                    {
                        w.Write(shadowOffset.x); w.Write(shadowOffset.y); w.Write(shadowOffset.z);
                    }
                    w.Write(petGuid);
                }
                if (clips)
                {
                    w.Write(clip);
                    w.Write(petClip);
                }
                w.Write((byte)((hasGun ? 1 : 0) | (gunFlipY ? 2 : 0)));
                if (hasGun)
                {
                    w.Write(gunId);
                    w.Write((ushort)Mathf.Clamp(gunSpriteId, 0, 65535));
                    Protocol.WriteQ(w, gunOffset);
                    w.Write((short)Mathf.RoundToInt(Mathf.Repeat(gunAngle, 360f) * 10f));
                    w.Write(Protocol.Q(gunHeight));
                }
                if (hasPet)
                {
                    w.Write((byte)Mathf.Clamp(petFrame, 0, 255));
                    w.Write(petFlip);
                    Protocol.WriteQ(w, petFeet);
                }
            }), Transport.Everyone), false);
        }

        private static void WriteColor(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }

        // What we last sent of each player we speak for (us, or the host's stand-in): see SendPlayerStateOf.
        private class SentState
        {
            public string SlowKey = "";
            public float SlowAt = -100f;
            public string Clip = "", PetClip = "";
            public float ClipAt = -100f;
        }
        private readonly SentState[] sentStates = NewSentStates();

        private static SentState[] NewSentStates()
        {
            SentState[] s = new SentState[Protocol.MaxPlayers];
            for (int i = 0; i < s.Length; i++)
                s[i] = new SentState();
            return s;
        }

        private static Color ReadColor(BinaryReader r)
        {
            return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private void ReadPlayerState(BinaryReader r, RemotePlayerState Remote)
        {
            float sentAt = r.ReadSingle();
            byte flags = r.ReadByte();
            int sceneCode = r.ReadInt32();
            bool hasPlayer = (flags & 1) != 0, slow = (flags & 2) != 0, clips = (flags & 4) != 0, visible = (flags & 8) != 0;
            bool flipX = (flags & 16) != 0, isDead = (flags & 32) != 0, onBreak = (flags & 64) != 0, hasPet = (flags & 128) != 0;
            Vector2 center = Protocol.ReadQ(r);
            Vector2 aim = Protocol.ReadQ(r);
            Vector2 feet = Protocol.ReadQ(r);
            int frame = r.ReadByte();
            // What wasn't sent this time is what we already have.
            string scene = Remote.Scene;
            int identity = Remote.Identity;
            bool altCostume = Remote.AltCostume;
            string customCharacter = Remote.CustomCharacter;
            float health = Remote.Health, maxHealth = Remote.MaxHealth, armor = Remote.Armor;
            int blanks = Remote.Blanks;
            PlayerLook look = Remote.Look;
            Vector2 rootOffset = Remote.RootOffset;
            bool hasShadow = Remote.HasShadow;
            Vector3 shadowOffset = Remote.ShadowOffset;
            string petGuid = Remote.PetGuid;
            string clip = Remote.Clip, petClip = Remote.PetClip;
            if (slow)
            {
                scene = r.ReadString();
                identity = r.ReadInt32();
                altCostume = r.ReadBoolean();
                customCharacter = r.ReadString();
                health = r.ReadSingle(); maxHealth = r.ReadSingle(); armor = r.ReadSingle();
                blanks = r.ReadByte();
                look = new PlayerLook();
                look.SpecialFlags = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                look.FlatColor = ReadColor(r);
                look.OverrideColor = ReadColor(r);
                look.OnFire = r.ReadBoolean();
                rootOffset = Protocol.ReadQ(r);
                hasShadow = r.ReadBoolean();
                shadowOffset = hasShadow ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : Vector3.zero;
                petGuid = r.ReadString();
            }
            if (clips)
            {
                clip = r.ReadString();
                petClip = r.ReadString();
            }
            byte gunFlags = r.ReadByte();
            bool hasGun = (gunFlags & 1) != 0, gunFlipY = (gunFlags & 2) != 0;
            int gunId = -1, gunSpriteId = 0;
            Vector2 gunOffset = Vector2.zero;
            float gunAngle = 0f, gunHeight = 0f;
            if (hasGun)
            {
                gunId = r.ReadInt32();
                gunSpriteId = r.ReadUInt16();
                gunOffset = Protocol.ReadQ(r);
                gunAngle = r.ReadInt16() / 10f;
                gunHeight = Protocol.DQ(r.ReadInt16());
            }
            int petFrame = 0;
            bool petFlip = false;
            Vector2 petFeet = Vector2.zero;
            if (hasPet)
            {
                petFrame = r.ReadByte();
                petFlip = r.ReadBoolean();
                petFeet = Protocol.ReadQ(r);
            }
            // A new level we haven't had the details of yet (lost or not due): wait for them (at most a second).
            if (Protocol.Hash(scene) != sceneCode)
                return;

            // A level change means the old positions mean nothing: start the motion history over.
            if (scene != Remote.Scene)
                Remote.Motion.Clear();
            Remote.Motion.Add(sentAt, feet);
            if (scene != Remote.Scene || !hasPet)
                Remote.PetMotion.Clear();
            if (hasPet)
                Remote.PetMotion.Add(sentAt, petFeet);

            // Updates can arrive out of order; an older one must not undo a newer animation frame.
            if (sentAt < Remote.SentAt && scene == Remote.Scene)
                return;
            Remote.SentAt = sentAt;
            Remote.HasPlayer = hasPlayer;
            Remote.Scene = scene;
            Remote.Position = center;
            Remote.Aim = aim;
            Remote.Identity = identity;
            Remote.AltCostume = altCostume;
            Remote.CustomCharacter = customCharacter;
            Remote.Visible = visible;
            Remote.Clip = clip;
            Remote.Frame = frame;
            Remote.FlipX = flipX;
            Remote.FeetPosition = feet;
            Remote.IsDead = isDead;
            Remote.OnBreak = onBreak;
            Remote.Health = health;
            Remote.MaxHealth = maxHealth;
            Remote.Armor = armor;
            Remote.Blanks = blanks;
            Remote.Look = look;
            Remote.RootOffset = rootOffset;
            Remote.HasGun = hasGun;
            Remote.GunId = gunId;
            Remote.GunSpriteId = gunSpriteId;
            Remote.GunOffset = gunOffset;
            Remote.GunAngle = gunAngle;
            Remote.GunFlipY = gunFlipY;
            Remote.GunHeight = gunHeight;
            Remote.HasShadow = hasShadow;
            Remote.ShadowOffset = shadowOffset;
            Remote.HasPet = hasPet;
            Remote.PetGuid = petGuid;
            Remote.PetClip = petClip;
            Remote.PetFrame = petFrame;
            Remote.PetFlipX = petFlip;
            Remote.PetFeet = petFeet;
            Remote.ReceivedAt = Time.realtimeSinceStartup;
        }

        private void ResetAll()
        {
            // Don't leave a shared seed behind, or every later solo run would get the same dungeon.
            LevelSync.ResetSeed();
            foreach (RemotePlayerState p in Players)
                p.Reset();
            RebuildRemotes();
            Protocol.LocalSlot = 0;
        }

        private void Disconnect(int slot, string reason)
        {
            if (Link == null)
                return;

            if (Link.IsHost)
            {
                if (slot <= 0 || slot >= Players.Length)
                    return;
                bool wasThere = Players[slot].Connected;
                string name = Players[slot].Name;
                // What we last knew of them (where they stood), for their stand-in.
                RemotePlayerState last = new RemotePlayerState();
                last.HasPlayer = Players[slot].HasPlayer;
                last.Scene = Players[slot].Scene;
                last.FeetPosition = Players[slot].FeetPosition;
                last.RootOffset = Players[slot].RootOffset;
                last.IsDead = Players[slot].IsDead;
                Players[slot].Reset();
                Link.DropPeer(slot);
                RebuildRemotes();
                if (!wasThere)
                    return;
                Plugin.Log.LogWarning("Player " + (slot + 1) + " (" + name + ") disconnected: " + reason);
                Toast.Show(name + " left the game.", 4f);
                StandIn.OnGuestLeft(slot, name, last);
                SendRoster();
                // The host keeps its lobby / port open so friends can come (back). Mid-run the seed stays, so a
                // player who comes back finds the same run (and their remembered gear).
                if (Remotes.Count == 0)
                {
                    if (GameManager.HasInstance && GameManager.Instance.IsFoyer)
                        LevelSync.ResetSeed();
                    Status = State.WaitingForPartner;
                }
                return;
            }

            // A guest only cares about losing the host.
            if (slot != 0)
                return;
            Plugin.Log.LogWarning("Disconnected: " + reason);

            // Steam joiner, still in the lobby: keep trying instead of leaving (needed a new invite before).
            // The run's seed is kept so the next floor still matches when the connection comes back.
            if (Link == Steam && Steam.Lobby.InLobby && reason == "timed out")
            {
                Plugin.Log.LogInfo("Staying in the lobby and trying to reconnect...");
                foreach (RemotePlayerState p in Players)
                    p.Reset();
                RebuildRemotes();
                Status = State.Connecting;
                lastReceivedAt = Time.realtimeSinceStartup;
                nextHelloAt = 0f;
                return;
            }

            ResetAll();
            Link.Close();
            Link = null;
            Status = State.Offline;
        }
    }
}
