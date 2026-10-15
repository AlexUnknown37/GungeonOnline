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
        public const string ModVersion = "0.68.0";

        public static ManualLogSource Log;

        // Settings, saved in BepInEx/config/gungeononline.multiplayer.cfg.
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> LanPort;
        public static ConfigEntry<string> LastLanAddress;
        public static ConfigEntry<bool> Pvp;
        public static ConfigEntry<bool> HalfMoney;
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
            }

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
            PartnerMapMarker.Init(session);
            Log.LogInfo(Name + " " + ModVersion + " loaded. Press " + toggleWindowKey.Value + " in game to open the multiplayer window.");
        }

        // Testing two copies on one PC: "--gungeon-online-test host" hosts on LAN and "--gungeon-online-test join"
        // joins 127.0.0.1, both as soon as the title screen is up (then they go into the Breach).
        private static string testStart = "";

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

            session.Update();
            TestStartWhenReady();

            // While playing online the game must keep running when its window isn't focused: otherwise Unity
            // stops completely on tab-out, nothing is sent, and the partner times out. (Keep Running does this
            // too, but players may not have it.) The game's own pause-on-tab-out still happens unless Keep
            // Running is installed; then Pause Sync freezes both games, like local co-op.
            bool online = session.Status == NetSession.State.Connected || session.Status == NetSession.State.Connecting;
            if (online && !Application.runInBackground)
                Application.runInBackground = true;
            SaveSync.Update();
            LootSync.Update();
            LoopSync.Update();
            PartnerMapMarker.Update();
            LevelSync.Update();
            DeathSync.Update();
            PauseSync.Update();
            MultiplayerMenu.Update();
            if (steamChecked)
                SteamFriendsList.UpdateOwnPresence(session);
            WorldSync.Update();
            EnemySync.Update();
            TargetSync.Update();
            PlayerWarp.Update();
            puppet.Update(session.Remote, session.Status == NetSession.State.Connected);
            petPuppet.Update(session.Remote, puppet.Status.StartsWith("shown"));
            PetSync.Update();
        }

        private GameCursorController gameCursor;

        private void OnGUI()
        {
            // Draw above the game's own on-screen UI.
            GUI.depth = -1000;
            SaveSlotLabel.Draw();
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
        }

        private void OnApplicationQuit()
        {
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
