using System.Collections.Generic;
using System.IO;
using System.Text;
using Dungeonator;
using UnityEngine;

namespace GungeonOnline
{
    // Safety net for floor sync: after a floor finishes loading, each game takes a fingerprint of it (every room's
    // name, position and size). The host sends its fingerprint; the joiner compares. If they differ, the joiner's
    // floor is wrong: it reloads the host's floor (up to MaxRetries times per floor), and the log lists the first
    // rooms that differ so the cause can be found. If it still differs, a warning stays on screen.
    public static class FloorCheck
    {
        private const int MaxRetries = 2;

        private static NetSession session;
        private static Dungeon printed;           // the floor our fingerprint belongs to
        private static string ourKey = "";
        private static uint ourHash;
        private static List<string> ourRooms = new List<string>();
        private static float loadedAt = -1f;

        // Joiner: the host's fingerprint for a floor.
        private static string hostKey = "";
        private static uint hostHash;
        private static List<string> hostRooms = new List<string>();
        private static bool compared;
        private static int retries;
        private static string retriesForKey = "";

        public static bool Mismatch { get; private set; }

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected && session.Link != null; }
        }

        public static void Update()
        {
            if (!Connected || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (gm.IsLoadingLevel || gm.Dungeon == null || gm.IsFoyer || gm.PrimaryPlayer == null)
            {
                loadedAt = -1f;
                return;
            }
            if (gm.Dungeon == printed)
            {
                if (!session.Link.IsHost && !compared)
                    Compare();
                return;
            }
            // A new floor: wait a moment for it to settle, then take its fingerprint.
            if (loadedAt < 0f)
            {
                loadedAt = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - loadedAt < 1f)
                return;
            TakePrint(gm.Dungeon);
            compared = false;
            if (session.Link.IsHost)
                SendPrint();
        }

        // Host: someone (re)joined; tell them what our floor looks like.
        public static void OnConnected()
        {
            if (Connected && session.Link.IsHost && printed != null && printed == (GameManager.HasInstance ? GameManager.Instance.Dungeon : null))
                SendPrint();
        }

        private static void TakePrint(Dungeon d)
        {
            printed = d;
            ourKey = LevelSync.CurrentLevelKey() + "|" + GameManager.Instance.CurrentRunSeed;
            ourRooms = new List<string>();
            foreach (RoomHandler room in d.data.rooms)
            {
                if (room == null || room.area == null)
                {
                    ourRooms.Add("?");
                    continue;
                }
                CellArea a = room.area;
                ourRooms.Add((a.PrototypeRoomName ?? "procedural") + "@" + a.basePosition.x + "," + a.basePosition.y
                    + "/" + a.dimensions.x + "x" + a.dimensions.y);
            }
            ourHash = Hash(ourRooms);
            Plugin.Log.LogInfo("Floor fingerprint: " + ourRooms.Count + " rooms, " + ourHash.ToString("X8") + " (" + ourKey + ")");
        }

        private static uint Hash(List<string> rooms)
        {
            uint h = 2166136261;
            foreach (string r in rooms)
            {
                foreach (char c in r)
                {
                    h ^= c;
                    h *= 16777619;
                }
                h ^= 0x7C;
                h *= 16777619;
            }
            return h;
        }

        private static void SendPrint()
        {
            string key = ourKey;
            uint hash = ourHash;
            List<string> rooms = ourRooms;
            session.SendReliable(MsgType.FloorPrint, delegate (BinaryWriter w)
            {
                w.Write(key);
                w.Write(hash);
                w.Write(rooms.Count);
                foreach (string r in rooms)
                    w.Write(r);
            });
        }

        public static void OnFloorPrint(BinaryReader r)
        {
            hostKey = r.ReadString();
            hostHash = r.ReadUInt32();
            int n = r.ReadInt32();
            hostRooms = new List<string>();
            for (int i = 0; i < n && i < 2000; i++)
                hostRooms.Add(r.ReadString());
            compared = false;
        }

        private static void Compare()
        {
            if (hostKey.Length == 0 || hostKey != ourKey)
                return; // not the host's fingerprint for this floor (yet)
            compared = true;
            if (hostHash == ourHash && hostRooms.Count == ourRooms.Count)
            {
                if (Mismatch)
                    Toast.Show("Floor now matches the host's.", 4f);
                Mismatch = false;
                Plugin.Log.LogInfo("Floor matches the host's (" + ourHash.ToString("X8") + ").");
                return;
            }
            Mismatch = true;
            // Show what differs, for finding the cause.
            StringBuilder diff = new StringBuilder();
            diff.Append("Floor does NOT match the host's: ours " + ourRooms.Count + " rooms " + ourHash.ToString("X8")
                + ", host " + hostRooms.Count + " rooms " + hostHash.ToString("X8") + ". First differences:");
            int shown = 0;
            for (int i = 0; i < Mathf.Max(ourRooms.Count, hostRooms.Count) && shown < 8; i++)
            {
                string ours = i < ourRooms.Count ? ourRooms[i] : "-";
                string theirs = i < hostRooms.Count ? hostRooms[i] : "-";
                if (ours == theirs)
                    continue;
                diff.Append("\n  room " + i + ": ours " + ours + " | host " + theirs);
                shown++;
            }
            Plugin.Log.LogWarning(diff.ToString());

            if (retriesForKey != ourKey)
            {
                retriesForKey = ourKey;
                retries = 0;
            }
            if (retries < MaxRetries && LevelSync.ReloadHostFloor())
            {
                retries++;
                Toast.Show("This floor didn't match the host's. Reloading it (try " + retries + " of " + MaxRetries + ")...", 6f);
                printed = null;
                return;
            }
            Toast.Show("This floor still doesn't match the host's. Enemies and items may be out of sync. Please send your LogOutput.log.", 15f);
        }
    }
}
