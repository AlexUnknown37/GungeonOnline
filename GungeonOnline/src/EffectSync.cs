using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Status effects on enemies (freeze, charm, fire, poison, slow, bleed, cheese...) in every game.
    //
    // A bullet puts its effect on the enemy it hits (Projectile.HandleDamage -> GameActor.ApplyEffect). Only the
    // shooter's real bullet does that - copies of other players' bullets never apply effects - so a frozen or charmed
    // enemy was frozen or charmed only in the shooter's game. Now, when one of our bullets puts an effect on an enemy,
    // we tell the others which enemy and which effect; they put the same effect on their copy. Damage over time
    // (burning, poison) then ticks the same in every game, like other damage whose cause every game has.
    //
    // Effects are game data that belong to guns' bullets and to items, the same in every game. Each game indexes
    // them once (type, name, duration) and finds "the same effect" by that - so modded items' effects work too.
    public static class EffectSync
    {
        private static NetSession session;
        private static bool applyingRemote;
        public static int Sent, Applied, NotFound;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(EffectSync));
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameActor), "ApplyEffect")]
        private static void BeforeApplyEffect(GameActor __instance, GameActorEffect __0, float __1)
        {
            if (applyingRemote || !Connected || !EnemySync.LocalBulletHitting || __0 == null)
                return;
            AIActor enemy = __instance as AIActor;
            int id;
            if (enemy == null || enemy.CompanionOwner != null || !EnemySync.TryGetId(enemy, out id))
                return;
            string type = __0.GetType().Name;
            string name = __0.effectIdentifier ?? "";
            // A fast gun puts the same effect on the same enemy many times a second: tell the others at most twice a
            // second per enemy and effect (each copy lasts its full time anyway).
            string key = id + "|" + type + "|" + name;
            float now = Time.realtimeSinceStartup, at;
            if (lastSent.TryGetValue(key, out at) && now - at < 0.5f)
                return;
            if (lastSent.Count > 500)
                lastSent.Clear();
            lastSent[key] = now;
            int level = LevelSync.CurrentLevelCode();
            int kind = EnemySync.KindOf(enemy);
            float duration = __0.duration, amount = __1;
            Sent++;
            session.SendReliable(MsgType.EnemyEffect, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(id);
                w.Write(kind);
                w.Write(type);
                w.Write(name);
                w.Write(duration);
                w.Write(amount);
            });
        }

        private static readonly Dictionary<string, float> lastSent = new Dictionary<string, float>();

        public static void OnEnemyEffect(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            int kind = r.ReadInt32();
            string type = r.ReadString();
            string name = r.ReadString();
            float duration = r.ReadSingle();
            float amount = r.ReadSingle();
            if (!GameManager.HasInstance || level != LevelSync.CurrentLevelCode())
                return;
            AIActor enemy = EnemySync.ById(id);
            if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead || EnemySync.KindOf(enemy) != kind)
                return;
            GameActorEffect effect = Find(type, name, duration);
            if (effect == null)
            {
                NotFound++;
                if (missing.Add(type + "/" + name))
                    Plugin.Log.LogWarning("Another player's " + type + " '" + name + "' isn't known here; not copied onto " + enemy.name + ".");
                return;
            }
            applyingRemote = true;
            try
            {
                enemy.ApplyEffect(effect, amount, null);
                Applied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't copy " + type + " onto " + enemy.name + ": " + e.Message);
            }
            finally
            {
                applyingRemote = false;
            }
        }
        private static readonly HashSet<string> missing = new HashSet<string>();

        // ---- Finding the same effect here ----

        private static Dictionary<string, GameActorEffect> exact;   // type|name|duration
        private static Dictionary<string, GameActorEffect> byName;  // type|name
        private static Dictionary<string, GameActorEffect> byType;  // type
        private static float lastIndexAt = -1000f;

        private static GameActorEffect Find(string type, string name, float duration)
        {
            if (exact == null)
                Index();
            GameActorEffect e;
            if (exact.TryGetValue(type + "|" + name + "|" + duration.ToString("0.###"), out e))
                return e;
            if (byName.TryGetValue(type + "|" + name, out e))
                return e;
            // Something new (another mod's item, or an effect made while playing): look again, at most every 10 s.
            if (Time.realtimeSinceStartup - lastIndexAt > 10f)
            {
                Index();
                if (exact.TryGetValue(type + "|" + name + "|" + duration.ToString("0.###"), out e) || byName.TryGetValue(type + "|" + name, out e))
                    return e;
            }
            // Same kind of effect at least (another fire for a fire), so the enemy still burns/freezes/... here.
            return byType.TryGetValue(type, out e) ? e : null;
        }

        private static void Index()
        {
            lastIndexAt = Time.realtimeSinceStartup;
            exact = new Dictionary<string, GameActorEffect>();
            byName = new Dictionary<string, GameActorEffect>();
            byType = new Dictionary<string, GameActorEffect>();
            int found = 0;
            foreach (Projectile p in Resources.FindObjectsOfTypeAll<Projectile>())
                found += Scan(p);
            if (PickupObjectDatabase.Instance != null)
            {
                foreach (PickupObject item in PickupObjectDatabase.Instance.Objects)
                {
                    if (item == null)
                        continue;
                    foreach (MonoBehaviour b in item.GetComponentsInChildren<MonoBehaviour>(true))
                        found += Scan(b);
                }
            }
            Plugin.Log.LogInfo("Indexed " + exact.Count + " status effects (" + found + " found on bullets and items).");
        }

        // Effect fields (and lists of effects) on one component.
        private static readonly Dictionary<Type, FieldInfo[]> effectFields = new Dictionary<Type, FieldInfo[]>();

        private static int Scan(object o)
        {
            if (o == null)
                return 0;
            FieldInfo[] fields;
            Type t = o.GetType();
            if (!effectFields.TryGetValue(t, out fields))
            {
                List<FieldInfo> list = new List<FieldInfo>();
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (typeof(GameActorEffect).IsAssignableFrom(f.FieldType) || typeof(IList).IsAssignableFrom(f.FieldType)
                        && f.FieldType.IsGenericType && typeof(GameActorEffect).IsAssignableFrom(f.FieldType.GetGenericArguments()[0]))
                        list.Add(f);
                }
                fields = list.ToArray();
                effectFields[t] = fields;
            }
            int n = 0;
            foreach (FieldInfo f in fields)
            {
                object v;
                try { v = f.GetValue(o); }
                catch (Exception) { continue; }
                IList many = v as IList;
                if (many != null)
                {
                    foreach (object e in many)
                        n += Add(e as GameActorEffect);
                }
                else
                    n += Add(v as GameActorEffect);
            }
            return n;
        }

        private static int Add(GameActorEffect e)
        {
            if (e == null)
                return 0;
            string type = e.GetType().Name;
            string name = e.effectIdentifier ?? "";
            string key = type + "|" + name + "|" + e.duration.ToString("0.###");
            if (!exact.ContainsKey(key))
                exact[key] = e;
            if (!byName.ContainsKey(type + "|" + name))
                byName[type + "|" + name] = e;
            if (!byType.ContainsKey(type))
                byType[type] = e;
            return 1;
        }
    }
}
