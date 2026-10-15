using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Compatibility with "Loop The Gungeon" (Milz): endless runs where each loop makes enemies stronger.
    // - The loop number lives in LoopTheGungeon.Plugin.GlobalLoopOffset. The host sends it (and its difficulty
    //   settings) to the joiner, so enemies have the same health/speed and shops the same prices in both games.
    // - When the host loops, the joiner runs the mod's own loop start too (LoopManager.TriggerLoop: loop count,
    //   casing reset, mastery shrine reset...) but skips its floor load: the joiner already follows the host's
    //   floor load through LevelSync, and two loads at once hang the game.
    // - The joiner's difficulty settings (Gunfig, GunfigManager.Get*PerLoop) answer with the host's values while
    //   connected, and so does every Loop The Gungeon option in its Gunfig menus (DPS cap, Lord of the Jammed, tax...):
//   Gunfig.Value(key) answers with the host's value. The joiner's own settings file is never changed.
// - Extra enemies per loop: the mod picks their types with Unity's random numbers, which differ between the two
//   games. While it picks, both games use the same seed (run seed + room + wave + pick), so the same enemies are
//   added in the same order; their positions are then pulled together by EnemySync like any other enemy.
// - The joiner's loop count is kept at the host's (Loop's own resume can restore a different one).
    // Everything is found by reflection, so none of this runs (or is needed) without the mod.
    public static class LoopSync
    {
        private static readonly string[] SettingGetters =
        {
            "GetHpPerLoop", "GetAtkSpeedPerLoop", "GetMoveSpeedPerLoop", "GetProjSpeedPerLoop",
            "GetShopPricingPerLoop", "GetSpecialPricingPerLoop", "GetExtraEnemiesPerLoop",
        };

        private static NetSession session;
        private static FieldInfo loopField;          // Plugin.GlobalLoopOffset
        private static MethodInfo triggerLoop;       // LoopManager.TriggerLoop(float)
        private static readonly Dictionary<string, MethodInfo> getters = new Dictionary<string, MethodInfo>();

        // Joiner: the host's settings, by getter name (boxed float / int).
        private static readonly Dictionary<string, object> hostSettings = new Dictionary<string, object>();
        private static bool suppressLoopLoad;
        private static bool loggedSettings;
        private static int knownHostLoops = -1;

        // Gunfig (Loop The Gungeon's option menus).
        private static object loopGunfig;               // GunfigManager.gunfig
        private static FieldInfo gunfigField, optionsField, parentField, nameField, activeField;
        private static readonly Dictionary<string, string> hostOptions = new Dictionary<string, string>();
        private static readonly Dictionary<object, string> menuNameOf = new Dictionary<object, string>(); // null = not Loop's

        // Extra-enemy seeding.
        private static readonly Dictionary<Dungeonator.RoomHandler, int> spawnCalls = new Dictionary<Dungeonator.RoomHandler, int>();
        private static Dungeonator.Dungeon spawnFloor;
        private static int spawnSeedBase, spawnPick;
        private static bool seeding;

        // Host: what we sent last.
        private static int lastSentLoop = -1;
        private static string lastSentSettings = "";
        private static float nextCheckAt;

        public static bool Present
        {
            get { return loopField != null; }
        }

        private static Harmony harmonyInstance;
        private static bool setUp;

        // Loop The Gungeon may load after us, so the actual set-up waits for the first frame (all mods loaded).
        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmonyInstance = harmony;
        }

        private static void SetUp()
        {
            setUp = true;
            Harmony harmony = harmonyInstance;
            bool found = false;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetType("LoopTheGungeon.Plugin", false) != null)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return;
            Type plugin = AccessTools.TypeByName("LoopTheGungeon.Plugin");
            Type manager = AccessTools.TypeByName("LoopTheGungeon.Core.LoopManager");
            Type gunfig = AccessTools.TypeByName("LoopTheGungeon.GunfigManager");
            if (plugin == null || manager == null)
                return;
            loopField = AccessTools.Field(plugin, "GlobalLoopOffset");
            triggerLoop = AccessTools.Method(manager, "TriggerLoop");
            MethodInfo performLoad = AccessTools.Method(manager, "PerformLoopLoad");
            if (loopField == null || triggerLoop == null || performLoad == null)
            {
                Plugin.Log.LogWarning("Loop The Gungeon found, but not the parts we sync (different version?). Loops won't be synced.");
                loopField = null;
                return;
            }
            try
            {
                harmony.Patch(performLoad, new HarmonyMethod(typeof(LoopSync), "BeforePerformLoopLoad"));
                if (gunfig != null)
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(LoopSync), "HostSetting");
                    foreach (string name in SettingGetters)
                    {
                        MethodInfo m = AccessTools.Method(gunfig, name);
                        if (m == null)
                            continue;
                        getters[name] = m;
                        harmony.Patch(m, prefix);
                    }
                    gunfigField = AccessTools.Field(gunfig, "gunfig");
                }
                Type gunfigType = AccessTools.TypeByName("Gunfiguration.Gunfig");
                if (gunfigType != null && gunfigField != null)
                {
                    optionsField = AccessTools.Field(gunfigType, "_options");
                    parentField = AccessTools.Field(gunfigType, "_parentGunfig");
                    nameField = AccessTools.Field(gunfigType, "_cleanModName");
                    activeField = AccessTools.Field(gunfigType, "_ActiveConfigs");
                    MethodInfo value = AccessTools.Method(gunfigType, "Value", new Type[] { typeof(string) });
                    if (optionsField != null && parentField != null && nameField != null && activeField != null && value != null)
                        harmony.Patch(value, new HarmonyMethod(typeof(LoopSync), "HostOption"));
                    else
                        optionsField = null;
                }
                Type scaler = AccessTools.TypeByName("LoopTheGungeon.Util.SpawnCountScaler");
                MethodInfo spawn = scaler != null ? AccessTools.Method(scaler, "SpawnEnemiesWithVariety") : null;
                MethodInfo pick = scaler != null ? AccessTools.Method(scaler, "PickWeightedCandidate") : null;
                if (spawn != null && pick != null)
                {
                    harmony.Patch(spawn, new HarmonyMethod(typeof(LoopSync), "BeforeExtraSpawn"), new HarmonyMethod(typeof(LoopSync), "AfterExtraSpawn"));
                    harmony.Patch(pick, new HarmonyMethod(typeof(LoopSync), "BeforePick"), new HarmonyMethod(typeof(LoopSync), "AfterPick"));
                }
                Plugin.Log.LogInfo("Loop The Gungeon found: loops, " + getters.Count + " difficulty settings"
                    + (optionsField != null ? ", its options" : "") + (spawn != null && pick != null ? " and extra enemies" : "") + " follow the host.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Loop The Gungeon support failed to start: " + e.Message);
                loopField = null;
            }
        }

        private static bool ConnectedJoiner
        {
            get { return session != null && session.Link != null && !session.Link.IsHost && session.Status == NetSession.State.Connected; }
        }

        private static int Loops
        {
            get { return (int)loopField.GetValue(null); }
            set { loopField.SetValue(null, value); }
        }

        // The joiner's floor load comes from the host (LevelSync); the mod's own one is skipped then.
        private static bool BeforePerformLoopLoad()
        {
            return !suppressLoopLoad;
        }

        // Joiner: difficulty settings answer with the host's.
        private static bool HostSetting(MethodBase __originalMethod, ref object __result)
        {
            object value;
            if (!ConnectedJoiner || !hostSettings.TryGetValue(__originalMethod.Name, out value))
                return true;
            __result = value;
            return false;
        }

        // Joiner: Loop The Gungeon's options answer with the host's values.
        private static bool HostOption(object __instance, string __0, ref string __result)
        {
            string key = __0;
            if (!ConnectedJoiner || hostOptions.Count == 0 || key == null)
                return true;
            string menu = MenuName(__instance);
            string value;
            if (menu == null || !hostOptions.TryGetValue(menu + "\u0001" + key, out value))
                return true;
            __result = value;
            return false;
        }

        // The menu name of a Gunfig that belongs to Loop The Gungeon (its main menu or a submenu), else null.
        private static string MenuName(object gunfig)
        {
            if (gunfig == null)
                return null;
            string name;
            if (menuNameOf.TryGetValue(gunfig, out name))
                return name;
            if (loopGunfig == null && gunfigField != null)
                loopGunfig = gunfigField.GetValue(null);
            name = null;
            object g = gunfig;
            for (int depth = 0; g != null && depth < 8; depth++)
            {
                if (ReferenceEquals(g, loopGunfig))
                {
                    name = (string)nameField.GetValue(gunfig);
                    break;
                }
                g = parentField.GetValue(g);
            }
            if (loopGunfig != null)
                menuNameOf[gunfig] = name; // only cache once Loop's menu exists
            return name;
        }

        // Host: every option of Loop The Gungeon's menus, as "menu<1>key" -> value.
        private static Dictionary<string, string> CollectOptions()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (optionsField == null)
                return result;
            System.Collections.IList all = activeField.GetValue(null) as System.Collections.IList;
            if (all == null)
                return result;
            foreach (object g in all)
            {
                string menu = MenuName(g);
                if (menu == null)
                    continue;
                Dictionary<string, string> options = optionsField.GetValue(g) as Dictionary<string, string>;
                if (options == null)
                    continue;
                foreach (KeyValuePair<string, string> o in options)
                    result[menu + "\u0001" + o.Key] = o.Value ?? "";
            }
            return result;
        }

        // ---- Extra enemies: same picks in both games ----

        private static void BeforeExtraSpawn(Dungeonator.RoomHandler __0)
        {
            seeding = false;
            if (session == null || session.Status != NetSession.State.Connected || __0 == null || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (gm.Dungeon == null)
                return;
            if (spawnFloor != gm.Dungeon)
            {
                spawnFloor = gm.Dungeon;
                spawnCalls.Clear();
            }
            int wave;
            spawnCalls.TryGetValue(__0, out wave);
            spawnCalls[__0] = wave + 1;
            int room = gm.Dungeon.data.rooms.IndexOf(__0);
            unchecked
            {
                spawnSeedBase = gm.CurrentRunSeed * 31 + room * 7919 + wave * 104729 + (Present ? Loops : 0) * 1299709;
            }
            spawnPick = 0;
            seeding = true;
        }

        private static void AfterExtraSpawn()
        {
            seeding = false;
        }

        private static void BeforePick(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (!seeding)
                return;
            unchecked
            {
                UnityEngine.Random.InitState(spawnSeedBase + spawnPick * 15485863);
            }
            spawnPick++;
        }

        private static void AfterPick(UnityEngine.Random.State __state)
        {
            // Give the game its own random sequence back, as if nothing happened.
            UnityEngine.Random.state = __state;
        }

        // ---- Host ----

        public static void Update()
        {
            if (!setUp)
                SetUp();
            if (ConnectedJoiner)
            {
                // Loop's own resume (Continue) can restore its saved loop count: keep the host's.
                if (Present && knownHostLoops >= 0 && Loops != knownHostLoops && GameManager.HasInstance && !GameManager.Instance.IsLoadingLevel)
                {
                    Plugin.Log.LogInfo("Loop count " + Loops + " differs from the host's " + knownHostLoops + "; using the host's.");
                    Loops = knownHostLoops;
                }
                return;
            }
            if (session == null || session.Link == null)
            {
                knownHostLoops = -1;
                hostOptions.Clear();
            }
            if (!Present || session == null || session.Link == null || !session.Link.IsHost || session.Status != NetSession.State.Connected)
            {
                lastSentLoop = -1;
                return;
            }
            if (Time.realtimeSinceStartup < nextCheckAt && Loops == lastSentLoop)
                return;
            nextCheckAt = Time.realtimeSinceStartup + 1f;
            SendState(false);
        }

        // Called on connect, before every level load, and when the loop or settings change.
        public static void SendState(bool force)
        {
            if (!Present || session == null || session.Link == null || !session.Link.IsHost)
                return;
            int loops = Loops;
            List<string> names = new List<string>();
            List<float> values = new List<float>();
            string key = "";
            foreach (KeyValuePair<string, MethodInfo> pair in getters)
            {
                float v;
                try
                {
                    v = Convert.ToSingle(pair.Value.Invoke(null, null));
                }
                catch
                {
                    continue;
                }
                names.Add(pair.Key);
                values.Add(v);
                key += pair.Key + "=" + v + ";";
            }
            Dictionary<string, string> options = CollectOptions();
            foreach (KeyValuePair<string, string> o in options)
                key += o.Key + "=" + o.Value + ";";
            if (!force && loops == lastSentLoop && key == lastSentSettings)
                return;
            lastSentLoop = loops;
            lastSentSettings = key;
            session.SendReliable(MsgType.LoopState, delegate (BinaryWriter w)
            {
                w.Write(loops);
                w.Write(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    w.Write(names[i]);
                    w.Write(values[i]);
                }
                w.Write(options.Count);
                foreach (KeyValuePair<string, string> o in options)
                {
                    w.Write(o.Key);
                    w.Write(o.Value);
                }
            });
        }

        // ---- Joiner ----

        public static void OnLoopState(BinaryReader r)
        {
            if (!setUp)
                SetUp();
            int hostLoops = r.ReadInt32();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                float value = r.ReadSingle();
                MethodInfo m;
                if (getters.TryGetValue(name, out m))
                    hostSettings[name] = m.ReturnType == typeof(int) ? (object)Mathf.RoundToInt(value) : value;
            }
            int optionCount = r.ReadInt32();
            hostOptions.Clear();
            for (int i = 0; i < optionCount; i++)
            {
                string optionKey = r.ReadString();
                hostOptions[optionKey] = r.ReadString();
            }
            knownHostLoops = hostLoops;
            if (count > 0 && !loggedSettings)
            {
                loggedSettings = true;
                // Ask our own settings now: they should answer with the host's values.
                int same = 0;
                foreach (KeyValuePair<string, object> pair in hostSettings)
                {
                    try
                    {
                        if (Convert.ToSingle(getters[pair.Key].Invoke(null, null)) == Convert.ToSingle(pair.Value))
                            same++;
                    }
                    catch
                    {
                    }
                }
                // And prove the answer really comes from the host's list (both could be equal by chance):
                // plant a test value for a moment.
                bool redirected = false;
                if (getters.ContainsKey("GetHpPerLoop") && hostSettings.ContainsKey("GetHpPerLoop"))
                {
                    object saved = hostSettings["GetHpPerLoop"];
                    hostSettings["GetHpPerLoop"] = 123.25f;
                    try
                    {
                        redirected = Convert.ToSingle(getters["GetHpPerLoop"].Invoke(null, null)) == 123.25f;
                    }
                    catch
                    {
                    }
                    hostSettings["GetHpPerLoop"] = saved;
                }
                // Same proof for the options: plant a test value for one of the main menu's options and read it.
                bool optionsRedirected = false;
                object main = gunfigField != null ? gunfigField.GetValue(null) : null;
                string mainName = main != null ? MenuName(main) : null;
                if (mainName != null)
                {
                    foreach (string k in new List<string>(hostOptions.Keys))
                    {
                        if (!k.StartsWith(mainName + "\u0001"))
                            continue;
                        string saved = hostOptions[k];
                        hostOptions[k] = "__gungeon_online_test__";
                        try
                        {
                            MethodInfo valueMethod = main.GetType().GetMethod("Value", new Type[] { typeof(string) });
                            optionsRedirected = (string)valueMethod.Invoke(main, new object[] { k.Substring(mainName.Length + 1) }) == "__gungeon_online_test__";
                        }
                        catch
                        {
                        }
                        hostOptions[k] = saved;
                        break;
                    }
                }
                Plugin.Log.LogInfo("Options redirect check " + (optionsRedirected ? "OK" : "FAILED") + ".");
                Plugin.Log.LogInfo("Using the host's Loop The Gungeon settings (" + hostOptions.Count + " options): " + same + "/" + hostSettings.Count
                    + " answer with the host's values (redirect check " + (redirected ? "OK" : "FAILED") + "). Host is on loop " + hostLoops + ".");
            }
            if (!Present)
            {
                if (hostLoops > 0)
                    Plugin.Log.LogWarning("The host is on loop " + hostLoops + " of Loop The Gungeon, which isn't installed here.");
                return;
            }
            int ours = Loops;
            if (hostLoops == ours)
                return;
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            bool inRun = gm != null && gm.PrimaryPlayer != null && !gm.IsFoyer && gm.Dungeon != null;
            if (hostLoops == ours + 1 && inRun)
            {
                // The host just looped: do the same loop start here (minus the floor load, see above).
                Plugin.Log.LogInfo("Host started loop " + hostLoops + ": looping too.");
                suppressLoopLoad = true;
                try
                {
                    triggerLoop.Invoke(null, new object[] { 0f });
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Loop start failed: " + e);
                }
                finally
                {
                    suppressLoopLoad = false;
                }
            }
            Loops = hostLoops;
            Plugin.Log.LogInfo("Loop count set to the host's: " + hostLoops);
        }
    }
}
