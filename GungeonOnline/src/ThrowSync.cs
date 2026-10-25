using System.Collections.Generic;
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

            GameObject spawned;
            spawningRemote = true;
            try
            {
                spawned = Spawn(item, prefab, center, aim, angleFromAim);
            }
            finally
            {
                spawningRemote = false;
            }
            Harmless(spawned);
        }

        // Test (Y: a Bomb, Shift+Y: a Molotov): another player's throw, copied here, landing at our feet.
        public static void TestCopiedThrow(PlayerController me, string itemName)
        {
            SpawnObjectPlayerItem item = null;
            foreach (PickupObject o in PickupObjectDatabase.Instance.Objects)
            {
                SpawnObjectPlayerItem s = o as SpawnObjectPlayerItem;
                if (s != null && s.EncounterNameOrDisplayName == itemName && s.objectToSpawn != null)
                {
                    item = s;
                    break;
                }
            }
            if (item == null)
            {
                Plugin.Log.LogInfo("Test: no throwable item called " + itemName + ".");
                return;
            }
            Plugin.Log.LogInfo("Test: another player's " + itemName + " (copied) lands at our feet.");
            GameObject go;
            spawningRemote = true;
            try { go = Spawn(item, item.objectToSpawn, me.CenterPosition + new Vector2(-0.5f, 0f), new Vector2(0.05f, 0f), 0f); }
            finally { spawningRemote = false; }
            Harmless(go);
            GameManager.Instance.StartCoroutine(StandOnIt(me, go));
        }

        // Once it has landed, stand right on it (in the fire, or next to the bomb before it goes off).
        private static System.Collections.IEnumerator StandOnIt(PlayerController me, GameObject go)
        {
            float until = Time.realtimeSinceStartup + 0.9f;
            Vector2 last = go != null ? (Vector2)go.transform.position : me.CenterPosition;
            while (Time.realtimeSinceStartup < until)
            {
                if (go != null)
                    last = go.transform.position;
                yield return null;
            }
            if (me != null)
            {
                Plugin.Log.LogInfo("Test: standing on the copied throw at " + last + ".");
                me.WarpToPoint(last, false, false);
            }
        }

        // ---- Blasts from other players' throws ----
        // Without PvP, the blast of another player's grenade, mine or bomb hurts no player (enemies still take it in
        // their game). What it leaves behind - fire, poison and other pools - still hurts everyone, PvP or not.
        private class Thrown
        {
            public GameObject Go;
            public Vector2 Last;
            public float GoneAt = -1f;
        }
        private static readonly List<Thrown> thrown = new List<Thrown>();
        private const float BlastReach = 2.5f;   // a blast this close to a copied throw is that throw's
        private const float AfterGone = 2f;      // ...also just after it disappeared (some blow up as they vanish)

        private static void Harmless(GameObject go)
        {
            if (go == null || session.PvpEnabled || DuelSync.Active || BulletSync.TestOldBullets)
                return;
            Projectile p = go.GetComponent<Projectile>();
            if (p != null)
                BulletSync.TreatAsCopy(p);
            else
                BulletSync.SpareTeammatesIn(go);
            Thrown t = new Thrown();
            t.Go = go;
            t.Last = go.transform.position;
            thrown.Add(t);
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = thrown.Count - 1; i >= 0; i--)
            {
                Thrown t = thrown[i];
                if (t.Go != null)
                    t.Last = t.Go.transform.position;
                else if (t.GoneAt < 0f)
                    t.GoneAt = now;
                else if (now - t.GoneAt > AfterGone)
                    thrown.RemoveAt(i);
            }
        }

        // Blasts that use the game's shared default settings (not stored on the throw): spare players from a blast
        // right where a copied throw is, or just was.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Exploder), "Explode")]
        private static void BeforeExplode(Vector3 position, ref ExplosionData data)
        {
            if (thrown.Count == 0 || data == null || session == null || session.PvpEnabled || DuelSync.Active || !GameManager.HasInstance)
                return;
            float now = Time.realtimeSinceStartup;
            bool ours = false;
            foreach (Thrown t in thrown)
            {
                Vector2 at = t.Go != null ? (Vector2)t.Go.transform.position : t.Last;
                if ((t.Go != null || now - t.GoneAt <= AfterGone) && Vector2.Distance(at, position) <= BlastReach)
                {
                    ours = true;
                    break;
                }
            }
            if (!ours)
                return;
            // A copy of the settings, so the shared ones stay untouched for everyone else's blasts.
            ExplosionData spare = (ExplosionData)cloneMethod.Invoke(data, null);
            spare.ignoreList = data.ignoreList != null ? new List<SpeculativeRigidbody>(data.ignoreList) : new List<SpeculativeRigidbody>();
            foreach (PlayerController player in GameManager.Instance.AllPlayers)
            {
                if (player != null && player.specRigidbody != null && !spare.ignoreList.Contains(player.specRigidbody))
                    spare.ignoreList.Add(player.specRigidbody);
            }
            data = spare;
        }
        private static readonly System.Reflection.MethodInfo cloneMethod =
            typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // The same three ways DoSpawn creates the object, with the partner's position and aim.
        private static GameObject Spawn(SpawnObjectPlayerItem item, GameObject prefab, Vector2 center, Vector2 aim, float angleFromAim)
        {
            if (prefab.GetComponent<Projectile>() != null)
            {
                // Projectile items: spawned at the player, pointing where they aimed, and fly off by themselves.
                return (GameObject)Object.Instantiate(prefab, center, Quaternion.Euler(0f, 0f, BraveMathCollege.Atan2Degrees(aim)));
            }

            if (item.tossForce == 0f)
            {
                // Placed items (mines etc.): put down where the player stood.
                GameObject placed = (GameObject)Object.Instantiate(prefab, center, Quaternion.identity);
                tk2dBaseSprite ps = placed.GetComponent<tk2dBaseSprite>();
                if (ps != null)
                    ps.PlaceAtPositionByAnchor(new Vector3(center.x, center.y, ps.transform.position.z), (tk2dBaseSprite.Anchor)4);
                placed.transform.position = dfVectorExtensions.Quantize(placed.transform.position, 0.0625f);
                return placed;
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
                return thrown;
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
            return thrown;
        }
    }
}
