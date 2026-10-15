using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Keeps floors identical when the two players' saves are at different points.
    //
    // The game reads save progress (rescued NPCs, unlocked items, how often rooms/items were seen...) while it
    // builds a floor, so the same seed gave a further-along joiner a different floor than the host, which could
    // fail to build at all (black screen forever). Fix: "host's world, your own save".
    // - The host sends a copy of its progress (the game's own save format, compressed, in chunks).
    // - While the joiner's game builds a floor, every progress READ is answered from the host's copy.
    // - Every progress WRITE still goes to the joiner's real progress, and only the real one is ever saved.
    //   The host's copy only lives in memory, is never saved, and is dropped when the session ends.
    // So Hegemony Credit, kills and unlocks earned online are kept on each player's own save as normal.
    // Both players' saves are also backed up once per game session before the first online run.
    public static class SaveSync
    {
        private const int ChunkSize = 12000;          // stays well under a UDP datagram for LAN
        private const float MinResendInterval = 10f;  // host: re-sends on reconnect hellos are throttled
        private const float BorrowTimeout = 90f;      // safety: never borrow longer than this
        private const float SettleTime = 1.5f;        // keep borrowing a moment after loading finishes

        private static NetSession session;

        // Joiner: the host's progress (read-only, never saved).
        private static GameStatsManager hostCopy;
        private static int receivingId = -1;
        private static byte[][] receivingChunks;
        private static int receivedCount;

        // Joiner: reads are redirected to hostCopy while this is true.
        private static bool borrowing;
        private static float borrowStartedAt;
        private static float loadDoneAt = -1f;
        private static UnityEngine.Object dungeonAtStart;

        // Host.
        private static int nextSendId;
        private static float lastSentAt = -100f;

        private static bool backedUp;

        private static MethodInfo serializeMethod, deserializeMethod;

        public static bool HasHostProgress
        {
            get { return hostCopy != null; }
        }

        public static bool Borrowing
        {
            get { return borrowing; }
        }

        // The progress reads the game does while building floors and picking loot. Writes are not patched.
        private static readonly string[] ReadMethods =
        {
            "GetFlag", "GetCharacterSpecificFlag", "GetPlayerStatValue", "GetCharacterStatValue", "GetPlayerMaximum",
            "GetNumberPastsBeaten", "AnyPastBeaten", "AllCorePastsBeaten", "HasPast", "TestPastBeaten",
            "QueryEncounterable", "QueryEncounterableDifferentiator", "QueryRoomEncountered", "QueryRoomDifferentiator",
            "QueryFlowDifferentiator", "IsForceUnlocked", "GetNumberOfCompanionsUnlocked",
            "CheckGunslingerCostumeUnlocked", "CheckLameyCostumeUnlocked",
        };

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;

            Type helpers = AccessTools.TypeByName("FullInspector.SerializationHelpers");
            Type serializer = AccessTools.TypeByName("FullInspector.FullSerializerSerializer");
            // The same generic calls the game's own SaveManager.Save/Load make: <GameStatsManager, FullSerializerSerializer>.
            // (Each name has several overloads, so pick the one with two type parameters and one argument.)
            try
            {
                MethodInfo s = FindHelper(helpers, "SerializeToContent");
                MethodInfo d = FindHelper(helpers, "DeserializeFromContent");
                if (s != null && d != null && serializer != null)
                {
                    serializeMethod = s.MakeGenericMethod(typeof(GameStatsManager), serializer);
                    deserializeMethod = d.MakeGenericMethod(typeof(GameStatsManager), serializer);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Save sync: " + e.Message);
                serializeMethod = deserializeMethod = null;
            }
            if (serializeMethod == null)
                Plugin.Log.LogError("Save sync: couldn't find the game's save serializer. Different saves may get different floors.");

            // One generic prefix on every overload of every read method. Overloads that only take the "current
            // character" are left alone: they pass the joiner's own character to the two-argument version.
            HarmonyMethod prefix = new HarmonyMethod(typeof(SaveSync), "RedirectRead");
            int patched = 0;
            foreach (MethodInfo m in typeof(GameStatsManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(ReadMethods, m.Name) < 0 || m.ReturnType == typeof(void))
                    continue;
                if ((m.Name == "GetCharacterSpecificFlag" || m.Name == "GetCharacterStatValue") && m.GetParameters().Length == 1)
                    continue;
                try
                {
                    harmony.Patch(m, prefix);
                    patched++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Save sync: couldn't patch GameStatsManager." + m.Name + ": " + e.Message);
                }
            }
            Plugin.Log.LogInfo("Save sync: watching " + patched + " progress reads.");

            // Writes made while the host's copy stands in for ours also go to our real progress.
            HarmonyMethod mirror = new HarmonyMethod(typeof(SaveSync), "MirrorWrite");
            HarmonyMethod mirrorVoid = new HarmonyMethod(typeof(SaveSync), "MirrorWriteVoid");
            int mirrored = 0;
            foreach (MethodInfo m in typeof(GameStatsManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(WriteMethods, m.Name) < 0)
                    continue;
                try
                {
                    harmony.Patch(m, m.ReturnType == typeof(void) ? mirrorVoid : mirror);
                    mirrored++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Save sync: couldn't patch GameStatsManager." + m.Name + ": " + e.Message);
                }
            }
            // Saving while swapped must save OUR progress.
            MethodInfo save = AccessTools.Method(typeof(GameStatsManager), "Save");
            if (save != null)
                harmony.Patch(save, new HarmonyMethod(typeof(SaveSync), "BeforeSave"), new HarmonyMethod(typeof(SaveSync), "AfterSave"));
            Plugin.Log.LogInfo("Save sync: " + mirrored + " progress writes kept for our own save.");
        }

        // Everything that changes progress. While the host's copy stands in, each one also runs on our real progress.
        private static readonly string[] WriteMethods =
        {
            "SetFlag", "SetCharacterSpecificFlag", "SetStat", "RegisterStatChange", "UpdateMaximum",
            "HandleEncounteredObject", "HandleEncounteredObjectRaw", "HandleEncounteredRoom", "HandleEncounteredSynergy",
            "EncounterFlow", "ForceUnlock", "SetPersistentStringLastIndex", "SetEncounterableDifferentiator",
            "SingleIncrementDifferentiator", "MarkEncounterableAnnounced", "SetNextFlag", "ClearStatValueGlobal",
            "BeginNewSession", "EndSession", "AssignMidGameSavedSessionStats", "MoveSessionStatsToSavedSessionStats",
        };

        // ---- Swapping: the host's copy IS the game's progress while a floor is built ----
        // Redirecting reads (above) missed code that reads the progress data directly, so a further-along save
        // still changed the floor. While a floor is built, GameStatsManager.Instance now returns the host's copy
        // itself. Our own run state (session) is lent to it, and every write also reaches our real progress.

        private static readonly FieldInfo instanceField = AccessTools.Field(typeof(GameStatsManager), "m_instance");
        private static readonly string[] SessionFields =
        {
            "m_sessionStats", "m_savedSessionStats", "m_sessionCharacter", "m_sessionSynergies", "isTurboMode",
            "isChump", "rainbowRunToggled", "midGameSaveGuid", "CurrentRobotArmFloor", "CurrentEeveeEquipSeed",
            "CurrentResRatShopSeed", "AccumulatedBeetleMerchantChance", "AccumulatedUsedBeetleMerchantChance",
            "CurrentAccumulatedGunderfuryExperience", "NumberRunsValidCellWithoutSpawn",
        };
        private static GameStatsManager realInstance;
        private static bool inMirror;

        private static bool Swapped
        {
            get { return realInstance != null; }
        }

        private static void CopySession(GameStatsManager from, GameStatsManager to)
        {
            foreach (string name in SessionFields)
            {
                FieldInfo f = AccessTools.Field(typeof(GameStatsManager), name);
                if (f != null)
                    f.SetValue(to, f.GetValue(from));
            }
        }

        private static void SwapIn()
        {
            if (Swapped || hostCopy == null || instanceField == null || !GameStatsManager.HasInstance)
                return;
            realInstance = GameStatsManager.Instance;
            CopySession(realInstance, hostCopy);
            instanceField.SetValue(null, hostCopy);
        }

        private static void SwapOut()
        {
            if (!Swapped)
                return;
            // Session changes made meanwhile (a run starting) went to both; take ours back as the truth.
            instanceField.SetValue(null, realInstance);
            realInstance = null;
        }

        // A write while swapped goes to our REAL progress only (so nothing earned is lost and nothing counts
        // twice); the host's copy stays exactly the host's. Our run state (session) is then lent to it again.
        private static bool MirrorWriteVoid(GameStatsManager __instance, MethodBase __originalMethod, object[] __args)
        {
            object ignored = null;
            return MirrorWrite(__instance, __originalMethod, __args, ref ignored);
        }

        private static bool MirrorWrite(GameStatsManager __instance, MethodBase __originalMethod, object[] __args, ref object __result)
        {
            if (!Swapped || inMirror || !ReferenceEquals(__instance, hostCopy))
                return true;
            inMirror = true;
            try
            {
                __result = __originalMethod.Invoke(realInstance, __args);
                CopySession(realInstance, hostCopy);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Save sync: couldn't keep " + __originalMethod.Name + " for our save: " + e.Message);
                return true;
            }
            finally
            {
                inMirror = false;
            }
        }

        private static void BeforeSave(out bool __state)
        {
            __state = Swapped;
            if (__state)
                instanceField.SetValue(null, realInstance);
        }

        private static void AfterSave(bool __state)
        {
            if (__state && Swapped)
                instanceField.SetValue(null, hostCopy);
        }

        // While borrowing, a read on the joiner's real progress is answered by the host's copy instead.
        // (Calling it on the host's copy comes back through here with __instance == hostCopy and runs normally.)
        private static bool RedirectRead(GameStatsManager __instance, MethodBase __originalMethod, object[] __args, ref object __result)
        {
            // (Not for reads that a write on our real progress makes for itself.)
            if (!borrowing || inMirror || hostCopy == null || ReferenceEquals(__instance, hostCopy))
                return true;
            try
            {
                __result = __originalMethod.Invoke(hostCopy, __args);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Save sync: host progress read " + __originalMethod.Name + " failed, using our own: " + e.Message);
                return true;
            }
        }

        // ---- Host: send our progress ----

        // Called when a joiner connects and whenever we start a new run (progress may have changed).
        public static void SendProgress(bool force)
        {
            if (session == null || session.Link == null || !session.Link.IsHost || serializeMethod == null || !GameStatsManager.HasInstance)
                return;
            if (!force && Time.realtimeSinceStartup - lastSentAt < MinResendInterval)
                return;
            BackupSavesOnce();
            byte[] packed;
            try
            {
                string json = (string)serializeMethod.Invoke(null, new object[] { GameStatsManager.Instance });
                packed = Compress(json);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Save sync: couldn't copy our progress: " + e);
                return;
            }
            lastSentAt = Time.realtimeSinceStartup;
            int id = nextSendId++;
            int count = (packed.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < count; i++)
            {
                int index = i;
                int offset = i * ChunkSize;
                int length = Math.Min(ChunkSize, packed.Length - offset);
                session.SendReliable(MsgType.Progress, delegate (BinaryWriter w)
                {
                    w.Write(id);
                    w.Write(index);
                    w.Write(count);
                    w.Write(length);
                    w.Write(packed, offset, length);
                });
            }
            Plugin.Log.LogInfo("Save sync: sent our progress to the partner (" + packed.Length / 1024 + " KB in " + count + " parts).");
        }

        // ---- Joiner: receive it ----

        public static void OnProgress(BinaryReader r)
        {
            int id = r.ReadInt32();
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int length = r.ReadInt32();
            byte[] data = r.ReadBytes(length);
            if (count <= 0 || count > 1000 || index < 0 || index >= count || data.Length != length)
                return;
            if (id != receivingId || receivingChunks == null || receivingChunks.Length != count)
            {
                receivingId = id;
                receivingChunks = new byte[count][];
                receivedCount = 0;
            }
            if (receivingChunks[index] == null)
            {
                receivingChunks[index] = data;
                receivedCount++;
            }
            if (receivedCount < count)
                return;

            byte[][] chunks = receivingChunks;
            receivingChunks = null;
            receivingId = -1;
            try
            {
                MemoryStream all = new MemoryStream();
                foreach (byte[] c in chunks)
                    all.Write(c, 0, c.Length);
                string json = Decompress(all.ToArray());
                GameStatsManager copy = (GameStatsManager)deserializeMethod.Invoke(null, new object[] { json });
                if (copy == null)
                    throw new Exception("empty result");
                if (borrowing)
                {
                    // Never swap mid-floor: half a floor built from one copy and half from another could
                    // differ from the host. Keep the old one until this load is over.
                    pendingCopy = copy;
                }
                else
                {
                    hostCopy = copy;
                }
                Plugin.Log.LogInfo("Save sync: got the host's progress; floors will be built from it.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Save sync: couldn't read the host's progress: " + e);
            }
        }

        private static GameStatsManager pendingCopy;

        // ---- Joiner: borrow while a floor is built ----

        // Called from the level-load patches when the joiner's game starts loading a dungeon floor
        // (not the Breach).
        public static void BeginBorrow()
        {
            if (session == null || session.Link == null || session.Link.IsHost || session.Status != NetSession.State.Connected)
                return;
            BackupSavesOnce();
            if (hostCopy == null)
            {
                Plugin.Log.LogWarning("Save sync: no host progress yet; building this floor from our own save.");
                return;
            }
            if (!borrowing)
                Plugin.Log.LogInfo("Save sync: building this floor from the host's progress.");
            borrowing = true;
            SwapIn();
            borrowStartedAt = Time.realtimeSinceStartup;
            loadDoneAt = -1f;
            dungeonAtStart = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
        }

        public static void EndBorrow(string why)
        {
            if (!borrowing)
                return;
            borrowing = false;
            SwapOut();
            Plugin.Log.LogInfo("Save sync: back to our own progress (" + why + ").");
            if (pendingCopy != null)
            {
                hostCopy = pendingCopy;
                pendingCopy = null;
            }
        }

        private static bool selfTested;

        // Once per game: copy our own progress the way the host does and read it back, so the log shows
        // early whether sending progress will work (without needing a partner).
        private static void SelfTest()
        {
            selfTested = true;
            try
            {
                string json = (string)serializeMethod.Invoke(null, new object[] { GameStatsManager.Instance });
                byte[] packed = Compress(json);
                GameStatsManager copy = (GameStatsManager)deserializeMethod.Invoke(null, new object[] { Decompress(packed) });
                int same = 0, total = 0;
                foreach (GungeonFlags f in Enum.GetValues(typeof(GungeonFlags)))
                {
                    if (f == GungeonFlags.NONE)
                        continue;
                    total++;
                    if (copy.GetFlag(f) == GameStatsManager.Instance.GetFlag(f))
                        same++;
                }
                Plugin.Log.LogInfo("Save sync self-test: progress copies fine (" + json.Length / 1024 + " KB, " + packed.Length / 1024
                    + " KB packed); " + same + "/" + total + " flags match.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Save sync self-test failed: " + e);
            }
        }

        public static void Update()
        {
            if (!selfTested && serializeMethod != null && GameStatsManager.HasInstance)
                SelfTest();
            bool joiner = session != null && session.Link != null && !session.Link.IsHost && session.Status == NetSession.State.Connected;
            if (!joiner)
            {
                EndBorrow("not connected to a host");
                if (session == null || session.Link == null)
                {
                    hostCopy = null;
                    pendingCopy = null;
                    receivingChunks = null;
                    receivingId = -1;
                }
                return;
            }
            if (!borrowing)
                return;
            float now = Time.realtimeSinceStartup;
            if (now - borrowStartedAt > BorrowTimeout)
            {
                EndBorrow("timed out");
                return;
            }
            // Done once the new floor exists and the game says loading is over, plus a moment to settle.
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            bool loaded = gm != null && !gm.IsLoadingLevel && gm.Dungeon != null && gm.Dungeon != dungeonAtStart && gm.PrimaryPlayer != null;
            if (!loaded)
            {
                loadDoneAt = -1f;
                return;
            }
            if (loadDoneAt < 0f)
                loadDoneAt = now;
            else if (now - loadDoneAt > SettleTime)
                EndBorrow("floor finished loading");
        }

        // ---- Backups ----

        // Copies the save folder once per game session, the first time we play online. Keeps the newest 10.
        public static void BackupSavesOnce()
        {
            if (backedUp)
                return;
            backedUp = true;
            try
            {
                string from = SaveManager.SavePath;
                if (string.IsNullOrEmpty(from) || !Directory.Exists(from))
                    return;
                string root = Path.Combine(BepInEx.Paths.ConfigPath, "GungeonOnline-save-backups");
                string to = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
                Directory.CreateDirectory(to);
                int files = 0;
                foreach (string file in Directory.GetFiles(from))
                {
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
                    files++;
                }
                Plugin.Log.LogInfo("Save sync: backed up " + files + " save files to " + to);

                List<string> old = new List<string>(Directory.GetDirectories(root));
                old.Sort(StringComparer.Ordinal);
                for (int i = 0; i < old.Count - 10; i++)
                    Directory.Delete(old[i], true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Save sync: save backup failed: " + e.Message);
            }
        }

        // ---- Helpers ----

        private static MethodInfo FindHelper(Type helpers, string name)
        {
            if (helpers == null)
                return null;
            foreach (MethodInfo m in helpers.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 1)
                    return m;
            }
            return null;
        }

        private static byte[] Compress(string text)
        {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(text);
            MemoryStream output = new MemoryStream();
            using (GZipStream gz = new GZipStream(output, CompressionMode.Compress))
                gz.Write(raw, 0, raw.Length);
            return output.ToArray();
        }

        private static string Decompress(byte[] data)
        {
            MemoryStream output = new MemoryStream();
            using (GZipStream gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                byte[] buffer = new byte[16384];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, n);
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
