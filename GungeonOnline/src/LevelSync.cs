using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Keeps both players on the same floor: when the host loads a level, the joiner loads it too,
    // with the same seed so the dungeon is generated the same way.
    //
    // The game builds every floor from GameManager.CurrentRunSeed when it is non-zero. When it is 0
    // (a normal solo run) each floor rolls a random seed that is never stored, so the host picks a
    // seed itself when a run starts (Breach elevator or Quick Restart). It is reset to 0 when the session ends.
    //
    // After a game over, each player chooses on their own death screen:
    // - Host went back to the Breach: a joiner pressing Quick Restart goes to the Breach instead.
    // - Host quick-restarted: a joiner pressing Quick Restart, or taking the Breach elevator, starts on the
    //   host's run (same seed, same floor) instead of their own.
    // The joiner's "start a run" always adopts the host's floor while the host is in a run, which also
    // covers joining late or after restarting the game.
    public static class LevelSync
    {
        private const float LoadDelay = 0.5f;
        private static readonly FieldInfo nextLevelIndexField = AccessTools.Field(typeof(GameManager), "nextLevelIndex");

        private static NetSession session;
        private static bool insideLoadNextLevel;
        // The Breach elevator calls Foyer.OnDepartedFoyer() (which sets IsFoyer = false) BEFORE the delayed
        // LoadNextLevel runs, so "am I in the Breach?" is already false by then. Remember the departure instead.
        private static bool departedFoyer;
        private static bool insideFoyerReturn;
        private static bool insideQuickRestart;
        private static bool pendingFoyerReturn;   // joiner: host went back to the Breach, follow when possible
        private static bool pendingEndRun;        // joiner: host ended the run, end ours when possible
        // "Continue" (a saved run) was pressed: its floor load starts a run, so a joiner goes to the host's floor
        // instead (and still gets their saved character and items back).
        private static bool continuingSavedRun;

        private class LevelInfo
        {
            public int Seed;
            public int Index;
            public string Custom;
        }
        // Host: the floor we loaded last. Joiner: the host's current floor (null while the host isn't in a run).
        private static LevelInfo hostLevel;

        // Joiner: the host started a new run while we were on our death screen (our Quick Restart joins it).
        public static bool WaitingToJoin;
        // Joiner: we pressed Quick Restart before the host did; we restart when the host does.
        public static bool WaitingForHost;
        private static QuickRestartOptions pendingRestartOptions;
        private static bool restartingForHost;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(LevelSync));
        }

        // Joiner: the host is in a run right now (we know its floor).
        public static bool HostInRun
        {
            get { return hostLevel != null; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "DelayedLoadMidgameSave")]
        private static void BeforeLoadSavedRun()
        {
            continuingSavedRun = true;
        }

        // A short id for "which level am I on", so the puppet only shows when both are on the same floor.
        // Every dungeon floor runs in the same Unity scene, so the dungeon object's name is added.
        public static string CurrentLevelKey()
        {
            string key = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (GameManager.HasInstance && GameManager.Instance.Dungeon != null)
                key += "|" + GameManager.Instance.Dungeon.gameObject.name;
            return key;
        }

        public static void ResetSeed()
        {
            if (GameManager.HasInstance)
                GameManager.Instance.InitializeForRunWithSeed(0);
        }

        private static bool IsConnected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static bool IsConnectedHost()
        {
            return IsConnected && session.Link.IsHost;
        }

        private static bool IsConnectedJoiner()
        {
            return IsConnected && !session.Link.IsHost;
        }

        // The partner reports a player standing in the Breach.
        private static bool PartnerInBreach
        {
            get { return session.Remote.HasPlayer && session.Remote.Scene.StartsWith("tt_foyer"); }
        }

        // We died for good and are looking at the death screen (not a ghost).
        private static bool OnDeathScreen
        {
            get
            {
                PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                return me != null && me.healthHaver != null && me.healthHaver.IsDead && !me.IsGhost;
            }
        }

        // Host: tell a newly connected joiner which floor we're on, so their next run start joins us.
        public static void OnConnected()
        {
            if (IsConnectedHost() && hostLevel != null && GameManager.HasInstance && !GameManager.Instance.IsFoyer)
                SendLevelLoad(hostLevel.Seed, hostLevel.Index, hostLevel.Custom, true, false);
        }

        // ---- Loading levels ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "LoadNextLevel")]
        private static void BeforeLoadNextLevel(GameManager __instance)
        {
            insideLoadNextLevel = true;
            bool startingRun = __instance.IsFoyer || departedFoyer || insideQuickRestart || continuingSavedRun;
            departedFoyer = false;
            bool savedRun = continuingSavedRun;
            continuingSavedRun = false;
            // ReturnToFoyer and LoadCharacterSelect go through LoadNextLevel too. That's not a floor to
            // follow; a second "load level N" made the joiner start two loads at once and hang forever.
            if (insideFoyerReturn)
                return;

            if (IsConnectedJoiner())
            {
                // Build this floor from the host's save progress so it comes out the same as theirs.
                SaveSync.BeginBorrow();
                // Starting a run while the host is in one: start on the host's run instead of our own.
                // (No "host has a player" check: during the host's own loading they briefly have none.)
                if (startingRun && hostLevel != null && !PartnerInBreach)
                {
                    Plugin.Log.LogInfo((savedRun ? "Continuing our saved run" : "Starting a run") + " while the host is in one: joining the host's floor (seed " + hostLevel.Seed + ").");
                    __instance.InitializeForRunWithSeed(hostLevel.Seed);
                    if (hostLevel.Custom.Length > 0)
                        __instance.InjectedLevelName = hostLevel.Custom;
                    else
                        nextLevelIndexField.SetValue(__instance, hostLevel.Index);
                }
                return;
            }
            if (!IsConnectedHost())
                return;

            // A fresh seed for every new run (Quick Restart would otherwise replay the same dungeon).
            if (startingRun || __instance.CurrentRunSeed == 0)
                __instance.InitializeForRunWithSeed(Random.Range(1, 1000000000));

            int index = (int)nextLevelIndexField.GetValue(__instance);
            string injected = __instance.InjectedLevelName ?? "";
            SendLevelLoad(__instance.CurrentRunSeed, index, injected, startingRun, true);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "LoadNextLevel")]
        private static void AfterLoadNextLevel()
        {
            insideLoadNextLevel = false;
            DeathSync.ResetGameOver();
            WaitingToJoin = false;
        }

        // Special floors (secret floors, shortcuts, the tutorial...) load by name.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "LoadCustomLevel")]
        private static void BeforeLoadCustomLevel(GameManager __instance, string custom)
        {
            // LoadNextLevel calls this itself for injected levels; that was already reported.
            if (insideLoadNextLevel)
                return;
            if (IsConnectedJoiner() && !insideFoyerReturn)
                SaveSync.BeginBorrow();
            bool startingRun = __instance.IsFoyer || departedFoyer || continuingSavedRun;
            departedFoyer = false;
            continuingSavedRun = false;
            if (!IsConnectedHost())
                return;
            if (startingRun || __instance.CurrentRunSeed == 0)
                __instance.InitializeForRunWithSeed(Random.Range(1, 1000000000));
            SendLevelLoad(__instance.CurrentRunSeed, -1, custom, startingRun, true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Foyer), "OnDepartedFoyer")]
        private static void BeforeDepartedFoyer()
        {
            departedFoyer = true;
        }

        // Quick Restart (death screen / pause menu) starts a new run directly.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "QuickRestart")]
        private static bool BeforeQuickRestart(GameManager __instance, QuickRestartOptions options)
        {
            if (IsConnectedJoiner() && !restartingForHost)
            {
                // Host went back to the Breach: go to the Breach too instead of a solo run.
                if (PartnerInBreach)
                {
                    Plugin.Log.LogInfo("Quick Restart while the host is in the Breach: going to the Breach instead.");
                    __instance.LoadCharacterSelect(true, false);
                    return false;
                }
                // The host hasn't started the new run yet: hold our restart until they do.
                if (!WaitingToJoin)
                {
                    Plugin.Log.LogInfo("Quick Restart: waiting for the host to restart.");
                    WaitingForHost = true;
                    pendingRestartOptions = options;
                    return false;
                }
            }
            WaitingForHost = false;
            WaitingToJoin = false;
            insideQuickRestart = true;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "QuickRestart")]
        private static void AfterQuickRestart()
        {
            insideQuickRestart = false;
            restartingForHost = false;
        }

        // Joiner: start a fresh run on the host's new run. A real Quick Restart (the same one the death screen
        // uses) gives a clean player; BeforeLoadNextLevel then puts it on the host's seed and floor. (Following
        // with a plain level load kept a ghost a ghost and got stuck loading forever.)
        private static void RestartOntoHostRun()
        {
            GameManager gm = GameManager.Instance;
            WaitingForHost = false;
            WaitingToJoin = false;
            pendingFoyerReturn = false;
            pendingEndRun = false;
            restartingForHost = true;
            Plugin.Log.LogInfo("Restarting onto the host's new run.");
            if (Pixelator.Instance != null)
                Pixelator.Instance.FadeToBlack(LoadDelay, false, 0f);
            gm.DelayedQuickRestart(LoadDelay, pendingRestartOptions);
        }

        // Elevators / tutorial back to the Breach: bring the partner too.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "ReturnToFoyer")]
        private static void BeforeReturnToFoyer()
        {
            insideFoyerReturn = true;
            if (IsConnectedHost())
            {
                hostLevel = null;
                session.SendReliable(MsgType.ReturnToFoyer, null);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "ReturnToFoyer")]
        private static void AfterReturnToFoyer()
        {
            insideFoyerReturn = false;
        }

        // Ending a run (pause menu "Quit", or the death screen) goes through LoadCharacterSelect: it unloads
        // the run and goes back to the Breach. End the partner's run too.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "LoadCharacterSelect")]
        private static void BeforeLoadCharacterSelect(GameManager __instance)
        {
            insideFoyerReturn = true;
            if (IsConnectedHost())
            {
                hostLevel = null;
                if (!__instance.IsFoyer)
                {
                    Plugin.Log.LogInfo("Host ended the run; ending the partner's run too.");
                    session.SendReliable(MsgType.EndRun, null);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "LoadCharacterSelect")]
        private static void AfterLoadCharacterSelect()
        {
            insideFoyerReturn = false;
        }

        public static void Update()
        {
            FollowToFoyerWhenReady();
        }

        // follow = false: just let the joiner know where we are (their next run start joins us).
        private static void SendLevelLoad(int seed, int levelIndex, string customLevel, bool startingRun, bool follow)
        {
            // A new run: send our latest save progress first (reliable messages arrive in order), so the
            // joiner builds the floors from the same progress as we do.
            if (startingRun && follow)
                SaveSync.SendProgress(true);
            // Loop The Gungeon: the loop number must arrive before the floor it belongs to.
            LoopSync.SendState(false);
            hostLevel = new LevelInfo();
            hostLevel.Seed = seed;
            hostLevel.Index = levelIndex;
            hostLevel.Custom = customLevel;
            Plugin.Log.LogInfo("Host " + (follow ? "loading" : "is on") + " level (index " + levelIndex + ", custom '" + customLevel + "', seed " + seed + ")");
            session.SendReliable(MsgType.LevelLoad, delegate (BinaryWriter w)
            {
                w.Write(seed);
                w.Write(levelIndex);
                w.Write(customLevel);
                w.Write(startingRun);
                w.Write(follow);
            });
        }

        // ---- Joiner side: follow the host ----

        public static void OnLevelLoad(BinaryReader r)
        {
            int seed = r.ReadInt32();
            int levelIndex = r.ReadInt32();
            string customLevel = r.ReadString();
            bool startingRun = r.ReadBoolean();
            bool follow = r.ReadBoolean();

            // Always remember where the host is, even when we don't follow right now.
            hostLevel = new LevelInfo();
            hostLevel.Seed = seed;
            hostLevel.Index = levelIndex;
            hostLevel.Custom = customLevel;

            if (!follow || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (gm.PrimaryPlayer == null)
            {
                Plugin.Log.LogWarning("Host started a level, but you haven't picked a character yet. Take the elevator to join them.");
                return;
            }
            if (startingRun && !gm.IsFoyer)
            {
                // The host started a new run while we're still in the old one.
                if (WaitingForHost)
                {
                    // We already pressed Quick Restart and were waiting for this.
                    RestartOntoHostRun();
                    return;
                }
                if (OnDeathScreen)
                {
                    // On the death screen we choose for ourselves; our Quick Restart then joins straight away.
                    Plugin.Log.LogInfo("Host started a new run; press Quick Restart (or take the Breach elevator) to join it.");
                    WaitingToJoin = true;
                    return;
                }
                // Alive or a ghost: come along.
                RestartOntoHostRun();
                return;
            }
            if (OnDeathScreen)
                return;
            if (gm.IsLoadingLevel)
            {
                // Starting a second load during one hangs the game.
                Plugin.Log.LogWarning("Host loaded a level while we were still loading; ignoring it.");
                return;
            }
            pendingFoyerReturn = false;
            pendingEndRun = false;

            Plugin.Log.LogInfo("Following host to level (index " + levelIndex + ", custom '" + customLevel + "', seed " + seed + ")");
            gm.InitializeForRunWithSeed(seed);
            bool wasInFoyer = gm.IsFoyer;

            if (Pixelator.Instance != null)
                Pixelator.Instance.FadeToBlack(LoadDelay, false, 0f);

            if (customLevel.Length > 0 && levelIndex < 0)
            {
                gm.DelayedLoadCustomLevel(LoadDelay, customLevel);
            }
            else
            {
                // Injected levels are picked up by LoadNextLevel itself.
                if (customLevel.Length > 0)
                    gm.InjectedLevelName = customLevel;
                gm.SetNextLevelIndex(levelIndex);
                gm.DelayedLoadNextLevel(LoadDelay);
            }

            // Same clean-up the Breach elevator does when leaving: among other things it un-hides the
            // player's gun (guns are switched off in the Breach). Without it the joiner arrives unarmed.
            if (wasInFoyer && Foyer.Instance != null)
                Foyer.Instance.OnDepartedFoyer();
        }

        public static void OnReturnToFoyer()
        {
            hostLevel = null;
            if (!GameManager.HasInstance || GameManager.Instance.IsFoyer || OnDeathScreen)
                return;
            Plugin.Log.LogInfo("Host returned to the Breach; following.");
            // Done from Update once no other load is running (two loads at once hang the game).
            pendingFoyerReturn = true;
        }

        public static void OnEndRun()
        {
            hostLevel = null;
            if (!GameManager.HasInstance || GameManager.Instance.IsFoyer)
                return;
            if (OnDeathScreen && !WaitingForHost)
            {
                // Our own death screen decides: Return to Breach works as normal, Quick Restart goes to the Breach.
                Plugin.Log.LogInfo("Host went back to the Breach; your death screen choice will take you there.");
                return;
            }
            // (If we pressed Quick Restart and were waiting for the host, the host chose the Breach: go there.)
            WaitingForHost = false;
            Plugin.Log.LogInfo("Host ended the run; ending ours too.");
            pendingEndRun = true;
        }

        private static void FollowToFoyerWhenReady()
        {
            if ((!pendingFoyerReturn && !pendingEndRun) || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (gm.IsFoyer)
            {
                pendingFoyerReturn = false;
                pendingEndRun = false;
                return;
            }
            if (gm.IsLoadingLevel)
                return;
            if (Pixelator.Instance != null)
                Pixelator.Instance.FadeToBlack(LoadDelay, false, 0f);
            if (pendingEndRun)
            {
                // Exactly what the pause menu's "Quit" and the death screen do: unload the run, back to the Breach.
                gm.DelayedLoadCharacterSelect(LoadDelay, true, false);
            }
            else
            {
                // The same delayed return the game's elevators use.
                gm.DelayedReturnToFoyer(LoadDelay);
            }
            pendingFoyerReturn = false;
            pendingEndRun = false;
        }
    }
}
