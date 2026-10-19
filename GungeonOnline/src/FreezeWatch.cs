using System;
using System.Threading;
using UnityEngine;

namespace GungeonOnline
{
    // Notices when the game stops running (no new frame for 10 s: Windows' "not responding") and writes to the
    // session log what was going on: which part of Gungeon Online last ran (or that the game itself was busy), and
    // the players' state a moment before. A frozen game shows nothing in the log by itself; this says where to look.
    public static class FreezeWatch
    {
        private const float FrozenAfter = 10f;

        private static volatile int frame;
        private static volatile string step = "starting";   // what our per-frame update is doing right now
        private static volatile string lastDone = "";       // the last part of it that finished
        private static volatile string scene = "";          // the players' state, refreshed twice a second
        private static float nextSceneAt;
        private static Thread watcher;

        public static void Start()
        {
            watcher = new Thread(Watch);
            watcher.IsBackground = true;
            watcher.Name = "GungeonOnline FreezeWatch";
            watcher.Start();
        }

        // Called at the start and end of our per-frame update.
        public static void FrameStart()
        {
            frame++;
            step = "Gungeon Online's per-frame update";
            if (Time.realtimeSinceStartup >= nextSceneAt)
            {
                nextSceneAt = Time.realtimeSinceStartup + 0.5f;
                try { scene = Describe(); }
                catch (Exception e) { scene = "(couldn't describe: " + e.Message + ")"; }
            }
        }

        public static void FrameEnd()
        {
            step = "";
        }

        public static void Done(string part)
        {
            lastDone = part;
        }

        private static string Describe()
        {
            if (!GameManager.HasInstance)
                return "no game yet";
            GameManager gm = GameManager.Instance;
            string s = (gm.IsFoyer ? "Breach" : "run") + (gm.IsLoadingLevel ? ", loading" : "") + (gm.IsPaused ? ", paused" : "")
                + ", " + gm.CurrentGameType;
            foreach (PlayerController p in gm.AllPlayers)
            {
                if (p == null)
                    continue;
                s += " | " + (p == StandIn.Player ? "bot " : "") + p.name.Replace("(Clone)", "") + " at " + p.CenterPosition
                    + " room " + (p.CurrentRoom != null ? p.CurrentRoom.GetRoomName() : "none")
                    + (p.IsFalling ? " FALLING" : "") + (p.IsGhost ? " ghost" : "") + (p.healthHaver != null && p.healthHaver.IsDead ? " dead" : "")
                    + " input " + p.CurrentInputState;
            }
            return s;
        }

        private static void Watch()
        {
            int seen = -1;
            DateTime seenAt = DateTime.Now;
            bool reported = false;
            while (true)
            {
                Thread.Sleep(1000);
                int now = frame;
                if (now != seen)
                {
                    if (reported)
                        SessionLog.Write("[FreezeWatch] The game is running again (it was stuck for "
                            + (int)(DateTime.Now - seenAt).TotalSeconds + " s).");
                    seen = now;
                    seenAt = DateTime.Now;
                    reported = false;
                    continue;
                }
                if (reported || (DateTime.Now - seenAt).TotalSeconds < FrozenAfter || seen < 0)
                    continue;
                reported = true;
                string inside = step.Length > 0 ? "inside " + step + " (after " + lastDone + ")"
                    : "outside Gungeon Online's update (the game or another mod was busy; our patches may still be involved)";
                SessionLog.Write("[FreezeWatch] THE GAME HAS NOT DRAWN A FRAME FOR " + FrozenAfter + " s. It froze " + inside + ".");
                SessionLog.Write("[FreezeWatch] Just before: " + scene);
            }
        }
    }
}
