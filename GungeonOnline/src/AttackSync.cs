using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Brave.BulletScript;
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

            // The same dice for what an attack fires afterwards (see "Dice for bullet patterns" below).
            PatchDice(harmony, AccessTools.Method(typeof(Bullet), "DoTick"), "BeforeBulletTick", "AfterBulletTick");
            PatchDice(harmony, AccessTools.Method(typeof(Bullet), "Fire",
                new Type[] { typeof(Offset), typeof(Direction), typeof(Speed), typeof(Bullet) }), "BeforeBulletFire", null);
            PatchDice(harmony, AccessTools.Method(typeof(ShootGunBehavior), "Fire"), "BeforeLaterRoll", "AfterLaterRoll");
            PatchDice(harmony, AccessTools.Method(typeof(TeleportBehavior), "DoTeleport"), "BeforeLaterRoll", "AfterLaterRoll");
        }

        private static void PatchDice(Harmony harmony, MethodInfo target, string prefix, string finalizer)
        {
            if (target == null)
            {
                Plugin.Log.LogWarning("AttackSync: " + prefix + " has nothing to hook; enemy patterns may differ between games.");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(AttackSync), prefix),
                    finalizer: finalizer != null ? new HarmonyMethod(typeof(AttackSync), finalizer) : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("AttackSync: couldn't hook " + target.DeclaringType.Name + "." + target.Name + ": " + e.Message);
            }
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
            leaderDice = UnityEngine.Random.state; // back to the game's own dice after this Update
            UnityEngine.Random.InitState(seed);
            seeded = true;
            updatingActor = a;
            updatingRoots = 0;
            updatingShots = 0;
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
            updatingActor = null;
            UnityEngine.Random.state = leaderDice;
            if (__result == BehaviorResult.Continue)
                return;
            AIActor a = ActorOf(__instance);
            int id;
            if (a == null || !EnemySync.TryGetId(a, out id))
                return;
            int index = AttacksOf(a).IndexOf((AttackBehaviorBase)__instance);
            if (index < 0)
                return;
            // What this attack fires over the next seconds rolls from the same seed (see below).
            Dice started = new Dice();
            started.Seed = seed;
            started.Roots = updatingRoots;
            started.Shots = updatingShots;
            diceOf[a] = started;
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

            // The game never runs behaviours on a copy that isn't started and awake (BehaviorSpeculator.Update): its
            // attacks have no enemy yet and it has no shadow. Running one anyway threw inside the attack (seen with
            // teleporting enemies) after the copy had already been moved.
            if (!a.isActiveAndEnabled || !spec.isActiveAndEnabled || !a.HasBeenAwoken
                || ActorOf(attack) == null || (a.HasShadow && a.ShadowObject == null))
            {
                Missed++;
                return;
            }

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

                Dice copied = new Dice();
                copied.Seed = diceSeed;
                diceOf[a] = copied;
                UnityEngine.Random.State saved = UnityEngine.Random.state;
                UnityEngine.Random.InitState(diceSeed);
                BehaviorResult result;
                replaying = true;
                try { result = attack.Update(); }
                finally
                {
                    replaying = false;
                    // Back to the game's own dice afterwards (also when the attack threw).
                    UnityEngine.Random.state = saved;
                }

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
                // With where it happened (the first lines of the trace): "object reference not set" alone says nothing.
                string where = e.StackTrace ?? "";
                if (where.Length > 400)
                    where = where.Substring(0, 400);
                Plugin.Log.LogWarning("Couldn't copy " + a.name + "'s attack " + attack.GetType().Name + ": " + e.Message + " " + where.Replace("\r", "").Replace("\n", " | "));
            }
        }

        // Is an attack that owns the whole enemy running (a charge, a leap, a teleport...)? Shooting while walking is
        // not one: the game runs that kind next to the enemy's movement ("class specific").
        public static bool HasGlobalAttack(BehaviorSpeculator spec)
        {
            return spec != null && activeField != null && activeField.GetValue(spec) is AttackBehaviorBase;
        }

        // ---- Dice for bullet patterns ----
        // The seed sent with an attack used to cover one call: the attack's Update. Almost nothing an enemy fires is
        // rolled there. Bullet patterns run as scripts over the following seconds (one step per 1/60 s of game
        // time), gun enemies fire a few frames later, and the spread of an enemy's gun doesn't use the engine's dice
        // at all but one counter shared by every gun in the game. So "the same attack" still sprayed differently
        // in every game. Now:
        // - every step of a pattern started by a synced attack rolls from (the attack's seed, which script, which
        //   step). Steps are game-time steps, so they line up between games at any frame rate, and a step that rolls
        //   once more or less in one game cannot shift the steps after it;
        // - a gun enemy's shot and a teleport's landing spot roll from the attack's seed too, and the spread counter
        //   is set from it for that shot;
        // - the game's own dice are put back after each, so loot and effects are untouched.
        // Aimed patterns still aim at where each game sees the target; that part cannot match.
        private class Dice
        {
            public int Seed, Roots, Shots;
        }
        private static readonly Dictionary<AIActor, Dice> diceOf = new Dictionary<AIActor, Dice>();
        private static readonly Dictionary<Bullet, int> keyOf = new Dictionary<Bullet, int>();
        private static readonly Dictionary<Bullet, int> firedBy = new Dictionary<Bullet, int>();
        private static AIActor updatingActor;  // leader: the enemy whose attack Update runs with `seed` right now
        private static int updatingRoots, updatingShots;
        private static UnityEngine.Random.State leaderDice;
        private static readonly FieldInfo spreadCounter = AccessTools.Field(typeof(ProjectileModule), "m_angleVarianceIterator");

        public struct SavedDice
        {
            public bool On;
            public UnityEngine.Random.State Saved;
            public int Spread;
        }

        private static int Mix(int a, int b)
        {
            unchecked
            {
                uint h = ((uint)a * 0x9E3779B1u) ^ (uint)b;
                h ^= h >> 15;
                h *= 0x85EBCA77u;
                h ^= h >> 13;
                return (int)h;
            }
        }

        // The seed of the attack this enemy is in, with a running number so a second script or shot in the same
        // attack doesn't repeat the first.
        private static bool NextSeed(AIActor a, bool shot, out int s)
        {
            s = 0;
            if (a == null)
                return false;
            if (seeded && updatingActor == a)
            {
                s = Mix(seed, shot ? 0x5107 + updatingShots++ : updatingRoots++);
                return true;
            }
            Dice d;
            if (!diceOf.TryGetValue(a, out d))
                return false;
            s = Mix(d.Seed, shot ? 0x5107 + d.Shots++ : d.Roots++);
            return true;
        }

        private static void BeforeBulletTick(Bullet __instance, out SavedDice __state)
        {
            __state = new SavedDice();
            if (!Connected)
                return;
            int key;
            if (!keyOf.TryGetValue(__instance, out key))
            {
                // A script's first step: give it its key. (Bullets a script fires get theirs in BeforeBulletFire.)
                if (__instance.Tick != 0 || !(__instance is Script))
                    return;
                AIBulletBank bank = __instance.BulletManager as AIBulletBank;
                AIActor a = bank != null ? bank.aiActor : null;
                if (a == null && bank != null)
                    a = bank.GetComponentInParent<AIActor>();
                int s;
                if (!NextSeed(a, false, out s))
                    return;
                key = Mix(s, Protocol.Hash(__instance.GetType().Name));
                if (keyOf.Count > 2000)
                    PruneKeys();
                keyOf[__instance] = key;
            }
            __state.On = true;
            __state.Saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(Mix(key, __instance.Tick));
        }

        private static Exception AfterBulletTick(Bullet __instance, Exception __exception, SavedDice __state)
        {
            if (__state.On)
            {
                UnityEngine.Random.state = __state.Saved; // a step inside a step gets the outer one's dice back
                if (__instance.Destroyed || __instance.IsEnded)
                {
                    keyOf.Remove(__instance);
                    firedBy.Remove(__instance);
                }
            }
            return __exception;
        }

        // Bullets with a script of their own get a key from the bullet that fires them.
        private static void BeforeBulletFire(Bullet __instance, Bullet __3)
        {
            int key;
            if (__3 == null || __3.GetType() == typeof(Bullet) || !keyOf.TryGetValue(__instance, out key))
                return;
            int n;
            firedBy.TryGetValue(__instance, out n);
            firedBy[__instance] = n + 1;
            keyOf[__3] = Mix(key, n + 1);
        }

        private static void PruneKeys()
        {
            List<Bullet> gone = new List<Bullet>();
            foreach (Bullet b in keyOf.Keys)
            {
                if (b.Destroyed || b.IsEnded)
                    gone.Add(b);
            }
            foreach (Bullet b in gone)
            {
                keyOf.Remove(b);
                firedBy.Remove(b);
            }
            if (keyOf.Count > 2000)
            {
                keyOf.Clear();
                firedBy.Clear();
            }
        }

        // A gun enemy's shot (ShootGunBehavior.Fire) and a teleport's landing spot (TeleportBehavior.DoTeleport) are
        // rolled a few frames after the attack starts.
        private static void BeforeLaterRoll(BehaviorBase __instance, out SavedDice __state)
        {
            __state = new SavedDice();
            int s;
            if (!Connected || !NextSeed(ActorOf(__instance), true, out s))
                return;
            __state.On = true;
            __state.Saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(s);
            if (spreadCounter != null)
            {
                __state.Spread = (int)spreadCounter.GetValue(null);
                spreadCounter.SetValue(null, s & 0xFFFF); // never negative: it picks from a table
            }
        }

        private static Exception AfterLaterRoll(Exception __exception, SavedDice __state)
        {
            if (__state.On)
            {
                UnityEngine.Random.state = __state.Saved;
                if (spreadCounter != null)
                    spreadCounter.SetValue(null, __state.Spread);
            }
            return __exception;
        }

        // Forget enemies that are gone.
        public static void Update()
        {
            if (diceOf.Count > 200)
            {
                List<AIActor> goneDice = new List<AIActor>();
                foreach (AIActor a in diceOf.Keys)
                {
                    if (a == null)
                        goneDice.Add(a);
                }
                foreach (AIActor a in goneDice)
                    diceOf.Remove(a);
            }
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
