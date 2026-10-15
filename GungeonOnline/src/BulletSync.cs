using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Shows the partner's bullets: whenever our own gun fires a projectile, we tell the partner which
    // projectile it was (by prefab name), where it started, its direction, speed, damage and range.
    // Their game spawns the same projectile there.
    //
    // Enemies aren't synced yet, so each game has its own copy of every enemy. Partner bullets do hit
    // the enemies in your game: both games then see both players' shots, which keeps the two copies of
    // each fight roughly alike until real enemy sync arrives. They only hit you when the host turns on PvP.
    public static class BulletSync
    {
        private static NetSession session;

        // SpawnManager.SpawnProjectile -> Gun.ShootSingleProjectile: remember which prefab was just spawned.
        private static GameObject lastPrefab;
        private static GameObject lastSpawned;
        private static bool spawningRemote;

        // Prefab name -> prefab, built from every gun in the game the first time the partner shoots.
        private static Dictionary<string, GameObject> prefabsByName;
        private static readonly HashSet<string> missingPrefabs = new HashSet<string>();

        private static readonly FieldInfo ownerField = AccessTools.Field(typeof(Projectile), "m_owner");
        private static readonly FieldInfo shooterField = AccessTools.Field(typeof(Projectile), "m_shooter");

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(BulletSync));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SpawnManager), "SpawnProjectile", new System.Type[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(bool) })]
        private static void AfterSpawnProjectile(GameObject __0, GameObject __result)
        {
            lastPrefab = __0;
            lastSpawned = __result;
            // Projectiles are pooled: one that was the partner's copy may now be reused for a local shot.
            if (!spawningRemote && __result != null)
            {
                Projectile p = __result.GetComponent<Projectile>();
                if (p != null)
                    remoteProjectiles.Remove(p);
            }
        }

        // The partner's copied bullets: their damage to enemies comes over the network (EnemySync) instead.
        private static readonly HashSet<Projectile> remoteProjectiles = new HashSet<Projectile>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "HandleDamage")]
        private static void BeforeHandleDamage(Projectile __instance)
        {
            bool remote = remoteProjectiles.Contains(__instance);
            EnemySync.RemoteBulletHitting = remote;
            EnemySync.LocalBulletHitting = !remote;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Projectile), "HandleDamage")]
        private static void AfterHandleDamage()
        {
            EnemySync.RemoteBulletHitting = false;
            EnemySync.LocalBulletHitting = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Gun), "ShootSingleProjectile")]
        private static void AfterShootSingleProjectile(Gun __instance)
        {
            if (spawningRemote || session == null || session.Status != NetSession.State.Connected)
                return;
            if (!GameManager.HasInstance || __instance.CurrentOwner == null
                || __instance.CurrentOwner != GameManager.Instance.PrimaryPlayer)
                return;
            Projectile p = __instance.LastProjectile;
            if (p == null || lastPrefab == null || p.gameObject != lastSpawned)
                return;

            string prefabName = lastPrefab.name;
            Vector2 pos = p.transform.position;
            float angle = p.transform.eulerAngles.z;
            float speed = p.baseData.speed, damage = p.baseData.damage, range = p.baseData.range;
            float scale = p.AdditionalScaleMultiplier;
            string level = LevelSync.CurrentLevelKey();
            // Unreliable on purpose: reliable messages wait for any lost one before them to be re-sent, so one
            // lost packet held back every later bullet, which then all came out at once (late "burst fire").
            // A bullet that's lost now is only missing from the partner's screen (damage is decided by
            // whoever's bullet it is).
            session.SendUnreliable(MsgType.Shot, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(prefabName);
                w.Write(pos.x); w.Write(pos.y);
                w.Write(angle);
                w.Write(speed);
                w.Write(damage);
                w.Write(range);
                w.Write(scale);
            });
        }

        public static void OnShot(BinaryReader r)
        {
            string level = r.ReadString();
            string prefabName = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            float angle = r.ReadSingle();
            float speed = r.ReadSingle(), damage = r.ReadSingle(), range = r.ReadSingle(), scale = r.ReadSingle();

            if (!GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null || level != LevelSync.CurrentLevelKey())
                return;
            GameObject prefab = FindPrefab(prefabName);
            if (prefab == null)
                return;

            GameObject spawned;
            spawningRemote = true;
            try
            {
                spawned = SpawnManager.SpawnProjectile(prefab, pos, Quaternion.Euler(0f, 0f, angle), true);
            }
            finally
            {
                spawningRemote = false;
            }
            Projectile p = spawned != null ? spawned.GetComponent<Projectile>() : null;
            if (p == null)
                return;

            remoteProjectiles.Add(p);

            // Pooled projectiles keep their last owner; make sure this one belongs to nobody here,
            // so it doesn't trigger our items or count as our kill.
            ownerField.SetValue(p, null);
            shooterField.SetValue(p, null);
            // PvP (host setting): the partner's bullets can hit us, like an enemy's.
            p.collidesWithPlayer = session.PvpEnabled;
            p.collidesWithEnemies = true;
            p.baseData.speed = speed;
            p.baseData.damage = damage;
            p.baseData.range = range;
            p.AdditionalScaleMultiplier = scale;
            float rad = angle * Mathf.Deg2Rad;
            p.SendInDirection(new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), true, true);
        }

        private static GameObject FindPrefab(string name)
        {
            if (prefabsByName == null)
                BuildPrefabIndex();
            GameObject prefab;
            if (prefabsByName.TryGetValue(name, out prefab) && prefab != null)
                return prefab;
            if (missingPrefabs.Add(name))
                Plugin.Log.LogWarning("Partner fired projectile '" + name + "', which isn't a known gun projectile; not shown.");
            return null;
        }

        // Every projectile any gun can fire: normal, charged, critical, alternate and reload volleys.
        private static void BuildPrefabIndex()
        {
            prefabsByName = new Dictionary<string, GameObject>();
            int guns = 0;
            foreach (PickupObject item in PickupObjectDatabase.Instance.Objects)
            {
                Gun gun = item as Gun;
                if (gun == null)
                    continue;
                guns++;
                AddModule(gun.singleModule);
                AddVolley(gun.RawSourceVolley);
                AddVolley(gun.alternateVolley);
                AddVolley(gun.rawOptionalReloadVolley);
                AddProjectile(gun.CriticalReplacementProjectile);
            }
            Plugin.Log.LogInfo("Indexed " + prefabsByName.Count + " projectile types from " + guns + " guns.");
        }

        private static void AddVolley(ProjectileVolleyData volley)
        {
            if (volley == null || volley.projectiles == null)
                return;
            foreach (ProjectileModule module in volley.projectiles)
                AddModule(module);
        }

        private static void AddModule(ProjectileModule module)
        {
            if (module == null)
                return;
            if (module.projectiles != null)
            {
                foreach (Projectile p in module.projectiles)
                    AddProjectile(p);
            }
            if (module.chargeProjectiles != null)
            {
                foreach (ProjectileModule.ChargeProjectile c in module.chargeProjectiles)
                {
                    if (c != null)
                        AddProjectile(c.Projectile);
                }
            }
        }

        private static void AddProjectile(Projectile p)
        {
            if (p != null && !prefabsByName.ContainsKey(p.gameObject.name))
                prefabsByName[p.gameObject.name] = p.gameObject;
        }
    }
}
