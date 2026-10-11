using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Thrown / placed items (Molotovs, grenades, mines, ... anything that is a SpawnObjectPlayerItem):
    // when we use one, the partner's game spawns the same object the same way. What it does after that
    // is the game's own logic in both games: a Molotov breaks into fire on the floor, a grenade explodes,
    // acid/water/oil spreads. Floor effects then work for everyone: walking into that fire in your own
    // game sets you on fire, because it's real fire on your floor.
    //
    // Spawning follows SpawnObjectPlayerItem.DoSpawn exactly (three ways: projectile items fly off,
    // tossForce 0 items are placed at the player, the rest are tossed as debris).
    // Not copied: items that spawn creatures (enemyGuidToSpawn), shadow clones, cigarettes.
    public static class ThrowSync
    {
        private static NetSession session;
        private static bool spawningRemote;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(ThrowSync));
        }

        private static bool Copyable(SpawnObjectPlayerItem item)
        {
            return item != null && string.IsNullOrEmpty(item.enemyGuidToSpawn) && !item.IsKageBunshinItem && !item.IsCigarettes;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SpawnObjectPlayerItem), "DoSpawn")]
        private static void AfterDoSpawn(SpawnObjectPlayerItem __instance, PlayerController user, float angleFromAim)
        {
            if (spawningRemote || session == null || session.Status != NetSession.State.Connected || user == null
                || !GameManager.HasInstance || user != GameManager.Instance.PrimaryPlayer || !Copyable(__instance)
                || __instance.spawnedPlayerObject == null || user.specRigidbody == null)
                return;

            int itemId = __instance.PickupObjectId;
            Vector2 center = user.specRigidbody.UnitCenter;
            Vector2 aim = user.unadjustedAimPoint - user.LockedApproximateSpriteCenter;
            bool synergy = __instance.SynergyObjectToSpawn != null
                && __instance.spawnedPlayerObject.name.StartsWith(__instance.SynergyObjectToSpawn.name);
            string level = LevelSync.CurrentLevelKey();
            Plugin.Log.LogInfo("Threw " + __instance.name + "; copying it to the partner.");
            session.SendReliable(MsgType.Throw, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(itemId);
                w.Write(angleFromAim);
                w.Write(center.x); w.Write(center.y);
                w.Write(aim.x); w.Write(aim.y);
                w.Write(synergy);
            });
        }

        public static void OnThrow(BinaryReader r)
        {
            string level = r.ReadString();
            int itemId = r.ReadInt32();
            float angleFromAim = r.ReadSingle();
            Vector2 center = new Vector2(r.ReadSingle(), r.ReadSingle());
            Vector2 aim = new Vector2(r.ReadSingle(), r.ReadSingle());
            bool synergy = r.ReadBoolean();
            if (!GameManager.HasInstance || level != LevelSync.CurrentLevelKey())
                return;

            SpawnObjectPlayerItem item = PickupObjectDatabase.GetById(itemId) as SpawnObjectPlayerItem;
            if (!Copyable(item))
                return;
            GameObject prefab = synergy && item.SynergyObjectToSpawn != null ? item.SynergyObjectToSpawn : item.objectToSpawn;
            if (prefab == null)
                return;

            spawningRemote = true;
            try
            {
                Spawn(item, prefab, center, aim, angleFromAim);
            }
            finally
            {
                spawningRemote = false;
            }
        }

        // The same three ways DoSpawn creates the object, with the partner's position and aim.
        private static void Spawn(SpawnObjectPlayerItem item, GameObject prefab, Vector2 center, Vector2 aim, float angleFromAim)
        {
            if (prefab.GetComponent<Projectile>() != null)
            {
                // Projectile items: spawned at the player, pointing where they aimed, and fly off by themselves.
                Object.Instantiate(prefab, center, Quaternion.Euler(0f, 0f, BraveMathCollege.Atan2Degrees(aim)));
                return;
            }

            if (item.tossForce == 0f)
            {
                // Placed items (mines etc.): put down where the player stood.
                GameObject placed = (GameObject)Object.Instantiate(prefab, center, Quaternion.identity);
                tk2dBaseSprite ps = placed.GetComponent<tk2dBaseSprite>();
                if (ps != null)
                    ps.PlaceAtPositionByAnchor(new Vector3(center.x, center.y, ps.transform.position.z), (tk2dBaseSprite.Anchor)4);
                placed.transform.position = dfVectorExtensions.Quantize(placed.transform.position, 0.0625f);
                return;
            }

            // Thrown items (Molotovs, grenades...): tossed from the player toward where they aimed.
            Vector3 start = center;
            if (aim.y > 0f)
                start += Vector3.up * 0.25f;
            GameObject thrown = (GameObject)Object.Instantiate(prefab, start, Quaternion.identity);
            tk2dBaseSprite ts = thrown.GetComponent<tk2dBaseSprite>();
            if (ts != null)
                ts.PlaceAtPositionByAnchor(start, (tk2dBaseSprite.Anchor)4);
            Vector2 direction = Quaternion.Euler(0f, 0f, angleFromAim) * aim;
            DebrisObject debris = LootEngine.DropItemWithoutInstantiating(thrown, thrown.transform.position, direction, item.tossForce, false, false, true, false);
            if (debris == null)
                return;
            if (thrown.GetComponent<BlackHoleDoer>() != null)
            {
                debris.PreventFallingInPits = true;
                debris.PreventAbsorption = true;
            }
            if (aim.y > 0f)
            {
                debris.additionalHeightBoost = -1f;
                if (debris.sprite != null)
                    debris.sprite.UpdateZDepth();
            }
            debris.IsAccurateDebris = true;
            debris.Priority = (EphemeralObject.EphemeralPriority)0; // same value DoSpawn uses
            debris.bounceCount = item.canBounce ? 1 : 0;
        }
    }
}
