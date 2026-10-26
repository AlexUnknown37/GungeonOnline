using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Game modes follow the host: Turbo mode, Rainbow runs and Challenge mode (and Mega Challenge). Each game used its
    // own player's choice, so a host in Turbo ran faster than the guests (enemies out of step) and rainbow chests or
    // challenges showed up only in some games. The host's choice travels with its rules (NetSession.SendRules) and is
    // resent when it changes; guests keep the host's choice even if they flip a mode at a Breach NPC.
    public static class GameModeSync
    {
        private static NetSession session;
        private static bool hostTurbo, hostRainbow;
        private static int hostChallenge;
        private static bool haveHost;
        private static string lastSent = "";
        private static float nextCheckAt;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static bool Turbo
        {
            get { return GameStatsManager.HasInstance && GameStatsManager.Instance.isTurboMode; }
        }

        private static bool Rainbow
        {
            get { return GameStatsManager.HasInstance && GameStatsManager.Instance.rainbowRunToggled; }
        }

        // Host: part of the rules message.
        public static void Write(BinaryWriter w)
        {
            w.Write(Turbo);
            w.Write(Rainbow);
            w.Write((byte)ChallengeManager.ChallengeModeType);
            lastSent = Key(Turbo, Rainbow, (int)ChallengeManager.ChallengeModeType);
        }

        // Guest: the host's modes, from its rules message.
        public static void Read(BinaryReader r)
        {
            bool turbo = r.ReadBoolean(), rainbow = r.ReadBoolean();
            int challenge = r.ReadByte();
            if (!haveHost || turbo != hostTurbo || rainbow != hostRainbow || challenge != hostChallenge)
            {
                string modes = Describe(turbo, rainbow, challenge);
                Plugin.Log.LogInfo("Host's game modes: " + modes + ".");
                if (haveHost)
                    Toast.Show("The host changed game modes: " + modes + ".", 4f);
            }
            hostTurbo = turbo;
            hostRainbow = rainbow;
            hostChallenge = challenge;
            haveHost = true;
            Apply();
        }

        public static void Update()
        {
            if (session == null || session.Status != NetSession.State.Connected || session.Link == null)
            {
                haveHost = false;
                lastSent = "";
                return;
            }
            if (Time.realtimeSinceStartup < nextCheckAt || !GameManager.HasInstance || GameManager.Instance.IsLoadingLevel)
                return;
            nextCheckAt = Time.realtimeSinceStartup + 1f;
            if (session.Link.IsHost)
            {
                // A mode changed (Breach NPC, Daisuke's challenge): everyone gets the new choice.
                if (Key(Turbo, Rainbow, (int)ChallengeManager.ChallengeModeType) != lastSent)
                    session.SendRules();
            }
            else if (haveHost)
            {
                Apply();
            }
        }

        private static void Apply()
        {
            if (!GameStatsManager.HasInstance)
                return;
            GameStatsManager stats = GameStatsManager.Instance;
            if (stats.isTurboMode != hostTurbo)
                stats.isTurboMode = hostTurbo;
            if (stats.rainbowRunToggled != hostRainbow)
                stats.rainbowRunToggled = hostRainbow;
            if ((int)ChallengeManager.ChallengeModeType != hostChallenge)
                ChallengeManager.ChallengeModeType = (ChallengeModeType)hostChallenge;
        }

        private static string Key(bool turbo, bool rainbow, int challenge)
        {
            return turbo + "|" + rainbow + "|" + challenge;
        }

        private static string Describe(bool turbo, bool rainbow, int challenge)
        {
            string s = "Turbo " + (turbo ? "on" : "off") + ", Rainbow " + (rainbow ? "on" : "off");
            if (challenge != 0)
                s += ", " + ((ChallengeModeType)challenge);
            return s;
        }
    }
}
