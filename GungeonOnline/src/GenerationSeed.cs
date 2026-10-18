using System;
using System.Collections.Generic;
using System.Reflection;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Same room decorations in every game.
    //
    // Floors match (same seed), but the decorations didn't: wall paintings, mounted heads, vases, barrels and other
    // props differed between games, so a bush or candle one player broke had no match in the others. The game picks
    // them with Unity's shared random numbers:
    // - while it builds the floor (Dungeon.Regenerate). It seeds them at the start, but building runs over many frames
    //   and everything else in the game (effects, menus, sounds...) draws from the same numbers in between, differently
    //   in each game;
    // - when a room's objects are placed (RoomHandler.PlaceObjectsFromLayer), which happens when rooms come alive -
    //   at different moments in each game.
    // - when a table or bench puts its bottles, books and plates on top (SurfaceDecorator.Decorate);
    // - when a decoration placeholder (DungeonPlaceableContainer) wakes up and picks what it becomes (book colour,
    //   which painting...), which also happens at different moments in each game.
    // Online, floor building (Dungeon.Regenerate and the DungeonData.Apply step it runs) keeps its own random state
    // from frame to frame; each placement is seeded from the run seed, the room and how many times that room placed
    // objects; each placeholder from the run seed and its position. Everything else keeps the game's own numbers.
    public static class GenerationSeed
    {
        private static NetSession session;
        private struct Saved
        {
            public UnityEngine.Random.State State;
            public bool Outer;
        }

        private static bool building;
        private static UnityEngine.Random.State buildState;
        private static int stepDepth;   // building steps can run inside each other: only the outermost one swaps
        private static readonly Dictionary<long, int> containerCalls = new Dictionary<long, int>();
        private static Dungeon containerFloor;
        private static readonly Dictionary<RoomHandler, int> placements = new Dictionary<RoomHandler, int>();
        private static Dungeon placementsFloor;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            Type iterator = AccessTools.Inner(typeof(Dungeon), "<Regenerate>c__Iterator1");
            MethodInfo moveNext = iterator != null ? AccessTools.Method(iterator, "MoveNext") : null;
            if (moveNext != null)
                harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(GenerationSeed), "BeforeBuildStep"),
                    finalizer: new HarmonyMethod(typeof(GenerationSeed), "AfterBuildStep"));
            else
                Plugin.Log.LogWarning("GenerationSeed: floor building not found; decorations may differ between games.");
            Type apply = AccessTools.Inner(typeof(DungeonData), "<Apply>c__Iterator0");
            MethodInfo applyNext = apply != null ? AccessTools.Method(apply, "MoveNext") : null;
            if (applyNext != null)
                harmony.Patch(applyNext, prefix: new HarmonyMethod(typeof(GenerationSeed), "BeforeApplyStep"),
                    finalizer: new HarmonyMethod(typeof(GenerationSeed), "AfterApplyStep"));
            MethodInfo decorate = AccessTools.Method(typeof(SurfaceDecorator), "Decorate");
            if (decorate != null)
                harmony.Patch(decorate, prefix: new HarmonyMethod(typeof(GenerationSeed), "BeforeSurface"),
                    finalizer: new HarmonyMethod(typeof(GenerationSeed), "AfterContainer"));
            MethodInfo containerAwake = AccessTools.Method(typeof(DungeonPlaceableContainer), "Awake");
            if (containerAwake != null)
                harmony.Patch(containerAwake, prefix: new HarmonyMethod(typeof(GenerationSeed), "BeforeContainer"),
                    finalizer: new HarmonyMethod(typeof(GenerationSeed), "AfterContainer"));
            MethodInfo place = AccessTools.Method(typeof(RoomHandler), "PlaceObjectsFromLayer");
            if (place != null)
                harmony.Patch(place, prefix: new HarmonyMethod(typeof(GenerationSeed), "BeforePlace"),
                    finalizer: new HarmonyMethod(typeof(GenerationSeed), "AfterPlace"));
        }

        private static bool Online
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        // ---- Floor building: its own random numbers, carried from one frame to the next ----

        private static void BeforeBuildStep(out Saved __state)
        {
            __state = new Saved();
            __state.State = UnityEngine.Random.state;
            __state.Outer = stepDepth == 0;
            stepDepth++;
            if (__state.Outer && Online && building)
                UnityEngine.Random.state = buildState;
        }

        private static Exception AfterBuildStep(Saved __state, bool __result, Exception __exception)
        {
            stepDepth = Math.Max(0, stepDepth - 1);
            if (__state.Outer && Online)
            {
                building = __exception == null && __result; // more steps to come, or finished
                buildState = UnityEngine.Random.state;
                UnityEngine.Random.state = __state.State;
            }
            return __exception;
        }

        // The tile/decoration step floor building runs (its own frames, or inside a building step): same stream.
        private static void BeforeApplyStep(out Saved __state)
        {
            __state = new Saved();
            __state.State = UnityEngine.Random.state;
            __state.Outer = stepDepth == 0;
            stepDepth++;
            if (__state.Outer && Online && building)
                UnityEngine.Random.state = buildState;
        }

        private static Exception AfterApplyStep(Saved __state, Exception __exception)
        {
            stepDepth = Math.Max(0, stepDepth - 1);
            if (__state.Outer && Online)
            {
                if (building)
                    buildState = UnityEngine.Random.state;
                UnityEngine.Random.state = __state.State;
            }
            return __exception;
        }

        // ---- A decoration placeholder picking what it becomes: seeded by run and position ----

        // A table or bench decorating its top: seeded by run and position like the placeholders.
        private static void BeforeSurface(SurfaceDecorator __instance, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (Online && GameManager.HasInstance && __instance != null)
                SeedAt(__instance.transform.position, 9973);
        }

        private static void BeforeContainer(DungeonPlaceableContainer __instance, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (Online && GameManager.HasInstance && __instance != null)
                SeedAt(__instance.transform.position, 6007);
        }

        // Seed from run, position, a salt per kind, and how many times this spot asked (retries differ).
        private static void SeedAt(Vector3 p, int salt)
        {
            Dungeon d = GameManager.Instance.Dungeon;
            if (d != containerFloor)
            {
                containerFloor = d;
                containerCalls.Clear();
            }
            unchecked
            {
                int x = Mathf.RoundToInt(p.x * 4f), y = Mathf.RoundToInt(p.y * 4f);
                // Code that retries a placement until it likes the result calls this again at the same spot: each
                // call there gets the next number, so retries differ (the same in every game, in the same order).
                long key = (((long)x << 32) ^ (uint)y) ^ ((long)salt << 48);
                if (containerCalls.Count > 20000)
                    containerCalls.Clear();
                int calls;
                containerCalls.TryGetValue(key, out calls);
                containerCalls[key] = calls + 1;
                UnityEngine.Random.InitState(GameManager.Instance.CurrentRunSeed * salt + x * 73856093 + y * 19349663 + calls * 83492791);
            }
        }

        private static Exception AfterContainer(UnityEngine.Random.State __state, Exception __exception)
        {
            if (Online)
                UnityEngine.Random.state = __state;
            return __exception;
        }

        // ---- A room placing its objects: seeded by run, room and how many times the room did it ----

        private static void BeforePlace(RoomHandler __instance, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (!Online || !GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return;
            Dungeon d = GameManager.Instance.Dungeon;
            if (d != placementsFloor)
            {
                placementsFloor = d;
                placements.Clear();
            }
            int room = d.data != null ? d.data.rooms.IndexOf(__instance) : -1;
            if (room < 0)
                return;
            int count;
            placements.TryGetValue(__instance, out count);
            placements[__instance] = count + 1;
            unchecked
            {
                UnityEngine.Random.InitState(GameManager.Instance.CurrentRunSeed * 7919 + room * 104729 + count * 31 + 17);
            }
        }

        private static Exception AfterPlace(UnityEngine.Random.State __state, Exception __exception)
        {
            if (Online)
                UnityEngine.Random.state = __state;
            return __exception;
        }
    }
}
