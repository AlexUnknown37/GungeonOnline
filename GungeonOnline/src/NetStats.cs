using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GungeonOnline
{
    // How much each kind of message costs: bytes and messages sent and received per type, logged once a minute
    // (only while connected and something was sent). Used to see where the network traffic goes.
    public static class NetStats
    {
        private static readonly long[] sentBytes = new long[256];
        private static readonly int[] sentCount = new int[256];
        private static readonly long[] receivedBytes = new long[256];
        private static readonly int[] receivedCount = new int[256];
        private static float since = -1f;

        public static void Sent(MsgType type, int bytes, int copies)
        {
            int t = (byte)type;
            sentBytes[t] += (long)bytes * copies;
            sentCount[t] += copies;
        }

        public static void Received(MsgType type, int bytes)
        {
            int t = (byte)type;
            receivedBytes[t] += bytes;
            receivedCount[t]++;
        }

        public static void Update(bool connected)
        {
            float now = Time.realtimeSinceStartup;
            if (since < 0f || !connected)
            {
                since = now;
                if (!connected)
                    Clear();
                return;
            }
            if (now - since < 60f)
                return;
            float seconds = now - since;
            since = now;
            long outTotal = 0, inTotal = 0;
            List<int> types = new List<int>();
            for (int i = 0; i < 256; i++)
            {
                outTotal += sentBytes[i];
                inTotal += receivedBytes[i];
                if (sentBytes[i] + receivedBytes[i] > 0)
                    types.Add(i);
            }
            if (outTotal + inTotal == 0)
                return;
            types.Sort(delegate (int a, int b) { return (sentBytes[b] + receivedBytes[b]).CompareTo(sentBytes[a] + receivedBytes[a]); });
            StringBuilder sb = new StringBuilder();
            sb.Append("Network last minute: out ").Append(Kb(outTotal, seconds)).Append(" KB/s, in ").Append(Kb(inTotal, seconds)).Append(" KB/s. Top:");
            for (int k = 0; k < types.Count && k < 8; k++)
            {
                int t = types[k];
                sb.Append(' ').Append((MsgType)t).Append(" out ").Append(Kb(sentBytes[t], seconds)).Append('/').Append((sentCount[t] / seconds).ToString("0"))
                    .Append("msg in ").Append(Kb(receivedBytes[t], seconds)).Append('/').Append((receivedCount[t] / seconds).ToString("0")).Append("msg;");
            }
            Plugin.Log.LogInfo(sb.ToString());
            Clear();
        }

        private static string Kb(long bytes, float seconds)
        {
            return (bytes / 1024f / seconds).ToString("0.0");
        }

        private static void Clear()
        {
            for (int i = 0; i < 256; i++)
            {
                sentBytes[i] = receivedBytes[i] = 0;
                sentCount[i] = receivedCount[i] = 0;
            }
        }
    }
}
