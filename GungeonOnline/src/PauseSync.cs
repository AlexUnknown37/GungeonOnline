using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Shared pause, like local co-op: when one player pauses, the game stops for both. The other game
    // freezes time (BraveTime multiplier 0, the same thing the game uses for its own freezes) and shows
    // "Partner paused" until they unpause. Nobody can move or be shot while either player has the menu open.
    public static class PauseSync
    {
        private static NetSession session;
        private static bool wePaused;          // our pause menu is open
        private static bool partnerPaused;     // theirs is
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
                SetPartnerPaused(false);
                wePaused = false;
                return;
            }

            // Only a living player's pause counts: the game also "pauses" behind the death screen, and sharing
            // that would freeze the partner's game while they're trying to restart.
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool pausedNow = me != null && GameManager.Instance.IsPaused && !GameManager.Instance.IsLoadingLevel
                && me.healthHaver != null && !me.healthHaver.IsDead;

            // Never stay frozen by a partner who has since died, left, or is loading.
            if (partnerPaused && (!session.Remote.HasPlayer || session.Remote.IsDead
                || (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel)))
                SetPartnerPaused(false);
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
            SetPartnerPaused(paused);
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
