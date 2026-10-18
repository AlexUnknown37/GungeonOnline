using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Shows the other players' bullets: whenever one of our bullets is fired (by our gun, an item or a companion),
    // we tell the others which projectile it was (by prefab name), where it started, its direction, speed, damage,
    // range and how items changed it (BulletLook, BulletMotion). Their games spawn a copy there.
    // Copies are harmless: what a bullet does to enemies (damage, status effects) is decided in the shooter's game
    // and sent (EnemySync, EffectSync). Copies only hit players when the host turns on PvP.
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
            if (!spawningRemote && __0 != null && __result != null)
            {
                if (prefabOf.Count > 600)
                    prefabOf.Clear();
                prefabOf[__result] = __0;
            }
            // Projectiles are pooled: one that was the partner's copy may now be reused for a local shot.
            if (!spawningRemote && __result != null)
            {
                Projectile p = __result.GetComponent<Projectile>();
                if (p != null && remoteProjectiles.Remove(p))
                {
                    // It was a copy: nothing we put on it (bounces, lightning, circling...) may carry over.
                    BulletLook.Reset(p);
                    BulletMotion.Reset(p);
                }
            }
        }

        // ---- Bullets that items and companions make ----
        // A gun's own shots are sent as they're fired (AfterShootSingleProjectile). Items make more bullets
        // themselves (Scattershot's extra bullets, splitting bullets...), and companions shoot too; those never
        // went through a gun. Any bullet that gets one of our players (or their companion) as its owner is sent a
        // frame later, once its items have finished changing it.
        private static readonly Dictionary<GameObject, GameObject> prefabOf = new Dictionary<GameObject, GameObject>();
        private static readonly HashSet<Projectile> sent = new HashSet<Projectile>();
        private static readonly List<Projectile> pending = new List<Projectile>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Projectile), "set_Owner")]
        private static void AfterSetOwner(Projectile __instance, GameActor value)
        {
            if (spawningRemote || value == null || session == null || session.Status != NetSession.State.Connected
                || remoteProjectiles.Contains(__instance) || pending.Contains(__instance) || pending.Count > 200)
                return;
            if (ShotSlot(value) == Protocol.NoSlot)
                return;
            // Beams (Raiden Coil, lasers...) are bullets too, but have their own copying (BeamSync): a beam sent as a
            // plain shot broke every frame in the other games.
            if (__instance.GetComponent<BeamController>() != null)
                return;
            pending.Add(__instance);
        }

        // Which player a bullet's owner shoots for here: us, the host's stand-in bot, or neither.
        private static int ShotSlot(GameActor owner)
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            PlayerController bot = StandIn.Active ? StandIn.Player : null;
            AIActor companion = owner as AIActor;
            PlayerController player = companion != null ? companion.CompanionOwner : owner as PlayerController;
            if (player != null && player == me)
                return session.LocalSlot;
            if (player != null && player == bot)
                return StandIn.Slot;
            return Protocol.NoSlot;
        }

        private static void SendPending()
        {
            if (pending.Count == 0)
                return;
            for (int i = 0; i < pending.Count; i++)
            {
                Projectile p = pending[i];
                if (p == null || !p.isActiveAndEnabled || sent.Contains(p) || remoteProjectiles.Contains(p))
                    continue;
                GameObject prefab;
                if (!prefabOf.TryGetValue(p.gameObject, out prefab) || prefab == null)
                    continue;
                int slot = ShotSlot(p.Owner);
                if (slot == Protocol.NoSlot)
                    continue;
                SendShot(p, prefab, -1, slot != session.LocalSlot ? slot : -1);
            }
            pending.Clear();
            if (sent.Count > 600)
                sent.RemoveWhere(delegate (Projectile x) { return x == null || !x.isActiveAndEnabled; });
        }

        // Our bullets' tints (fire, poison, frost, charm... from items and synergies), as the game set them.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "ChangeTintColorShader")]
        private static void BeforeTint(Projectile __instance, Color __1)
        {
            if (!spawningRemote && !remoteProjectiles.Contains(__instance))
                BulletLook.Tinted(__instance, __1);
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
            public byte Motion;
            public Tracked(int id, float angle) { Id = id; StartAngle = angle; }
        }
        private static readonly Dictionary<Projectile, Tracked> ours = new Dictionary<Projectile, Tracked>();
        private static readonly Dictionary<int, Projectile> theirs = new Dictionary<int, Projectile>();

        // Bullet numbers are per player, so the copies are kept by sender and number.
        private static int TheirKey(int netId)
        {
            return netId * Protocol.MaxPlayers + Mathf.Clamp(session.From, 0, Protocol.MaxPlayers - 1);
        }
        private static int nextNetId;
        private static float nextCourseSendAt;
        private const int MaxCoursesPerSend = 24;

        public static void Update()
        {
            if (session == null || session.Status != NetSession.State.Connected)
            {
                ours.Clear();
                theirs.Clear();
                pending.Clear();
                return;
            }
            SendPending();
            BulletLook.Report();
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
                byte motion = BulletMotion.KindOf(pr);
                if (motion != pair.Value.Motion)
                {
                    pair.Value.Motion = motion;
                    motionChanged.Add(pair.Key);
                }
                if (motion == BulletMotion.Orbit)
                    continue; // the copy circles the shooter by itself
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
            SendMotionChanges();
            if (ids.Count == 0)
                return;
            int level = LevelSync.CurrentLevelCode();
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

        private static readonly List<Projectile> motionChanged = new List<Projectile>();

        private static void SendMotionChanges()
        {
            if (motionChanged.Count == 0)
                return;
            List<Projectile> changed = new List<Projectile>(motionChanged);
            motionChanged.Clear();
            int level = LevelSync.CurrentLevelCode();
            session.SendReliable(MsgType.Motion, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)Mathf.Min(changed.Count, 255));
                for (int i = 0; i < changed.Count && i < 255; i++)
                {
                    Tracked t;
                    w.Write(ours.TryGetValue(changed[i], out t) ? t.Id : -1);
                    BulletMotion.Write(w, changed[i]);
                }
            });
        }

        public static void OnMotion(BinaryReader r)
        {
            int level = r.ReadInt32();
            int n = r.ReadByte();
            bool here = GameManager.HasInstance && level == LevelSync.CurrentLevelCode();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                Projectile copy;
                if (!here || !theirs.TryGetValue(TheirKey(id), out copy) || copy == null || !copy.isActiveAndEnabled)
                    copy = null;
                BulletMotion.ReadAndApply(r, copy, session.From);
            }
        }

        public static void OnCourses(BinaryReader r)
        {
            int level = r.ReadInt32();
            int n = r.ReadByte();
            bool here = GameManager.HasInstance && level == LevelSync.CurrentLevelCode();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
                float angle = r.ReadSingle();
                float speed = r.ReadSingle();
                Projectile pr;
                if (!here || !theirs.TryGetValue(TheirKey(id), out pr) || pr == null || !pr.isActiveAndEnabled)
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

        // A copy of another player's bullet hitting something: what it breaks here, the shooter's game breaks too
        // and reports, so we don't report it again.
        public static bool RemoteBulletColliding;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Projectile), "OnRigidbodyCollision")]
        private static void BeforeRigidbodyCollision(Projectile __instance)
        {
            RemoteBulletColliding = remoteProjectiles.Contains(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Projectile), "OnRigidbodyCollision")]
        private static void AfterRigidbodyCollision()
        {
            RemoteBulletColliding = false;
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
            if (!GameManager.HasInstance || __instance.CurrentOwner == null)
                return;
            // Our own shots, or (host) the stand-in's, sent as the player it plays for.
            bool standIn = StandIn.Active && __instance.CurrentOwner == StandIn.Player;
            if (__instance.CurrentOwner != GameManager.Instance.PrimaryPlayer && !standIn)
                return;
            Projectile p = __instance.LastProjectile;
            if (p == null || lastPrefab == null || p.gameObject != lastSpawned)
                return;
            SendShot(p, lastPrefab, __instance.PickupObjectId, standIn ? StandIn.Slot : -1);
        }

        // Tell the others about one of our bullets (gunId -1: not fired by a gun, no gun sound).
        private static void SendShot(Projectile p, GameObject prefab, int gunId, int asSlot)
        {
            sent.Add(p);
            string prefabName = prefab.name;
            Vector2 pos = p.transform.position;
            float angle = p.transform.eulerAngles.z;
            float speed = p.baseData.speed, damage = p.baseData.damage, range = p.baseData.range;
            float scale = p.AdditionalScaleMultiplier;
            int netId = ++nextNetId;
            BulletLook look = BulletLook.Read(p, prefab);
            Tracked tracked = new Tracked(netId, angle);
            tracked.Motion = BulletMotion.KindOf(p);
            ours[p] = tracked;
            int level = LevelSync.CurrentLevelCode();
            // Unreliable on purpose: reliable messages wait for any lost one before them to be re-sent, so one
            // lost packet held back every later bullet, which then all came out at once (late "burst fire").
            // A bullet that's lost now is only missing from the partner's screen (damage is decided by
            // whoever's bullet it is).
            if (asSlot >= 0)
                Protocol.SendAs = asSlot;
            try
            {
            session.SendUnreliable(MsgType.Shot, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(Protocol.Hash(prefabName));
                Protocol.WriteQ(w, pos);
                w.Write((short)Mathf.RoundToInt(Mathf.Repeat(angle, 360f) * 10f));
                w.Write(speed);
                w.Write(damage);
                w.Write(range);
                w.Write(scale);
                w.Write(gunId);
                w.Write(netId);
                look.Write(w);
                BulletMotion.Write(w, p);
            });
            }
            finally
            {
                Protocol.SendAs = -1;
            }
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

        // A gun's own effects (muzzle flash, rings and bursts at the barrel, reload effects): the gun data has them,
        // so the copy plays the same ones where the shot or reload happened.
        private static void PlayGunVfx(VFXPool vfx, Vector2 pos, float angle)
        {
            if (vfx == null)
                return;
            try
            {
                vfx.SpawnAtPosition(new Vector3(pos.x, pos.y, 0f), angle, null, null, null, null, false, null, null, false);
            }
            catch (System.Exception e)
            {
                if (!vfxWarned)
                {
                    vfxWarned = true;
                    Plugin.Log.LogWarning("Couldn't play another player's gun effect: " + e.Message);
                }
            }
        }
        private static bool vfxWarned;

        private static void PlayShotEffects(int gunId, Vector2 pos, float angle)
        {
            if (Time.realtimeSinceStartup - lastShotSoundAt < 0.06f)
                return;
            if (gunId < 0)
                return; // not fired by a gun (an item's or a companion's bullet)
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
                PlayGunVfx(gun.muzzleFlashEffects, pos, angle);
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
            int level = LevelSync.CurrentLevelCode();
            session.SendUnreliable(MsgType.Reload, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(gunId); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnReload(BinaryReader r)
        {
            int level = r.ReadInt32();
            int gunId = r.ReadInt32();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (!GameManager.HasInstance || level != LevelSync.CurrentLevelCode())
                return;
            Gun gun = PickupObjectDatabase.GetById(gunId) as Gun;
            GameObject emitter = Emitter(pos);
            if (gun != null && !string.IsNullOrEmpty(gun.gunSwitchGroup))
                AkSoundEngine.SetSwitch("WPN_Guns", gun.gunSwitchGroup, emitter);
            AkSoundEngine.PostEvent("Play_WPN_gun_reload_01", emitter);
            if (gun != null)
                PlayGunVfx(gun.reloadEffects, pos, 0f);
        }

        public static void OnShot(BinaryReader r)
        {
            int level = r.ReadInt32();
            int prefabCode = r.ReadInt32();
            Vector2 pos = Protocol.ReadQ(r);
            float angle = r.ReadInt16() / 10f;
            float speed = r.ReadSingle(), damage = r.ReadSingle(), range = r.ReadSingle(), scale = r.ReadSingle();
            int gunId = r.ReadInt32();
            int netId = r.ReadInt32();
            BulletLook look = BulletLook.ReadFrom(r);

            if (!GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null || level != LevelSync.CurrentLevelCode())
                return;
            PlayShotEffects(gunId, pos, angle);
            GameObject prefab = FindPrefab(prefabCode);
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
            theirs[TheirKey(netId)] = p;

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
            // Bigger/smaller bullets (items): setting the number alone didn't resize the picture.
            float current = p.AdditionalScaleMultiplier > 0.01f ? p.AdditionalScaleMultiplier : 1f;
            if (Mathf.Abs(scale - current) > 0.01f)
                p.RuntimeUpdateScale(scale / current);
            look.Apply(p);
            BulletMotion.ReadAndApply(r, p, session.From);
            float rad = angle * Mathf.Deg2Rad;
            try
            {
                p.SendInDirection(new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)), true, true);
            }
            catch (System.Exception)
            {
                // Some bullets can't fly without their owner's setup: don't keep a broken copy around.
                remoteProjectiles.Remove(p);
                p.DieInAir(true, false, false, false);
            }
        }

        // Bullet types by the code of their name (shots send the code, not the name).
        private static readonly Dictionary<int, GameObject> prefabsByCode = new Dictionary<int, GameObject>();
        private static readonly HashSet<int> missingCodes = new HashSet<int>();

        public static GameObject FindPrefab(int code)
        {
            if (prefabsByName == null)
                BuildPrefabIndex();
            GameObject prefab;
            if (prefabsByCode.TryGetValue(code, out prefab) && prefab != null)
                return prefab;
            if (missingCodes.Contains(code))
                return null;
            // Not a gun's bullet (items and companions make their own): look through every loaded bullet prefab once.
            foreach (Projectile candidate in Resources.FindObjectsOfTypeAll<Projectile>())
            {
                if (candidate != null && !candidate.gameObject.scene.IsValid() && Protocol.Hash(candidate.name) == code)
                {
                    prefabsByCode[code] = candidate.gameObject;
                    return candidate.gameObject;
                }
            }
            missingCodes.Add(code);
            Plugin.Log.LogWarning("Another player fired a bullet type that isn't loaded here (code " + code + "); not shown.");
            return null;
        }

        public static GameObject FindPrefab(string name)
        {
            if (prefabsByName == null)
                BuildPrefabIndex();
            GameObject prefab;
            if (prefabsByName.TryGetValue(name, out prefab) && prefab != null)
                return prefab;
            if (missingPrefabs.Contains(name))
                return null;
            // Not a gun's bullet (items and companions make their own): look through every loaded bullet prefab once.
            foreach (Projectile candidate in Resources.FindObjectsOfTypeAll<Projectile>())
            {
                if (candidate != null && candidate.name == name && !candidate.gameObject.scene.IsValid())
                {
                    prefabsByName[name] = candidate.gameObject;
                    return candidate.gameObject;
                }
            }
            missingPrefabs.Add(name);
            Plugin.Log.LogWarning("Partner fired projectile '" + name + "', which isn't loaded here; not shown.");
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
            {
                prefabsByName[p.gameObject.name] = p.gameObject;
                prefabsByCode[Protocol.Hash(p.gameObject.name)] = p.gameObject;
            }
        }
    }
}
