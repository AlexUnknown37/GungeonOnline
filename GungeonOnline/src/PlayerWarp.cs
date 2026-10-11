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

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        // Where the partner's transform is right now (their latest feet + their transform-to-feet offset).
        private static Vector2 PartnerBasePoint
        {
            get { return session.Remote.FeetPosition + session.Remote.RootOffset; }
        }

        public static bool CanTeleportToPartner(out string reason)
        {
            reason = null;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            RemotePlayerState r = session != null ? session.Remote : null;
            if (session == null || session.Status != NetSession.State.Connected)
                reason = "not connected";
            else if (me == null)
                reason = "you have no character";
            else if (!r.HasPlayer || r.Scene != LevelSync.CurrentLevelKey())
                reason = "partner is on another floor";
            else if (GameManager.Instance.IsLoadingLevel)
                reason = "loading";
            else if (!me.IsGhost && me.CurrentRoom != null && me.CurrentRoom.IsSealed)
                reason = "you're in a locked fight";
            return reason == null;
        }

        public static void TeleportToPartner()
        {
            string reason;
            if (!CanTeleportToPartner(out reason))
                return;
            Plugin.Log.LogInfo("Teleporting to partner.");
            GameManager.Instance.PrimaryPlayer.WarpToPoint(PartnerBasePoint, true, true);
        }

        // A fight started where the partner is and we're a ghost: follow them after a short delay,
        // so we arrive once they're inside the room rather than in the doorway.
        public static void ScheduleGhostPull()
        {
            ghostPullAt = Time.realtimeSinceStartup + GhostPullDelay;
        }

        public static void Update()
        {
            if (ghostPullAt < 0f || Time.realtimeSinceStartup < ghostPullAt)
                return;
            ghostPullAt = -1f;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            string reason;
            if (me == null || !me.IsGhost || !CanTeleportToPartner(out reason))
                return;
            Plugin.Log.LogInfo("Partner started a fight; joining them as a ghost.");
            me.WarpToPoint(PartnerBasePoint, true, true);
        }
    }
}
