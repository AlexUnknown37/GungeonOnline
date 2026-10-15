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

        // ---- Steered bullets (guided rockets, homing, bouncing...) ----
        // A copied bullet flies straight from where it was fired. Bullets of ours that changed course since (the
        // player steering a rocket, homing, bounces) send where they are and where they're heading, 10x a second,
        // and the partner's copy follows.
        private class Tracked
        {
            public int Id;
            public float StartAngle;
            public Tracked(int id, float angle) { Id = id; StartAngle = angle; }
        }
        private static readonly Dictionary<Projectile, Tracked> ours = new Dictionary<Projectile, Tracked>();
        private static readonly Dictionary<int, Projectile> theirs = new Dictionary<int, Projectile>();
        private static int nextNetId;
        private static float nextCourseSendAt;
        private const int MaxCoursesPerSend = 24;

        public static void Update()
        {
            if (session == null || session.Status != NetSession.State.Connected)
            {
                ours.Clear();
                theirs.Clear();
                return;
            }
            if (Time.realtimeSinceStartup < nextCourseSendAt)
                return;
            nextCourseSendAt = Time.realtimeSinceStartup + 0.1f;
            if (theirs.Count > 0)
            {
                List<int> gone = null;
                foreach (KeyValuePair<int, Projectile> pair in theirs)
                {
                    if (pair.Value == null || !pair.Value.isActiveAndEnabled || !remoteProjectiles.Contains(pair.Value))
                    {
                        if (gone == null) gone = new List<int>();
                        gone.Add(pair.Key);
                    }
                }
                if (gone != null)
                    foreach (int id in gone) theirs.Remove(id);
            }
            if (ours.Count == 0)
                return;
            List<int> ids = new List<int>();
            List<Vector2> positions = new List<Vector2>();
            List<float> angles = new List<float>();
            List<float> speeds = new List<float>();
            List<Projectile> dead = null;
            foreach (KeyValuePair<Projectile, Tracked> pair in ours)
            {
                Projectile pr = pair.Key;
                if (pr == null || !pr.isActiveAndEnabled)
                {
                    if (dead == null) dead = new List<Projectile>();
                    dead.Add(pr);
                    continue;
                }
                float angle = Mathf.Atan2(pr.Direction.y, pr.Direction.x) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(angle, pair.Value.StartAngle)) < 6f)
                    continue; // still flying straight: the copy is where it should be
                if (ids.Count >= MaxCoursesPerSend)
                    continue;
                ids.Add(pair.Value.Id);
                positions.Add(pr.transform.position);
                angles.Add(angle);
                speeds.Add(pr.Speed);
            }
            if (dead != null)
                foreach (Projectile d in dead) ours.Remove(d);
            if (ids.Count == 0)
                return;
            string level = LevelSync.CurrentLevelKey();
            session.SendUnreliable(MsgType.Courses, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)ids.Count);
                for (int i = 0; i < ids.Count; i++)
                {
                    w.Write(ids[i]);
                    w.Write(positions[i].x); w.Write(positions[i].y);
                    w.Write(angles[i]);
                    w.Write(speeds[i]);
                }
            });
        }

        public static void OnCourses(BinaryReader r)
        {
            string level = r.ReadString();
            int n = r.ReadByte();
            bool here = GameManager.HasInstance && level == LevelSync.CurrentLevelKey();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
                float angle = r.ReadSingle();
                float speed = r.ReadSingle();
                Projectile pr;
                if (!here || !theirs.TryGetValue(id, out pr) || pr == null || !pr.isActiveAndEnabled)
                    continue;
                Vector3 t = pr.transform.position;
                pr.transform.position = new Vector3(pos.x, pos.y, t.z);
                if (pr.specRigidbody != null)
                    pr.specRigidbody.Reinitialize();
                float rad = angle * Mathf.Deg2Rad;
                pr.baseData.speed = speed;
                pr.SendInDirection(new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), true, false);
            }
        }

        public static bool IsRemote(Projectile p)
        {
            return p != null && remoteProjectiles.Contains(p);
        }

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
            int gunId = __instance.PickupObjectId;
            int netId = ++nextNetId;
            ours[p] = new Tracked(netId, angle);
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
                w.Write(gunId);
                w.Write(netId);
            });
        }

        // ---- The partner's gun sounds and shell casings ----
        // Bullets came across but the gun stayed silent. The partner's game plays the same gun's sound (the game
        // picks it with the gun's "switch group") and drops its shell casing, at the shot's position. A shotgun
        // fires many bullets at once, so one sound per 60 ms is enough.

        private static GameObject soundEmitter;
        private static float lastShotSoundAt;

        private static GameObject Emitter(Vector2 pos)
        {
            if (soundEmitter == null)
                soundEmitter = new GameObject("GungeonOnline Partner Sounds");
            soundEmitter.transform.position = new Vector3(pos.x, pos.y, 0f);
            return soundEmitter;
        }

        private static void PlayShotEffects(int gunId, Vector2 pos)
        {
            if (Time.realtimeSinceStartup - lastShotSoundAt < 0.06f)
                return;
            lastShotSoundAt = Time.realtimeSinceStartup;
            Gun gun = PickupObjectDatabase.GetById(gunId) as Gun;
            if (gun == null)
                return;
            try
            {
                GameObject emitter = Emitter(pos);
                if (!string.IsNullOrEmpty(gun.gunSwitchGroup))
                    AkSoundEngine.SetSwitch("WPN_Guns", gun.gunSwitchGroup, emitter);
                AkSoundEngine.PostEvent(string.IsNullOrEmpty(gun.OverrideNormalFireAudioEvent) ? "Play_WPN_gun_shot_01" : gun.OverrideNormalFireAudioEvent, emitter);
                if (gun.shellCasing != null)
                {
                    GameObject casing = SpawnManager.SpawnDebris(gun.shellCasing, new Vector3(pos.x, pos.y, pos.y), Quaternion.identity);
                    ShellCasing sc = casing != null ? casing.GetComponent<ShellCasing>() : null;
                    if (sc != null)
                        sc.Trigger();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Partner gun sound: " + e.Message);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Gun), "Reload")]
        private static void AfterReload(Gun __instance, bool __result)
        {
            if (!__result || session == null || session.Status != NetSession.State.Connected || !GameManager.HasInstance
                || __instance.CurrentOwner == null || __instance.CurrentOwner != GameManager.Instance.PrimaryPlayer)
                return;
            int gunId = __instance.PickupObjectId;
            Vector2 pos = __instance.transform.position;
            string level = LevelSync.CurrentLevelKey();
            session.SendUnreliable(MsgType.Reload, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(gunId); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnReload(BinaryReader r)
        {
            string level = r.ReadString();
            int gunId = r.ReadInt32();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (!GameManager.HasInstance || level != LevelSync.CurrentLevelKey())
                return;
            Gun gun = PickupObjectDatabase.GetById(gunId) as Gun;
            GameObject emitter = Emitter(pos);
            if (gun != null && !string.IsNullOrEmpty(gun.gunSwitchGroup))
                AkSoundEngine.SetSwitch("WPN_Guns", gun.gunSwitchGroup, emitter);
            AkSoundEngine.PostEvent("Play_WPN_gun_reload_01", emitter);
        }

        public static void OnShot(BinaryReader r)
        {
            string level = r.ReadString();
            string prefabName = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            float angle = r.ReadSingle();
            float speed = r.ReadSingle(), damage = r.ReadSingle(), range = r.ReadSingle(), scale = r.ReadSingle();
            int gunId = r.ReadInt32();
            int netId = r.ReadInt32();

            if (!GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null || level != LevelSync.CurrentLevelKey())
                return;
            PlayShotEffects(gunId, pos);
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
            theirs[netId] = p;

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

        public static GameObject FindPrefab(string name)
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
