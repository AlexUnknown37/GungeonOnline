using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Lets enemies go after the partner, not just the player in their own game.
    //
    // The game's AI aims, walks and fires its bullet patterns at AIActor.TargetRigidbody, which returns
    // AIActor.OverrideTarget when one is set (the game uses this itself, e.g. for charmed enemies). So the
    // partner gets an invisible physics body that follows them, and every enemy picks the closer of the two
    // players. Enemies that pick the partner get OverrideTarget = that body.
    //
    // Both games run this with the same positions, so they mostly agree: an enemy chasing the partner on
    // your screen is chasing them in their game too (where it simply targets their real player).
    // The body has no collision: bullets fly through the partner's picture on your screen; in the
    // partner's own game the same attack really hits or misses them.
    public static class TargetSync
    {
        private const float SwitchBias = 0.8f;      // switch only when the other player is 20% closer
        private const float Interval = 0.25f;       // re-pick targets 4x a second

        private static NetSession session;
        private static GameObject targetGo;
        private static SpeculativeRigidbody target;
        private static readonly Dictionary<AIActor, bool> chasingPartner = new Dictionary<AIActor, bool>();
        private static float nextPickAt;

        // The partner's invisible body (where they stand), when it's set up. Also used to replay their kicks.
        public static SpeculativeRigidbody PartnerBody
        {
            get { return target != null && targetGo != null && targetGo.activeInHierarchy && target.enabled ? target : null; }
        }

        public static void Init(NetSession netSession, HarmonyLib.Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(TargetSync));
        }

        public static void Update()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            RemotePlayerState remote = session != null ? session.Remote : null;
            // The physics body can only be set up once the floor has finished loading: right after a level load
            // the game's physics system doesn't exist yet, the setup crashed, and the body stayed broken for the
            // whole floor (enemies then followed the partner but aimed at us and never fired).
            bool physicsReady = PhysicsEngine.HasInstance && GameManager.HasInstance && !GameManager.Instance.IsLoadingLevel;
            bool partnerTargetable = physicsReady && session != null && session.Status == NetSession.State.Connected && me != null
                && remote.HasPlayer && !remote.IsDead && remote.Scene == LevelSync.CurrentLevelKey();
            if (!partnerTargetable)
            {
                ReleaseAll();
                if (targetGo != null && targetGo.activeSelf)
                    targetGo.SetActive(false);
                return;
            }

            // Follow the partner's (smoothed) position. Center = smoothed feet + their center-to-feet offset.
            Vector2 feet = remote.Motion.HasData ? remote.Motion.Sample() : remote.FeetPosition;
            Vector2 partnerCenter = feet + (remote.Position - remote.FeetPosition);
            if (!EnsureTarget())
                return;
            if (!targetGo.activeSelf)
                targetGo.SetActive(true);
            targetGo.transform.position = new Vector3(partnerCenter.x, partnerCenter.y, targetGo.transform.position.z);
            try
            {
                if (!target.enabled)
                    target.enabled = true; // created disabled; switched on now that physics exists
                target.Reinitialize();
            }
            catch (System.Exception e)
            {
                // Throw it away and build a fresh one next frame.
                Plugin.Log.LogWarning("Partner target body failed to set up (" + e.GetType().Name + "); retrying.");
                ReleaseAll();
                Object.Destroy(targetGo);
                targetGo = null;
                target = null;
                return;
            }

            if (Time.realtimeSinceStartup < nextPickAt)
            {
                ReapplyOverrides();
                return;
            }
            nextPickAt = Time.realtimeSinceStartup + Interval;

            bool meTargetable = !me.IsGhost && !me.healthHaver.IsDead;
            Vector2 myCenter = me.CenterPosition;
            RoomHandlerList(me);
            foreach (AIActor enemy in enemies)
            {
                if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead || !enemy.CanTargetPlayers
                    || enemy.CompanionOwner != null)
                    continue;
                Vector2 pos = enemy.CenterPosition;
                float dMe = meTargetable ? Vector2.Distance(pos, myCenter) : float.MaxValue;
                float dPartner = Vector2.Distance(pos, partnerCenter);

                bool wasPartner;
                chasingPartner.TryGetValue(enemy, out wasPartner);
                bool partner = wasPartner ? !(dMe < dPartner * SwitchBias) : dPartner < dMe * SwitchBias;
                chasingPartner[enemy] = partner;
            }
            ReapplyOverrides();
        }

        // Some behaviours clear OverrideTarget; set it again every frame for enemies chasing the partner.
        private static void ReapplyOverrides()
        {
            List<AIActor> gone = null;
            foreach (KeyValuePair<AIActor, bool> pair in chasingPartner)
            {
                AIActor enemy = pair.Key;
                if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead)
                {
                    if (gone == null) gone = new List<AIActor>();
                    gone.Add(enemy);
                    continue;
                }
                if (pair.Value)
                {
                    if (enemy.OverrideTarget != target)
                        enemy.OverrideTarget = target;
                }
                else if (enemy.OverrideTarget == target)
                {
                    enemy.OverrideTarget = null;
                }
            }
            if (gone != null)
            {
                foreach (AIActor enemy in gone)
                    chasingPartner.Remove(enemy);
            }
        }

        private static void ReleaseAll()
        {
            foreach (KeyValuePair<AIActor, bool> pair in chasingPartner)
            {
                if (pair.Key != null && target != null && pair.Key.OverrideTarget == target)
                    pair.Key.OverrideTarget = null;
            }
            chasingPartner.Clear();
        }

        private static readonly List<AIActor> enemies = new List<AIActor>();

        // Enemies in the room we're in (the only ones that are fighting).
        private static void RoomHandlerList(PlayerController me)
        {
            enemies.Clear();
            if (me.CurrentRoom != null)
                me.CurrentRoom.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.All, ref enemiesOut);
            if (enemiesOut != null)
                enemies.AddRange(enemiesOut);
        }
        private static List<AIActor> enemiesOut = new List<AIActor>();

        // ---- Line of sight ----
        // Enemies only fire when they "see" their target: AIActor casts a ray that must HIT the target's body.
        // Ours has no collisions, so the ray never hit it and enemies aimed but never fired. For our body we
        // answer the question ourselves: visible when no wall cell lies between the enemy and the partner.

        [HarmonyLib.HarmonyPostfix]
        [HarmonyLib.HarmonyPatch(typeof(AIActor), "HasLineOfSightToRigidbody")]
        private static void AfterHasLineOfSightToRigidbody(AIActor __instance, SpeculativeRigidbody __0, ref bool __result)
        {
            if (target != null && __0 == target && __instance.specRigidbody != null)
                __result = !WallBetween(__instance.specRigidbody.UnitCenter, target.UnitCenter);
        }

        [HarmonyLib.HarmonyPostfix]
        [HarmonyLib.HarmonyPatch(typeof(AIActor), "HasLineOfSightToTargetFromPosition")]
        private static void AfterHasLineOfSightFromPosition(AIActor __instance, Vector2 __0, ref bool __result)
        {
            if (target != null && __instance.TargetRigidbody == target)
                __result = !WallBetween(__0, target.UnitCenter);
        }

        // Walk the line between two points in quarter-tile steps and look for wall cells.
        private static bool WallBetween(Vector2 from, Vector2 to)
        {
            Dungeonator.Dungeon dungeon = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (dungeon == null || dungeon.data == null)
                return false;
            float length = Vector2.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length * 4f));
            for (int i = 1; i < steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (!dungeon.data.CheckInBoundsAndValid(x, y) || dungeon.data.isWall(x, y))
                    return true;
            }
            return false;
        }

        // The invisible body enemies can aim at: one player-sized hitbox, no collisions.
        private static bool EnsureTarget()
        {
            if (target != null && targetGo != null)
                return true;
            targetGo = new GameObject("GungeonOnline Partner Target");
            target = targetGo.AddComponent<SpeculativeRigidbody>();
            target.CollideWithOthers = false;
            target.CollideWithTileMap = false;
            target.CanBePushed = false;
            target.PixelColliders = new List<PixelCollider>();
            PixelCollider box = new PixelCollider();
            box.ColliderGenerationMode = PixelCollider.PixelColliderGeneration.Manual;
            box.CollisionLayer = CollisionLayer.PlayerHitBox;
            box.ManualOffsetX = -6;   // 12 x 16 pixels around the center (16 pixels = 1 tile)
            box.ManualOffsetY = -8;
            box.ManualWidth = 12;
            box.ManualHeight = 16;
            target.PixelColliders.Add(box);
            // Disabled until Update has checked the physics system is ready (it then enables + initializes it).
            target.enabled = false;
            Plugin.Log.LogInfo("Created the partner's target body for enemies.");
            return true;
        }
    }
}
