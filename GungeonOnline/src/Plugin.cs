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
        public const string ModVersion = "0.101.0";

        public static ManualLogSource Log;

        // Settings, saved in BepInEx/config/gungeononline.multiplayer.cfg.
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> LanPort;
        public static ConfigEntry<string> LastLanAddress;
        public static ConfigEntry<bool> Pvp;
        public static ConfigEntry<bool> HalfMoney;
        public static ConfigEntry<bool> ShowPartnerName;
        private ConfigEntry<KeyCode> toggleWindowKey;

        private NetSession session;
        private DebugWindow window;
        private readonly RemotePuppet puppet = new RemotePuppet();
        private readonly PetPuppet petPuppet = new PetPuppet();
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
            HalfMoney = Config.Bind("Rules", "HalfMoney", true,
                "When you host: money pickups count half online. The purse is shared and both games drop money, " +
                "so this keeps a run's income close to solo. Joiners follow the host's setting.");

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

            AllocProfiler.Init();
            session = new NetSession();
            window = new DebugWindow(session, puppet);
            Harmony harmony = new Harmony(Guid);
            LevelSync.Init(session, harmony);
            BulletSync.Init(session, harmony);
            DeathSync.Init(session, harmony);
            EnemySync.Init(session, harmony);
            ThrowSync.Init(session, harmony);
            BlankSync.Init(session, harmony);
            MenuButtons.Init(harmony);
            MenuGuard.Init(harmony);
            MultiplayerMenu.Init(session);
            PauseSync.Init(session);
            WorldSync.Init(session, harmony);
            PetSync.Init(session, petPuppet);
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
        private static bool testPicked, testRunStarted;
        private static float testPickedAt;

        private void TestRunWhenReady()
        {
            if (testRunCharacter.Length == 0 || testRunStarted || session.Status != NetSession.State.Connected || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
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
            // Host: start the run once both have a character (and things settled).
            if (!session.Link.IsHost || gm.IsSelectingCharacter || gm.PrimaryPlayer == null || !session.Remote.HasPlayer
                || Time.realtimeSinceStartup - testPickedAt < 8f)
                return;
            testRunStarted = true;
            Log.LogInfo("Test run: host starting the run.");
            if (Pixelator.Instance != null)
                Pixelator.Instance.FadeToBlack(0.5f, false, 0f);
            Foyer.Instance.OnDepartedFoyer();
            gm.DelayedLoadNextLevel(0.5f);
        }

        private void TestStartWhenReady()
        {
            if (testStart.Length == 0)
                return;
            MainMenuFoyerController title = FindObjectOfType<MainMenuFoyerController>();
            if (title == null || title.NewGameButton == null || !title.NewGameButton.IsVisible)
                return;
            string mode = testStart;
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
                puppet.DebugDump();

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
            { long __m = AllocProfiler.Begin(); WorldSync.Update(); AllocProfiler.End("WorldSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); EnemySync.Update(); AllocProfiler.End("EnemySync.Update", __m); }
            { long __m = AllocProfiler.Begin(); TargetSync.Update(); AllocProfiler.End("TargetSync.Update", __m); }
            { long __m = AllocProfiler.Begin(); PlayerWarp.Update(); AllocProfiler.End("PlayerWarp.Update", __m); }
            { long __m = AllocProfiler.Begin(); puppet.Update(session.Remote, session.Status == NetSession.State.Connected); AllocProfiler.End("puppet.Update", __m); }
            { long __m = AllocProfiler.Begin(); petPuppet.Update(session.Remote, puppet.Status.StartsWith("shown")); AllocProfiler.End("petPuppet.Update", __m); }
            { long __m = AllocProfiler.Begin(); PetSync.Update(); AllocProfiler.End("PetSync.Update", __m); }
            AllocProfiler.Report();
        }

        private GameCursorController gameCursor;

        private void OnGUI()
        {
            // Draw above the game's own on-screen UI.
            GUI.depth = -1000;
            long __g = AllocProfiler.Begin();
            SaveSlotLabel.Draw();
            Toast.Draw();
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
