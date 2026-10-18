using UnityEngine;

namespace GungeonOnline
{
    // Moving our player to the partner: the delayed ghost pull when a fight starts, and the
    // "Teleport to partner" button.
    //
    // PlayerController.WarpToPoint sets the player's transform (its base point, near the feet) straight to
    // the given point. It must get the partner's transform position, not their center: warping to the
    // center put the ghost half a body off, sometimes inside a wall where it got stuck.
    public static class PlayerWarp
    {
        private const float GhostPullDelay = 1f;

        private static NetSession session;
        private static float ghostPullAt = -1f;
        private static int ghostPullTo = -1;   // the player who started the fight
        private static float pullGiveUpAt;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        // Where a player's transform is right now (their latest feet + their transform-to-feet offset).
        private static Vector2 BasePoint(RemotePlayerState r)
        {
            return r.FeetPosition + r.RootOffset;
        }

        // Who to go to: the given slot if they're on our floor, else the closest living player on our floor
        // (a ghost only if nobody is alive).
        private static RemotePlayerState PickPartner(int preferred)
        {
            if (session == null)
                return null;
            string level = LevelSync.CurrentLevelKey();
            if (preferred >= 0 && preferred < session.Players.Length)
            {
                RemotePlayerState p = session.Players[preferred];
                if (p.Connected && p.HasPlayer && p.Scene == level)
                    return p;
            }
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            Vector2 from = me != null ? me.CenterPosition : Vector2.zero;
            RemotePlayerState best = null;
            float bestScore = float.MaxValue;
            foreach (RemotePlayerState r in session.Remotes)
            {
                if (!r.HasPlayer || r.Scene != level)
                    continue;
                float score = Vector2.Distance(from, r.Position) + (r.IsDead ? 100000f : 0f);
                if (score < bestScore)
                {
                    best = r;
                    bestScore = score;
                }
            }
            return best;
        }

        public static bool CanTeleportToPartner(out string reason)
        {
            return CanTeleportTo(-1, out reason);
        }

        private static bool CanTeleportTo(int slot, out string reason)
        {
            reason = null;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (session == null || session.Status != NetSession.State.Connected)
                reason = "not connected";
            else if (me == null)
                reason = "you have no character";
            else if (PickPartner(slot) == null)
                reason = "nobody else is on this floor";
            else if (GameManager.Instance.IsLoadingLevel)
                reason = "loading";
            else if (!me.IsGhost && me.CurrentRoom != null && me.CurrentRoom.IsSealed)
                reason = "you're in a locked fight";
            return reason == null;
        }

        // To the player whose message is being handled (they opened a chest, ...), else the closest one.
        public static void TeleportToPartner()
        {
            int slot = session != null ? session.From : -1;
            string reason;
            if (!CanTeleportTo(slot, out reason))
                return;
            RemotePlayerState to = PickPartner(slot);
            Plugin.Log.LogInfo("Teleporting to " + to.Name + ".");
            GameManager.Instance.PrimaryPlayer.WarpToPoint(BasePoint(to), true, true);
        }

        // A fight started where another player is and we're a ghost: follow them after a short delay,
        // so we arrive once they're inside the room rather than in the doorway.
        public static void ScheduleGhostPull()
        {
            ghostPullAt = Time.realtimeSinceStartup + GhostPullDelay;
            ghostPullTo = session != null ? session.From : -1;
            pullGiveUpAt = ghostPullAt + 5f;
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (ghostPullAt < 0f || now < ghostPullAt)
                return;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            // Paused (ours or someone else's pause freezing the game): wait, the pull still counts afterwards.
            if (me == null || GameManager.Instance.IsPaused || PauseSync.PartnerPaused)
            {
                pullGiveUpAt = Mathf.Max(pullGiveUpAt, now + 5f);
                return;
            }
            string reason;
            if (!CanTeleportTo(ghostPullTo, out reason))
            {
                // Try again for a few seconds (still loading, a door closing behind us...), then say why not.
                if (now < pullGiveUpAt)
                    return;
                ghostPullAt = -1f;
                Plugin.Log.LogInfo("Couldn't join the fight: " + reason + ".");
                return;
            }
            ghostPullAt = -1f;
            RemotePlayerState to = PickPartner(ghostPullTo);
            Plugin.Log.LogInfo(me.IsGhost ? to.Name + " started a fight; joining them as a ghost." : to.Name + " started a fight; joining them.");
            me.WarpToPoint(BasePoint(to), true, true);
        }
    }
}
