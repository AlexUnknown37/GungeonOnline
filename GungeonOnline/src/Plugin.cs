using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace GungeonOnline
{
    // Entry point. BepInEx finds this class, creates it once, and calls Awake/Update/OnGUI on it.
    [BepInPlugin(Guid, Name, ModVersion)]
    [BepInProcess("EtG.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gungeononline.multiplayer";
        public const string Name = "Gungeon Online";
        public const string ModVersion = "0.115.0";

        public static ManualLogSource Log;

        // Settings, saved in BepInEx/config/gungeononline.multiplayer.cfg.
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> LanPort;
        public static ConfigEntry<string> LastLanAddress;
        public static ConfigEntry<bool> Pvp;
        public static ConfigEntry<bool> HalfMoney;
        public static ConfigEntry<bool> TeleportToFights;
        public static ConfigEntry<bool> ShowPartyList;
        public static ConfigEntry<bool> ScaleEnemyHealth;
        public static ConfigEntry<float> HealthPerExtraPlayer;
        public static ConfigEntry<KeyCode> TakeABreakKey;
        public static ConfigEntry<KeyCode> AcceptRestartKey;
        public static ConfigEntry<bool> ShowPartnerName;
        private ConfigEntry<KeyCode> toggleWindowKey;

        private NetSession session;
        private DebugWindow window;
        // One character picture (and companion picture) per other player, by slot.
        public static readonly RemotePuppet[] Puppets = new RemotePuppet[Protocol.MaxPlayers];
        public static readonly PetPuppet[] Pets = new PetPuppet[Protocol.MaxPlayers];
        private bool steamChecked;

        private void Awake()
        {
            Log = Logger;
            PlayerName = Config.Bind("General", "PlayerName", "",
                "Name other players see. Leave empty to use your Steam name.");
            toggleWindowKey = Config.Bind("General", "ToggleWindowKey", KeyCode.F8,
                "Key that shows/hides the multiplayer window.");
            LanPort = Config.Bind("LAN", "Port", 27460,
                "UDP port used for LAN games. Host and players must use the same port.");
            LastLanAddress = Config.Bind("LAN", "LastAddress", "",
                "The last LAN address you joined (filled in automatically).");
            Pvp = Config.Bind("Rules", "PvP", false,
                "When you host: players' bullets can hurt each other. Joiners follow the host's setting.");
            ShowPartnerName = Config.Bind("Display", "ShowPartnerName", true,
                "Show your partner's name above their character. Only changes your own screen.");
            AcceptRestartKey = Config.Bind("General", "AcceptRestartKey", KeyCode.F7,
                "When you host and a player asks to Quick Restart while you're still playing: this key restarts the run.");
            TakeABreakKey = Config.Bind("General", "TakeABreakKey", KeyCode.F6,
                "Take a Break: the bot plays your character until you move again.");
            ScaleEnemyHealth = Config.Bind("Rules", "ScaleEnemyHealth", true,
                "When you host: enemies get more health the more players there are (stacks with Loop The Gungeon). " +
                "Joiners follow the host's setting.");
            HealthPerExtraPlayer = Config.Bind("Rules", "HealthPerExtraPlayer", 0.5f,
                "When you host: extra enemy health for each player beyond the first (0.5 = +50%: x1.5 with 2 players, x2 with 3, x2.5 with 4).");
            ShowPartyList = Config.Bind("Display", "ShowPartyList", true,
                "Show the other players (character and health) on the right side of the screen.");
            TeleportToFights = Config.Bind("Rules", "TeleportToFights", true,
                "When you host: when a player walks into a fight (the room locks), everyone else is teleported there. " +
                "Joiners follow the host's setting.");
            HalfMoney = Config.Bind("Rules", "HalfMoney", true,
                "When you host: a boss's money counts 1/players online. The purse is shared and a boss drops money in every " +
                "player's game, so this keeps a run's income close to solo. Enemies' money always counts in full. " +
                "Joiners follow the host's setting.");

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--gungeon-online-name")
                    commandLineName = args[i + 1].Trim();
                if (args[i] == "--gungeon-online-test")
                    testStart = args[i + 1].Trim();
                if (args[i] == "--gungeon-online-test-run")
                    testRunCharacter = args[i + 1].Trim();
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--gungeon-online-bot")
                    testBot = true;
            }

            AllocProfiler.Init();
            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                Puppets[i] = new RemotePuppet();
                Pets[i] = new PetPuppet();
            }
            session = new NetSession();
            window = new DebugWindow(session);
            Harmony harmony = new Harmony(Guid);
            LevelSync.Init(session, harmony);
            BulletSync.Init(session, harmony);
            DeathSync.Init(session, harmony);
            EnemySync.Init(session, harmony);
            AttackSync.Init(session, harmony);
            CompanionSync.Init(session);
            GenerationSeed.Init(session, harmony);
            EffectSync.Init(session, harmony);
            ThrowSync.Init(session, harmony);
            BlankSync.Init(session, harmony);
            MenuButtons.Init(harmony);
            MenuGuard.Init(harmony);
            MultiplayerMenu.Init(session);
            PauseSync.Init(session);
            WorldSync.Init(session, harmony);
            PetSync.Init(session);
            TargetSync.Init(session, harmony);
            PlayerWarp.Init(session);
            SaveSync.Init(session, harmony);
            SaveSlotLabel.Init(session);
            LootSync.Init(session, harmony);
            LoopSync.Init(session, harmony);
            OrbitalSync.Init(session);
            FloorCheck.Init(session);
            ModSettingsSync.Init(session);
            ShopBan.Init(session, harmony);
            BeamSync.Init(session);
            ExpandSync.Init(session, harmony);
            Autopilot.Init(session, harmony);
            BreakButton.Init(session, harmony);
            StandIn.Init(session, harmony);
            DifficultySync.Init(session, harmony);
            TablePush.Init(session);
            BreachCultist.Init(session, harmony);
            InventorySync.Init(session);
            PartnerMapMarker.Init(session);
            Log.LogInfo(Name + " " + ModVersion + " loaded. Press " + toggleWindowKey.Value + " in game to open the multiplayer window.");
        }

        // Testing two copies on one PC: "--gungeon-online-test host" hosts on LAN and "--gungeon-online-test join"
        // joins 127.0.0.1, both as soon as the title screen is up (then they go into the Breach).
        private static string testStart = "";

        // Testing floor sync: "--gungeon-online-test-run Rogue" picks that character on the Breach's select screen;
        // the host then starts a run once the partner has picked theirs (the joiner follows the host).
        private static string testRunCharacter = "";
        private static bool testBot;   // "--gungeon-online-bot": this test copy plays itself (Take a Break, always on)
        private static bool testPicked, testRunStarted;
        private static float testPickedAt;

        private void TestRunWhenReady()
        {
            if (testRunCharacter.Length == 0 || testRunStarted || session.Status != NetSession.State.Connected || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (!gm.IsFoyer && gm.PrimaryPlayer != null && !gm.IsLoadingLevel)
            {
                testRunStarted = true; // in a run: the test start is done (later Breach visits are the game's own)
                return;
            }
            if (!gm.IsFoyer || gm.IsLoadingLevel || Foyer.Instance == null)
                return;
            if (!testPicked)
            {
                if (!gm.IsSelectingCharacter || Foyer.IsCurrentlyPlayingCharacterSelect)
                    return;
                foreach (FoyerCharacterSelectFlag flag in FindObjectsOfType<FoyerCharacterSelectFlag>())
                {
                    if (flag != null && flag.CharacterPrefabPath != null && flag.CharacterPrefabPath.EndsWith(testRunCharacter))
                    {
                        Log.LogInfo("Test run: picking " + flag.CharacterPrefabPath);
                        Foyer.Instance.StartCoroutine(Foyer.Instance.OnSelectedCharacter(0.25f, flag));
                        testPicked = true;
                        testPickedAt = Time.realtimeSinceStartup;
                        return;
                    }
                }
                return;
            }
            if (gm.IsSelectingCharacter || gm.PrimaryPlayer == null || Time.realtimeSinceStartup - testPickedAt < 8f)
                return;
            // Guest that arrived after the host started: take the elevator, which joins the host's run.
            if (!session.Link.IsHost)
            {
                if (!LevelSync.HostInRun)
                    return;
                testRunStarted = true;
                Log.LogInfo("Test run: guest joining the host's run.");
                Foyer.Instance.OnDepartedFoyer();
                gm.DelayedLoadNextLevel(0.5f);
                return;
            }
            // Host: start the run once everyone has a character (and things settled).
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (!p.HasPlayer)
                    return;
            }
            testRunStarted = true;
            Log.LogInfo("Test run: host starting the run.");
            if (Pixelator.Instance != null)
                Pixelator.Instance.FadeToBlack(0.5f, false, 0f);
            Foyer.Instance.OnDepartedFoyer();
            gm.DelayedLoadNextLevel(0.5f);
        }

        // Test copies only. K: warp our player 2 tiles below the nearest table that isn't flipped yet.
        // L: write our health and the nearest table's state to the log. B: go back to the Breach.
        private static bool testMode;
        private static FlippableCover testTable;

        private void TestWarpToTable()
        {
            if (!testMode || !GameManager.HasInstance)
                return;
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            if (me == null)
                return;
            if (Input.GetKeyDown(KeyCode.L))
            {
                HealthHaver h = me.healthHaver;
                Log.LogInfo("Test: " + me.characterIdentity + " health " + h.GetCurrentHealth() + "/" + h.GetMaxHealth() + ", armor " + h.Armor
                    + ", game type " + GameManager.Instance.CurrentGameType + ", vulnerable " + h.IsVulnerable + ", rolling " + me.IsDodgeRolling
                    + ", input " + me.CurrentInputState + ", reviving " + me.IsCurrentlyCoopReviving + ", ghost " + me.IsGhost
                    + ", hitbox " + (me.specRigidbody != null && me.specRigidbody.HitboxPixelCollider != null && me.specRigidbody.HitboxPixelCollider.Enabled)
                    + ", collides " + (me.specRigidbody != null && me.specRigidbody.CollideWithOthers));
                FlippableCover t = testTable != null ? testTable : NearestTable(me, true);
                if (t != null)
                    Log.LogInfo("Test: table '" + t.name + "' transform " + t.transform.position + ", body " + (t.specRigidbody != null
                        ? t.specRigidbody.UnitCenter + " pushable " + t.specRigidbody.CanBePushed + " on " + t.specRigidbody.gameObject.name : "none")
                        + ", flipped " + t.IsFlipped + ", tracked " + WorldSync.Describe(t));
            }
            // G: get an AK-47 (a gun that isn't anyone's starting gun, to test gear being kept).
            if (Input.GetKeyDown(KeyCode.G))
            {
                Gun ak = PickupObjectDatabase.GetById(15) as Gun;
                if (ak != null)
                {
                    me.inventory.AddGunToInventory(ak, true);
                    Log.LogInfo("Test: got " + ak.name + ".");
                }
                return;
            }
            // X: die (to test deaths, game overs and restarts).
            if (Input.GetKeyDown(KeyCode.X) && !GameManager.Instance.IsFoyer && !me.IsGhost && !me.healthHaver.IsDead)
            {
                Log.LogInfo("Test: dying.");
                me.healthHaver.Armor = 0f;
                me.healthHaver.ApplyDamage(10000f, Vector2.zero, "Test", CoreDamageTypes.None, DamageCategory.Unstoppable, true, null, true);
                return;
            }
            // I: a test loadout of items, companions, orbitals and guns for this player, then a gun tour.
            if (Input.GetKeyDown(KeyCode.I) && !GameManager.Instance.IsFoyer && !me.IsGhost)
            {
                TestLoadout.Give(me, session.LocalSlot);
                return;
            }
            // R: press Quick Restart on the death screen (as a player clicking it).
            if (Input.GetKeyDown(KeyCode.R) && LevelSync.DeathPageUp())
            {
                Log.LogInfo("Test: pressing Quick Restart.");
                LevelSync.PressQuickRestart();
                return;
            }
            // H: 5 more heart containers, healed (to test lots of hearts).
            if (Input.GetKeyDown(KeyCode.H) && !me.IsGhost)
            {
                float max = me.healthHaver.GetMaxHealth() + 5f;
                me.healthHaver.SetHealthMaximum(max, 5f, false);
                Log.LogInfo("Test: max health now " + max + ".");
                return;
            }
            // J (host): start a Loop The Gungeon loop, the same way its rift at the Forge does.
            if (Input.GetKeyDown(KeyCode.J) && !GameManager.Instance.IsFoyer && !GameManager.Instance.IsLoadingLevel
                && session.Link != null && session.Link.IsHost)
            {
                System.Type tube = HarmonyLib.AccessTools.TypeByName("LoopTheGungeon.Util.TimeTubeTransition");
                System.Reflection.MethodInfo start = tube != null ? HarmonyLib.AccessTools.Method(tube, "StartLoopTimeTubeThenLoop") : null;
                if (start == null)
                    Log.LogInfo("Test: Loop The Gungeon isn't installed.");
                else
                {
                    Log.LogInfo("Test: starting a loop.");
                    start.Invoke(null, new object[] { GameManager.Instance, 0f });
                }
                return;
            }
            // B: go back to the Breach mid-run (like quitting to the Breach).
            if (Input.GetKeyDown(KeyCode.B) && !GameManager.Instance.IsFoyer && !GameManager.Instance.IsLoadingLevel)
            {
                Log.LogInfo("Test: going back to the Breach.");
                GameManager.Instance.LoadCharacterSelect(true, false);
                return;
            }
            if (!Input.GetKeyDown(KeyCode.K))
                return;
            FlippableCover best = NearestTable(me, false);
            if (best == null)
            {
                Log.LogInfo("Test: no unflipped table on this floor.");
                return;
            }
            testTable = best;
            Vector2 spot = best.specRigidbody.UnitBottomCenter + new Vector2(0f, -2f);
            Log.LogInfo("Test: warping 2 tiles below table '" + best.name + "' at " + best.specRigidbody.UnitCenter + ".");
            me.WarpToPoint(spot, false, false);
        }

        private static FlippableCover NearestTable(PlayerController me, bool flippedToo)
        {
            FlippableCover best = null;
            float bestDist = float.MaxValue;
            foreach (FlippableCover t in Resources.FindObjectsOfTypeAll<FlippableCover>())
            {
                if (t == null || !t.gameObject.scene.IsValid() || (t.IsFlipped && !flippedToo) || t.IsBroken || t.specRigidbody == null)
                    continue;
                float d = Vector2.Distance(t.specRigidbody.UnitCenter, me.CenterPosition);
                if (d < bestDist)
                {
                    best = t;
                    bestDist = d;
                }
            }
            return best;
        }

        private void TestStartWhenReady()
        {
            if (testStart.Length == 0)
                return;
            MainMenuFoyerController title = FindObjectOfType<MainMenuFoyerController>();
            if (title == null || title.NewGameButton == null || !title.NewGameButton.IsVisible)
                return;
            string mode = testStart;
            testMode = true;
            testStart = "";
            Log.LogInfo("Test start: " + mode);
            bool ok = mode == "host" ? session.HostLan(LanPort.Value)
                : session.JoinLan(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, LanPort.Value));
            if (ok)
                MultiplayerMenu.EnterBreachFromTitle();
        }

        // Set from "--gungeon-online-name X" on the command line (for two copies on one PC, which share settings).
        private static string commandLineName = "";

        // The name shown to other players: the command-line name, else the PlayerName setting,
        // else the Steam name, else "Player".
        public static string DisplayName()
        {
            string name = commandLineName.Length > 0 ? commandLineName : PlayerName.Value.Trim();
            if (name.Length > 0)
                return name.Length > 24 ? name.Substring(0, 24) : name;
            if (SteamManager.Initialized)
                return SteamFriends.GetPersonaName();
            return "Player";
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleWindowKey.Value))
                window.Visible = !window.Visible;
            if (Input.GetKeyDown(KeyCode.F9))
            {
                foreach (RemotePlayerState p in session.Remotes)
                    Puppets[p.Slot].DebugDump();
            }

            // The game starts Steam itself (SteamManager). Once it has, enable the Steam option.
            if (!steamChecked && SteamManager.Initialized)
            {
                steamChecked = true;
                session.InitSteam();
                JoinLobbyFromCommandLine();
            }

            { long __m = AllocProfiler.Begin(); session.Update(); AllocProfiler.End("session.Update", __m); }
            { long __m = AllocProfiler.Begin(); TestStartWhenReady(); AllocProfiler.End("TestStartWhenReady", __m); }
            TestRunWhenReady();
            // Test bots hosting: after a game over, start a new run by themselves (the guests follow).
            if (!testBot || session.Link == null || !session.Link.IsHost)
                deathPageUp = false;
            else if (Time.frameCount % 30 == 0)
                deathPageUp = LevelSync.DeathPageUp(); // twice a second is plenty
            if (deathPageUp)
            {
                if (deathScreenSince < 0f)
                    deathScreenSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - deathScreenSince > 6f)
                {
                    deathScreenSince = -1f;
                    Log.LogInfo("Test: game over - quick restarting.");
                    LevelSync.PressQuickRestart();
                }
            }
            else
                deathScreenSince = -1f;
            if (testBot && !Autopilot.Active && GameManager.HasInstance && GameManager.Instance.PrimaryPlayer != null && !GameManager.Instance.IsFoyer)
            {
                Autopilot.Explore = true; // test bots: the living one with the lowest player number leads the way
                Autopilot.Start(true);
            }
            { long __m = AllocProfiler.Begin(); Autopilot.Update(); AllocProfiler.End("Autopilot.Update", __m); }
            { long __m = AllocProfiler.Begin(); StandIn.Update(); AllocProfiler.End("StandIn.Update", __m); }
            DifficultySync.Update();
            TestWarpToTable();

            // While playing online the game must keep running when its window isn't focused: otherwise Unity
            // stops completely on tab-out, nothing is sent, and the partner times out. (Keep Running does this
            // too, but players may not have it.) The game's own pause-on-tab-out still happens unless Keep
            // Running is installed; then Pause Sync freezes both games, like local co-op.
            bool online = session.Status == NetSession.State.Connected || session.Status == NetSession.State.Connecting;
            if (online && !Application.runInBackground)
                Application.runInBackground = true;
            { long __m = AllocProfiler.Begin(); SaveSync.Update(); AllocProfiler.End("SaveSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); LootSync.Update(); AllocProfiler.End("LootSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); LoopSync.Update(); AllocProfiler.End("LoopSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); OrbitalSync.Update(); AllocProfiler.End("OrbitalSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); InventorySync.Update(); AllocProfiler.End("InventorySync.Update", __m); }
            { long __m = AllocProfiler.Begin(); InventorySync.UpdateRejoin(); AllocProfiler.End("InventorySync.UpdateRejoin", __m); }
            { long __m = AllocProfiler.Begin(); FloorCheck.Update(); AllocProfiler.End("FloorCheck.Update", __m); }
            { long __m = AllocProfiler.Begin(); ModSettingsSync.Update(); AllocProfiler.End("ModSettingsSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); ShopBan.Update(); AllocProfiler.End("ShopBan.Update", __m); }
            { long __m = AllocProfiler.Begin(); BulletSync.Update(); AllocProfiler.End("BulletSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); BeamSync.Update(); AllocProfiler.End("BeamSync.Update", __m); }
            ExpandSync.Update();
            { long __m = AllocProfiler.Begin(); PartnerMapMarker.Update(); AllocProfiler.End("PartnerMapMarker.Update", __m); }
            { long __m = AllocProfiler.Begin(); LevelSync.Update(); AllocProfiler.End("LevelSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); DeathSync.Update(); AllocProfiler.End("DeathSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); PauseSync.Update(); AllocProfiler.End("PauseSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); MultiplayerMenu.Update(); AllocProfiler.End("MultiplayerMenu.Update", __m); }
            { long __m = AllocProfiler.Begin(); MenuButtons.Update(); AllocProfiler.End("MenuButtons.Update", __m); }
            if (steamChecked)
                { long __m = AllocProfiler.Begin(); SteamFriendsList.UpdateOwnPresence(session); AllocProfiler.End("SteamFriendsList.UpdateOwnPresence", __m); }
            { long __m = AllocProfiler.Begin(); TablePush.Update(); AllocProfiler.End("TablePush.Update", __m); }
            { long __m = AllocProfiler.Begin(); WorldSync.Update(); AllocProfiler.End("WorldSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); EnemySync.Update(); AllocProfiler.End("EnemySync.Update", __m); }
            AttackSync.Update();
            CompanionSync.Update();
            NetStats.Update(session.Status == NetSession.State.Connected);
            if (testMode)
                TestLoadout.Update();
            { long __m = AllocProfiler.Begin(); TargetSync.Update(); AllocProfiler.End("TargetSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); PlayerWarp.Update(); AllocProfiler.End("PlayerWarp.Update", __m); }
            {
                long __m = AllocProfiler.Begin();
                bool connected = session.Status == NetSession.State.Connected;
                for (int i = 0; i < Protocol.MaxPlayers; i++)
                {
                    RemotePlayerState p = session.Players[i];
                    bool here = connected && p.Connected && i != session.LocalSlot && !LoopSync.TubePlaying;
                    Puppets[i].Update(p, here);
                    Pets[i].Update(p, here && Puppets[i].Status.StartsWith("shown"));
                }
                AllocProfiler.End("puppets.Update", __m);
            }
            { long __m = AllocProfiler.Begin(); PetSync.Update(); AllocProfiler.End("PetSync.Update", __m); }
            AllocProfiler.Report();
        }

        private GameCursorController gameCursor;
        private float deathScreenSince = -1f;
        private bool deathPageUp;

        private void OnGUI()
        {
            // Draw above the game's own on-screen UI.
            GUI.depth = -1000;
            long __g = AllocProfiler.Begin();
            SaveSlotLabel.Draw();
            Toast.Draw();
            if (ShowPartyList.Value)
                PartyList.Draw(session);
            window.Draw();
            MultiplayerMenu.Draw();

            // The game draws its cursor the same way, so our panels covered it. While one is open, draw the
            // game's own cursor again on top of them.
            if ((MultiplayerMenu.IsOpen || window.Visible || SaveSlotLabel.IsListOpen) && Event.current.type == EventType.Repaint)
            {
                if (gameCursor == null)
                    gameCursor = FindObjectOfType<GameCursorController>();
                if (gameCursor != null)
                    gameCursor.DrawCursor();
            }
            AllocProfiler.End("OnGUI", __g);
        }

        private void OnApplicationQuit()
        {
            SaveSync.EndBorrow("game closing"); // never quit with the host's progress in place
            session.Leave();
            session.Discovery.Stop();
        }

        // If the game was started by clicking "Join Game" on a Steam invite,
        // Steam passes "+connect_lobby <id>" on the command line.
        private void JoinLobbyFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "+connect_lobby")
                    continue;
                ulong lobbyId;
                if (ulong.TryParse(args[i + 1], out lobbyId))
                {
                    Log.LogInfo("Joining lobby from command line: " + lobbyId);
                    session.JoinSteamLobby(new CSteamID(lobbyId));
                }
            }
        }
    }
}
