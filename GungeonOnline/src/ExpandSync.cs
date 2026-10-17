using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Expand the Gungeon compatibility: its jungle tree (in the Keep) adds an extra elevator room after the floor is
    // built, picking one of two versions with Unity's unseeded random, so host and joiner got different rooms.
    // Online, that pick is seeded from the run seed and the tree's position, so both games pick the same one.
    // Expand loads after us, so the patch is set up on the first frames instead of at start.
    public static class ExpandSync
    {
        private static NetSession session;
        private static Harmony harmony;
        private static bool setUp;
        private static int tries;

        public static void Init(NetSession netSession, Harmony h)
        {
            session = netSession;
            harmony = h;
        }

        public static void Update()
        {
            if (setUp || tries > 600)
                return;
            tries++;
            if (tries % 60 != 1)
                return;
            Type tree = AccessTools.TypeByName("ExpandTheGungeon.ExpandComponents.ExpandJungleTreeController");
            if (tree == null)
                return;
            setUp = true;
            foreach (Type nested in tree.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!nested.Name.StartsWith("<Start>"))
                    continue;
                MethodInfo moveNext = AccessTools.Method(nested, "MoveNext");
                if (moveNext == null)
                    continue;
                try
                {
                    harmony.Patch(moveNext, new HarmonyMethod(typeof(ExpandSync), "BeforeStep"), new HarmonyMethod(typeof(ExpandSync), "AfterStep"));
                    Plugin.Log.LogInfo("Expand the Gungeon found: its jungle elevator room now matches between players.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't patch Expand the Gungeon's jungle tree: " + e.Message);
                }
            }
        }

        private static bool Online
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static void BeforeStep(object __instance, out object __state)
        {
            __state = null;
            if (!Online || !GameManager.HasInstance || GameManager.Instance.CurrentRunSeed == 0)
                return;
            int seed = GameManager.Instance.CurrentRunSeed;
            Component owner = Traverse.Create(__instance).Field("<>4__this").GetValue() as Component;
            if (owner != null)
            {
                Vector3 p = owner.transform.position;
                seed = seed * 31 + Mathf.RoundToInt(p.x) * 7919 + Mathf.RoundToInt(p.y);
            }
            __state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
        }

        private static void AfterStep(object __state)
        {
            if (__state != null)
                UnityEngine.Random.state = (UnityEngine.Random.State)__state;
        }
    }
}
