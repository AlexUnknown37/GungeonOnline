using System;
using System.IO;
using System.Net;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GungeonOnline
{
    // What we last heard about the other player.
    public class RemotePlayerState
    {
        public bool HasPlayer;
        public string Scene = "";
        public Vector2 Position;      // center of the character, used for the name tag
        public Vector2 Aim;
        public float ReceivedAt;

        // What the character looks like right now.
        public int Identity;          // PlayableCharacters (Pilot, Convict, ...)
        public bool AltCostume;
        public bool Visible;
        public string Clip = "";      // animation name, e.g. "run_right"
        public int Frame;             // frame within that animation
        public bool FlipX;
        public Vector2 FeetPosition;  // bottom-center of the sprite in world space (newest update)
        public float SentAt = -1f;    // partner's clock when the newest update was sent
        public readonly MotionBuffer Motion = new MotionBuffer(); // smoothed feet position over time

        public bool IsDead;           // a ghost waiting for a chest, or dead
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
    }

    // Runs one multiplayer session over whichever link is active (Steam or LAN):
    // says hello, measures ping, and swaps player positions.
    public class NetSession
    {
        public enum State { Offline, WaitingForPartner, Connecting, Connected }

        private const float StateSendInterval = 1f / 30f; // 30 times a second
        private const float PingInterval = 1f;
        private const float Timeout = 20f;       // silence (not counting our own loading) before giving up
        private const float HelloRetry = 2f;     // reconnecting joiner: hello every 2 s
        private float lastUpdateAt;
        private float nextHelloAt;

        public readonly RemotePlayerState Remote = new RemotePlayerState();
        public readonly LanDiscovery Discovery = new LanDiscovery();

        public State Status = State.Offline;
        public string PeerName = "";
        public float PingMs = -1f;

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

        // Lobby rule set by the host: money pickups count half while online (the purse is shared and both
        // games drop money for the same kills, so full value would earn about twice a solo run).
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

        // Host: tell the partner the current rules (on connect and whenever they change).
        public void SendRules()
        {
            if (Link == null || !Link.IsHost)
                return;
            bool pvp = Plugin.Pvp.Value;
            bool halfMoney = Plugin.HalfMoney.Value;
            SendReliable(MsgType.Rules, delegate (BinaryWriter w) { w.Write(pvp); w.Write(halfMoney); });
        }

        // The link in use right now, or null when offline.
        public Transport Link;
        // Created once Steam is ready; null if the game isn't running through Steam.
        public SteamLink Steam;

        private float lastReceivedAt;
        private float nextPingAt;
        private float nextStateAt;
        private bool sentHello;

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
            ResetPeer();
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
                lastReceivedAt += now - lastUpdateAt;
            lastUpdateAt = now;
            // The partner's own load also goes quiet; allow extra time right after either of us loaded.
            if (now - lastReceivedAt > Timeout)
            {
                Disconnect("timed out");
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
        }

        private void HandlePacket(byte[] data, int offset, int length)
        {
            if (length < 1 || Link == null)
                return;

            lastReceivedAt = Time.realtimeSinceStartup;
            MsgType type = (MsgType)data[offset];
            using (BinaryReader r = new BinaryReader(new MemoryStream(data, offset + 1, length - 1)))
            {
                switch (type)
                {
                    case MsgType.Hello:
                        int version = r.ReadInt32();
                        PeerName = r.ReadString();
                        if (version != Protocol.Version)
                        {
                            Plugin.Log.LogError(PeerName + " runs protocol " + version + ", we run " + Protocol.Version + ".");
                            Disconnect("version mismatch");
                            return;
                        }
                        // The host answers every hello: a joiner reconnecting after a timeout needs the reply even
                        // if the host never noticed the gap.
                        if (!sentHello || Link.IsHost)
                            SendHello();
                        Status = State.Connected;
                        SendRules();
                        SaveSync.SendProgress(false);
                        LevelSync.OnConnected();
                        Plugin.Log.LogInfo("Connected to " + PeerName + " over " + Link.Kind + "!");
                        break;

                    case MsgType.Ping:
                        float sentAt = r.ReadSingle();
                        Link.Send(Protocol.Build(MsgType.Pong, delegate (BinaryWriter w) { w.Write(sentAt); }), false);
                        break;

                    case MsgType.Pong:
                        PingMs = (Time.realtimeSinceStartup - r.ReadSingle()) * 1000f;
                        break;

                    case MsgType.PlayerState:
                        ReadPlayerState(r);
                        break;

                    case MsgType.Bye:
                        Disconnect(PeerName + " left");
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
                            Plugin.Log.LogInfo("Host rules: PvP " + (joinerPvp ? "on" : "off") + ", half money " + (joinerHalfMoney ? "on" : "off"));
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
                        PetSync.OnPetStop();
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
                Link.Send(Protocol.Build(type, writeBody), false);
        }

        // For game events (level loads etc.) that must arrive. Ignored unless connected.
        public void SendReliable(MsgType type, Action<BinaryWriter> writeBody)
        {
            if (Status == State.Connected && Link != null)
                Link.Send(Protocol.Build(type, writeBody), true);
        }

        private void SendHello()
        {
            sentHello = true;
            string myName = Plugin.DisplayName();
            Link.Send(Protocol.Build(MsgType.Hello, delegate (BinaryWriter w)
            {
                w.Write(Protocol.Version);
                w.Write(myName);
            }), true);
        }

        private void SendPlayerState()
        {
            PlayerController player = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool hasPlayer = player != null;
            Vector2 pos = hasPlayer ? player.CenterPosition : Vector2.zero;
            Vector2 aim = hasPlayer ? (Vector2)player.unadjustedAimPoint : Vector2.zero;
            string scene = LevelSync.CurrentLevelKey();

            int identity = hasPlayer ? (int)player.characterIdentity : 0;
            bool altCostume = hasPlayer && player.IsUsingAlternateCostume;
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
            float now = Time.realtimeSinceStartup;

            Link.Send(Protocol.Build(MsgType.PlayerState, delegate (BinaryWriter w)
            {
                w.Write(now);
                w.Write(hasPlayer);
                w.Write(scene);
                w.Write(pos.x); w.Write(pos.y);
                w.Write(aim.x); w.Write(aim.y);
                w.Write(identity);
                w.Write(altCostume);
                w.Write(visible);
                w.Write(clip);
                w.Write((byte)Mathf.Clamp(frame, 0, 255));
                w.Write(flip);
                w.Write(feet.x); w.Write(feet.y);
                w.Write(rootOffset.x); w.Write(rootOffset.y);
                w.Write(isDead);
                w.Write(look.SpecialFlags.x); w.Write(look.SpecialFlags.y); w.Write(look.SpecialFlags.z); w.Write(look.SpecialFlags.w);
                WriteColor(w, look.FlatColor);
                WriteColor(w, look.OverrideColor);
                w.Write(look.OnFire);
                w.Write(hasGun);
                if (hasGun)
                {
                    w.Write(gunId);
                    w.Write(gunSpriteId);
                    w.Write(gunOffset.x); w.Write(gunOffset.y);
                    w.Write(gunAngle);
                    w.Write(gunFlipY);
                    w.Write(gunHeight);
                }
                w.Write(hasShadow);
                if (hasShadow)
                {
                    w.Write(shadowOffset.x); w.Write(shadowOffset.y); w.Write(shadowOffset.z);
                }
                w.Write(hasPet);
                if (hasPet)
                {
                    w.Write(petGuid);
                    w.Write(petClip);
                    w.Write((byte)Mathf.Clamp(petFrame, 0, 255));
                    w.Write(petFlip);
                    w.Write(petFeet.x); w.Write(petFeet.y);
                }
            }), false);
        }

        private static void WriteColor(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }

        private static Color ReadColor(BinaryReader r)
        {
            return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private void ReadPlayerState(BinaryReader r)
        {
            float sentAt = r.ReadSingle();
            bool hasPlayer = r.ReadBoolean();
            string scene = r.ReadString();
            Vector2 center = new Vector2(r.ReadSingle(), r.ReadSingle());
            Vector2 aim = new Vector2(r.ReadSingle(), r.ReadSingle());
            int identity = r.ReadInt32();
            bool altCostume = r.ReadBoolean();
            bool visible = r.ReadBoolean();
            string clip = r.ReadString();
            int frame = r.ReadByte();
            bool flipX = r.ReadBoolean();
            Vector2 feet = new Vector2(r.ReadSingle(), r.ReadSingle());
            Vector2 rootOffset = new Vector2(r.ReadSingle(), r.ReadSingle());
            bool isDead = r.ReadBoolean();
            PlayerLook look = new PlayerLook();
            look.SpecialFlags = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            look.FlatColor = ReadColor(r);
            look.OverrideColor = ReadColor(r);
            look.OnFire = r.ReadBoolean();
            bool hasGun = r.ReadBoolean();
            int gunId = -1, gunSpriteId = 0;
            Vector2 gunOffset = Vector2.zero;
            float gunAngle = 0f, gunHeight = 0f;
            bool gunFlipY = false;
            if (hasGun)
            {
                gunId = r.ReadInt32();
                gunSpriteId = r.ReadInt32();
                gunOffset = new Vector2(r.ReadSingle(), r.ReadSingle());
                gunAngle = r.ReadSingle();
                gunFlipY = r.ReadBoolean();
                gunHeight = r.ReadSingle();
            }
            bool hasShadow = r.ReadBoolean();
            Vector3 shadowOffset = hasShadow ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : Vector3.zero;
            bool hasPet = r.ReadBoolean();
            string petGuid = "", petClip = "";
            int petFrame = 0;
            bool petFlip = false;
            Vector2 petFeet = Vector2.zero;
            if (hasPet)
            {
                petGuid = r.ReadString();
                petClip = r.ReadString();
                petFrame = r.ReadByte();
                petFlip = r.ReadBoolean();
                petFeet = new Vector2(r.ReadSingle(), r.ReadSingle());
            }

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
            Remote.Visible = visible;
            Remote.Clip = clip;
            Remote.Frame = frame;
            Remote.FlipX = flipX;
            Remote.FeetPosition = feet;
            Remote.IsDead = isDead;
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
            Remote.ReceivedAt = lastReceivedAt;
        }

        private void ResetPeer()
        {
            // Don't leave a shared seed behind, or every later solo run would get the same dungeon.
            LevelSync.ResetSeed();
            PeerName = "";
            PingMs = -1f;
            sentHello = false;
            Remote.HasPlayer = false;
        }

        private void Disconnect(string reason)
        {
            if (Link == null)
                return;
            Plugin.Log.LogWarning("Disconnected: " + reason);

            // Steam joiner, still in the lobby: keep trying instead of leaving (needed a new invite before).
            // The run's seed is kept so the next floor still matches when the connection comes back.
            if (!Link.IsHost && Link == Steam && Steam.Lobby.InLobby && reason == "timed out")
            {
                Plugin.Log.LogInfo("Staying in the lobby and trying to reconnect...");
                PingMs = -1f;
                sentHello = false;
                Status = State.Connecting;
                lastReceivedAt = Time.realtimeSinceStartup;
                nextHelloAt = 0f;
                return;
            }

            ResetPeer();
            // A host keeps its lobby / port open so the friend can come back; a joiner leaves.
            if (Link.IsHost)
            {
                Link.DropPeer();
                Status = State.WaitingForPartner;
            }
            else
            {
                Link.Close();
                Link = null;
                Status = State.Offline;
            }
        }
    }
}
