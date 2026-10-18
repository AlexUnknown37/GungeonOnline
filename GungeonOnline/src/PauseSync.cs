using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Shared pause, like local co-op: when one player pauses, the game stops for everyone. The other games
    // freeze time (BraveTime multiplier 0, the same thing the game uses for its own freezes) and shows
    // "Partner paused" until they unpause. Nobody can move or be shot while either player has the menu open.
    public static class PauseSync
    {
        private static NetSession session;
        private static bool wePaused;          // our pause menu is open
        private static bool partnerPaused;     // someone else's is
        private static readonly bool[] pausedBy = new bool[Protocol.MaxPlayers]; // who has theirs open
        public static string PausedBy = "";
        private static GameObject freezeKey;   // owner of our time multiplier

        public static bool PartnerPaused
        {
            get { return partnerPaused; }
        }

        public static void Init(NetSession netSession)
        {
            session = netSession;
            freezeKey = new GameObject("GungeonOnline PauseFreeze");
            Object.DontDestroyOnLoad(freezeKey);
        }

        public static void Update()
        {
            bool connected = session != null && session.Status == NetSession.State.Connected;
            if (!connected)
            {
                for (int i = 0; i < pausedBy.Length; i++)
                    pausedBy[i] = false;
                SetPartnerPaused(false);
                wePaused = false;
                return;
            }

            // Only a living player's pause counts: the game also "pauses" behind the death screen, and sharing
            // that would freeze the partner's game while they're trying to restart.
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool pausedNow = me != null && GameManager.Instance.IsPaused && !GameManager.Instance.IsLoadingLevel
                && me.healthHaver != null && !me.healthHaver.IsDead;

            // Never stay frozen by a player who has since died, left, or is loading.
            bool loading = GameManager.HasInstance && GameManager.Instance.IsLoadingLevel;
            string by = "";
            for (int i = 0; i < pausedBy.Length; i++)
            {
                RemotePlayerState p = session.Players[i];
                if (pausedBy[i] && (loading || !p.Connected || !p.HasPlayer || p.IsDead))
                    pausedBy[i] = false;
                if (pausedBy[i] && by.Length == 0)
                    by = p.Name;
            }
            PausedBy = by;
            SetPartnerPaused(by.Length > 0);
            if (pausedNow != wePaused)
            {
                wePaused = pausedNow;
                bool p = pausedNow;
                session.SendReliable(MsgType.Paused, delegate (BinaryWriter w) { w.Write(p); });
            }
        }

        public static void OnPaused(BinaryReader r)
        {
            bool paused = r.ReadBoolean();
            Plugin.Log.LogInfo(session.PeerName + (paused ? " paused the game." : " unpaused the game."));
            if (session.From >= 0 && session.From < pausedBy.Length)
                pausedBy[session.From] = paused;
        }

        private static void SetPartnerPaused(bool paused)
        {
            if (paused == partnerPaused)
                return;
            partnerPaused = paused;
            if (paused)
                BraveTime.RegisterTimeScaleMultiplier(0f, freezeKey);
            else
                BraveTime.ClearMultiplier(freezeKey);
        }
    }
}
