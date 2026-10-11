using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Blanks: every blank in the game (the normal blank, the ghost's pulse, items that blank) is a
    // SilencerInstance.TriggerSilencer call with a radius, push/knockback strengths and a VFX. When we
    // trigger one, the partner's game triggers the same one at the same spot: it shows the effect and
    // clears enemy bullets around it there too, like a blank in local co-op.
    public static class BlankSync
    {
        private static NetSession session;
        private static bool triggeringRemote;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(BlankSync));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SilencerInstance), "TriggerSilencer")]
        private static void AfterTriggerSilencer(Vector2 centerPoint, float expandSpeed, float maxRadius, GameObject silencerVFX,
            float distIntensity, float distRadius, float pushForce, float pushRadius, float knockbackForce, float knockbackRadius,
            float additionalTimeAtMaxRadius, PlayerController user, bool breaksWalls, bool skipBreakables)
        {
            if (triggeringRemote || session == null || session.Status != NetSession.State.Connected || user == null
                || !GameManager.HasInstance || user != GameManager.Instance.PrimaryPlayer)
                return;
            bool ghostVfx = silencerVFX != null && silencerVFX.name.Contains("Ghost");
            bool anyVfx = silencerVFX != null;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.Blank, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(centerPoint.x); w.Write(centerPoint.y);
                w.Write(expandSpeed); w.Write(maxRadius);
                w.Write(anyVfx); w.Write(ghostVfx);
                w.Write(distIntensity); w.Write(distRadius);
                w.Write(pushForce); w.Write(pushRadius);
                w.Write(knockbackForce); w.Write(knockbackRadius);
                w.Write(additionalTimeAtMaxRadius);
                w.Write(breaksWalls); w.Write(skipBreakables);
            });
        }

        public static void OnBlank(BinaryReader r)
        {
            string level = r.ReadString();
            Vector2 center = new Vector2(r.ReadSingle(), r.ReadSingle());
            float expandSpeed = r.ReadSingle(), maxRadius = r.ReadSingle();
            bool anyVfx = r.ReadBoolean(), ghostVfx = r.ReadBoolean();
            float distIntensity = r.ReadSingle(), distRadius = r.ReadSingle();
            float pushForce = r.ReadSingle(), pushRadius = r.ReadSingle();
            float knockbackForce = r.ReadSingle(), knockbackRadius = r.ReadSingle();
            float extraTime = r.ReadSingle();
            bool breaksWalls = r.ReadBoolean(), skipBreakables = r.ReadBoolean();

            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || level != LevelSync.CurrentLevelKey())
                return;

            // The same VFX the game loads for blanks (PlayerController.ForceBlank / DoGhostBlank).
            GameObject vfx = anyVfx ? BraveResources.Load(ghostVfx ? "Global VFX/BlankVFX_Ghost" : "Global VFX/BlankVFX", ".prefab") as GameObject : null;
            AkSoundEngine.PostEvent(ghostVfx ? "Play_OBJ_silenceblank_small_01" : "Play_OBJ_silenceblank_use_01", me.gameObject);

            triggeringRemote = true;
            try
            {
                SilencerInstance silencer = new GameObject("silencer").AddComponent<SilencerInstance>();
                silencer.TriggerSilencer(center, expandSpeed, maxRadius, vfx, distIntensity, distRadius, pushForce, pushRadius,
                    knockbackForce, knockbackRadius, extraTime, me, breaksWalls, skipBreakables);
            }
            finally
            {
                triggeringRemote = false;
            }
        }
    }
}
