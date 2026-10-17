using System;
using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Test tool: how much memory each part of the mod creates. Only on with "--gungeon-online-allocprof" on the
    // command line. Every 30 s it logs the parts that created the most (best measured with The Garbage Collector
    // mod, which stops cleanup during play, so the managed heap only grows).
    public static class AllocProfiler
    {
        public static bool Enabled;
        private static readonly Dictionary<string, long> totals = new Dictionary<string, long>();
        private static float nextReportAt;
        private static long startTotal = -1;

        public static void Init()
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (a == "--gungeon-online-allocprof")
                    Enabled = true;
            }
            if (Enabled)
                Plugin.Log.LogInfo("Allocation profiler on.");
        }

        public static long Begin()
        {
            return Enabled ? GC.GetTotalMemory(false) : 0;
        }

        public static void End(string name, long before)
        {
            if (!Enabled)
                return;
            long d = GC.GetTotalMemory(false) - before;
            if (d <= 0)
                return;
            long t;
            totals.TryGetValue(name, out t);
            totals[name] = t + d;
        }

        public static void Report()
        {
            if (!Enabled)
                return;
            long now = GC.GetTotalMemory(false);
            if (startTotal < 0)
            {
                startTotal = now;
                nextReportAt = Time.realtimeSinceStartup + 30f;
                return;
            }
            if (Time.realtimeSinceStartup < nextReportAt)
                return;
            nextReportAt = Time.realtimeSinceStartup + 30f;
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(totals);
            list.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            string text = "Alloc report (last 30 s): heap " + (now / 1048576) + " MB (+" + ((now - startTotal) / 1024) + " KB);";
            for (int i = 0; i < list.Count && i < 12; i++)
                text += " " + list[i].Key + "=" + (list[i].Value / 1024) + "KB";
            Plugin.Log.LogInfo(text);
            totals.Clear();
            startTotal = now;
        }
    }
}
