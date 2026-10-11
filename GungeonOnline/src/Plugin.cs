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
        public const string ModVersion = "0.43.0";

        public static ManualLogSource Log;

        // Settings, saved in BepInEx/config/gungeononline.multiplayer.cfg.
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> LanPort;
        public static ConfigEntry<string> LastLanAddress;
        public static ConfigEntry<bool> Pvp;
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

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--gungeon-online-name")
                    commandLineName = args[i + 1].Trim();
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
            PauseSync.Init(session);
            WorldSync.Init(session, harmony);
            PetSync.Init(session, petPuppet);
            TargetSync.Init(session, harmony);
            PlayerWarp.Init(session);
            Log.LogInfo(Name + " " + ModVersion + " loaded. Press " + toggleWindowKey.Value + " in game to open the multiplayer window.");
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
            LevelSync.Update();
            DeathSync.Update();
            PauseSync.Update();
            WorldSync.Update();
            EnemySync.Update();
            TargetSync.Update();
            PlayerWarp.Update();
            puppet.Update(session.Remote, session.Status == NetSession.State.Connected);
            petPuppet.Update(session.Remote, puppet.Status.StartsWith("shown"));
            PetSync.Update();
        }

        private void OnGUI()
        {
            window.Draw();
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
