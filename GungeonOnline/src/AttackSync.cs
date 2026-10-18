using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Enemies attack the same way in every game.
    //
    // Every game runs its own copy of each enemy, and each copy used to pick its own attacks (at random, at its own
    // moment, at its own target): bosses started different patterns in each game. Now one game leads each room's
    // enemies - the one EnemySync puts in charge (the lowest-numbered living player in the room, the host first).
    // When an enemy starts an attack there, the leader tells the others which attack (its place in the enemy's list
    // of attacks), at whom, from where, plus a random seed. The other games never start attacks on their own: they
    // start exactly that attack when told, from the same spot, at the same player, with the same dice.
    //
    // How: every attack's Update (the game's "start this attack if you can" step) is patched. On the leader it runs
    // as usual and we report the ones that started; elsewhere it is skipped, except when we replay a reported one,
    // in which case its readiness checks (cooldowns, range) are waved through.
    public static class AttackSync
    {
        private static NetSession session;

        private static readonly FieldInfo actorField = AccessTools.Field(typeof(BehaviorBase), "m_aiActor");
        private static readonly FieldInfo activeField = AccessTools.Field(typeof(BehaviorSpeculator), "m_activeContinuousBehavior");
        private static readonly FieldInfo classActiveField = AccessTools.Field(typeof(BehaviorSpeculator), "m_classSpecificContinuousBehavior");
        private static readonly MethodInfo endContinuous = AccessTools.Method(typeof(BehaviorSpeculator), "EndContinuousBehavior");
        private static readonly MethodInfo endClassContinuous = AccessTools.Method(typeof(BehaviorSpeculator), "EndClassSpecificContinuousBehavior");

        // Each enemy's attacks in the game's own order (the same in every game).
        private static readonly Dictionary<AIActor, List<AttackBehaviorBase>> attacksOf = new Dictionary<AIActor, List<AttackBehaviorBase>>();

        private static int depth;          // attack Updates running inside each other (a leaf calls its base Update)
        private static bool replaying;     // we're starting an attack the leader reported
        private static bool seeded;        // we swapped in a random seed for this Update
        private static int seed;
        private static int lastFrame;

        public static int Started, Replayed, Missed;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            HarmonyMethod pre = new HarmonyMethod(typeof(AttackSync), "BeforeAttackUpdate");
            HarmonyMethod post = new HarmonyMethod(typeof(AttackSync), "AfterAttackUpdate");
            HarmonyMethod ready = new HarmonyMethod(typeof(AttackSync), "BeforeIsReady");
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type[] types;
            try { types = typeof(AttackBehaviorBase).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            int updates = 0, readies = 0;
            foreach (Type t in types)
            {
                if (t == null || !typeof(AttackBehaviorBase).IsAssignableFrom(t) || typeof(IAttackBehaviorGroup).IsAssignableFrom(t))
                    continue;
                MethodInfo u = t.GetMethod("Update", flags, null, Type.EmptyTypes, null);
                if (u != null && !u.IsAbstract)
                {
                    try { harmony.Patch(u, pre, post); updates++; }
                    catch (Exception e) { Plugin.Log.LogWarning("AttackSync: couldn't patch " + t.Name + ".Update: " + e.Message); }
                }
                MethodInfo r = t.GetMethod("IsReady", flags, null, Type.EmptyTypes, null);
                if (r != null && !r.IsAbstract)
                {
                    try { harmony.Patch(r, ready); readies++; }
                    catch (Exception e) { Plugin.Log.LogWarning("AttackSync: couldn't patch " + t.Name + ".IsReady: " + e.Message); }
                }
            }
            Plugin.Log.LogInfo("AttackSync: watching " + updates + " kinds of enemy attack.");
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static AIActor ActorOf(BehaviorBase b)
        {
            return actorField != null ? actorField.GetValue(b) as AIActor : null;
        }

        private static List<AttackBehaviorBase> AttacksOf(AIActor a)
        {
            List<AttackBehaviorBase> list;
            if (attacksOf.TryGetValue(a, out list))
                return list;
            list = new List<AttackBehaviorBase>();
            BehaviorSpeculator spec = a.behaviorSpeculator;
            if (spec != null && spec.AttackBehaviors != null)
            {
                foreach (AttackBehaviorBase top in spec.AttackBehaviors)
                    Collect(top, list);
            }
            attacksOf[a] = list;
            return list;
        }

        private static void Collect(AttackBehaviorBase b, List<AttackBehaviorBase> list)
        {
            if (b == null)
                return;
            IAttackBehaviorGroup g = b as IAttackBehaviorGroup;
            if (g == null)
            {
                list.Add(b);
                return;
            }
            for (int i = 0; i < g.Count; i++)
                Collect(g.GetAttackBehavior(i), list);
        }

        // ---- Patches ----

        private static bool BeforeAttackUpdate(BehaviorBase __instance, ref BehaviorResult __result, out bool __state)
        {
            __state = false;
            if (Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount; // an exception inside an attack can't leave this stuck
                depth = 0;
            }
            depth++;
            __state = true;
            if (depth > 1 || replaying || !Connected)
                return true;
            AIActor a = ActorOf(__instance);
            int id;
            if (a == null || !EnemySync.TryGetId(a, out id) || AttacksOf(a).IndexOf((AttackBehaviorBase)__instance) < 0)
                return true;
            if (!EnemySync.LeadsEnemy(a))
            {
                // Not ours to decide: wait for the leader's game to say this attack starts.
                __result = BehaviorResult.Continue;
                return false;
            }
            // Roll the dice from a seed we can send along, so the copy rolls the same.
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            UnityEngine.Random.InitState(seed);
            seeded = true;
            return true;
        }

        private static void AfterAttackUpdate(BehaviorBase __instance, BehaviorResult __result, bool __state)
        {
            if (!__state)
                return;
            depth--;
            if (depth > 0 || replaying || !seeded)
                return;
            seeded = false;
            if (__result == BehaviorResult.Continue)
                return;
            AIActor a = ActorOf(__instance);
            int id;
            if (a == null || !EnemySync.TryGetId(a, out id))
                return;
            int index = AttacksOf(a).IndexOf((AttackBehaviorBase)__instance);
            if (index < 0)
                return;
            Started++;
            Send(a, id, index, (byte)__result, seed);
        }

        private static bool BeforeIsReady(ref bool __result)
        {
            if (!replaying)
                return true;
            __result = true;
            return false;
        }

        // ---- Messages ----

        private static void Send(AIActor a, int id, int index, byte result, int diceSeed)
        {
            int level = LevelSync.CurrentLevelCode();
            Vector2 pos = a.specRigidbody != null ? a.specRigidbody.UnitCenter : (Vector2)a.transform.position;
            byte target = (byte)TargetSync.SlotOfTarget(a);
            session.SendReliable(MsgType.AttackStart, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(id);
                w.Write(a.EnemyGuid ?? "");
                w.Write((ushort)index);
                w.Write(target);
                w.Write(pos.x); w.Write(pos.y);
                w.Write(diceSeed);
            });
        }

        public static void OnAttackStart(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            string guid = r.ReadString();
            int index = r.ReadUInt16();
            int target = r.ReadByte();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            int diceSeed = r.ReadInt32();
            if (level != LevelSync.CurrentLevelCode())
                return;
            AIActor a = EnemySync.ById(id);
            if (a == null || a.EnemyGuid != guid || a.healthHaver == null || a.healthHaver.IsDead || EnemySync.LeadsEnemy(a))
                return;
            BehaviorSpeculator spec = a.behaviorSpeculator;
            List<AttackBehaviorBase> attacks = AttacksOf(a);
            if (spec == null || index >= attacks.Count)
            {
                Missed++;
                return;
            }
            AttackBehaviorBase attack = attacks[index];

            // Same spot, same target as in the leader's game.
            EnemySync.PlaceAt(a, pos);
            TargetSync.Force(a, target);

            try
            {
                // Whatever this copy was still doing ends now (the leader has moved on).
                if (activeField.GetValue(spec) != null)
                    endContinuous.Invoke(spec, null);
                IDictionary classActive = classActiveField.GetValue(spec) as IDictionary;
                if (classActive != null && classActive.Contains(spec.AttackBehaviors))
                    endClassContinuous.Invoke(spec, new object[] { spec.AttackBehaviors });

                UnityEngine.Random.State saved = UnityEngine.Random.state;
                UnityEngine.Random.InitState(diceSeed);
                BehaviorResult result;
                replaying = true;
                try { result = attack.Update(); }
                finally { replaying = false; }
                // Back to the game's own dice afterwards.
                UnityEngine.Random.state = saved;

                if (result == BehaviorResult.RunContinuous)
                    activeField.SetValue(spec, attack);
                else if (result == BehaviorResult.RunContinuousInClass && classActive != null)
                    classActive[spec.AttackBehaviors] = attack;
                if (result == BehaviorResult.Continue)
                    Missed++;
                else
                    Replayed++;
            }
            catch (Exception e)
            {
                Missed++;
                Plugin.Log.LogWarning("Couldn't copy " + a.name + "'s attack " + attack.GetType().Name + ": " + e.Message);
            }
        }

        // Forget enemies that are gone.
        public static void Update()
        {
            if (attacksOf.Count > 200)
            {
                List<AIActor> gone = new List<AIActor>();
                foreach (AIActor a in attacksOf.Keys)
                {
                    if (a == null)
                        gone.Add(a);
                }
                foreach (AIActor a in gone)
                    attacksOf.Remove(a);
            }
        }
    }
}
