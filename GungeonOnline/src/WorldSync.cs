using System.Collections.Generic;
using System.IO;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Room objects: breakables (barrels, crates, pots, ...) and tables.
    // - Something breaks in our game (walked/rolled into, shot, blown up) → the partner breaks their copy.
    // - A table is flipped → it flips the same way in the partner's game.
    // - A table is pushed → its new position is sent; the partner's copy slides there.
    // - A table is destroyed (shot apart, fell in a pit) → destroyed in the partner's game too.
    // Objects are matched by kind + name + position. Tables move, so each table is known by where it
    // started ("home"), which is the same in both games because the dungeon is built from the same seed.
    public static class WorldSync
    {
        private const float MatchDistance = 0.75f;  // breakables: same object if this close
        private const float TableInterval = 0.15f;  // send pushed tables ~7x a second
        private const float TableMoved = 0.02f;     // ignore tiny jitter

        private enum Kind : byte { Minor = 1, Major = 2, Table = 3 }

        private static NetSession session;
        private static bool applyingRemote;

        // Tables we know on this floor: their home (where they started) and last position sent/applied.
        private class TableInfo
        {
            public Vector2 Home;
            public Vector2 LastKnown;
            public Vector2? Target;   // where the partner says it is (we glide there)
            public bool RemotelyKicked; // barrel: the partner kicked it, so they report its roll
            public float MovedAt = -1f;   // when WE last moved it
            public bool SettleSent = true; // its resting place was sent reliably
        }

        private const float SettleTime = 0.4f;     // no movement this long = it came to rest
        private const float ResyncInterval = 3f;   // re-send every moved table this often (lost messages)
        private static float nextResync;
        private static readonly Dictionary<FlippableCover, TableInfo> tables = new Dictionary<FlippableCover, TableInfo>();
        private static Dungeonator.Dungeon tablesFloor;
        private static float nextTableScan, nextTableSend;
        private static Dungeonator.RoomHandler lastScanRoom;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(WorldSync));
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static string BaseName(GameObject go)
        {
            return go.name.Replace("(Clone)", "").Trim();
        }

        // ---- Breaking ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MinorBreakable), "Break", new System.Type[] { typeof(Vector2) })]
        private static void BeforeMinorBreak(MinorBreakable __instance, out bool __state)
        {
            __state = __instance.IsBroken;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MinorBreakable), "Break", new System.Type[] { typeof(Vector2) })]
        private static void AfterMinorBreak(MinorBreakable __instance, Vector2 direction, bool __state)
        {
            if (!__state && __instance.IsBroken)
                SendBreak(Kind.Minor, __instance.gameObject, __instance.transform.position, direction);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MinorBreakable), "Break", new System.Type[0])]
        private static void BeforeMinorBreakNoDir(MinorBreakable __instance, out bool __state)
        {
            __state = __instance.IsBroken;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MinorBreakable), "Break", new System.Type[0])]
        private static void AfterMinorBreakNoDir(MinorBreakable __instance, bool __state)
        {
            if (!__state && __instance.IsBroken)
                SendBreak(Kind.Minor, __instance.gameObject, __instance.transform.position, Vector2.zero);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MajorBreakable), "Break")]
        private static void BeforeMajorBreak(MajorBreakable __instance, out bool __state)
        {
            __state = __instance.IsDestroyed;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MajorBreakable), "Break")]
        private static void AfterMajorBreak(MajorBreakable __instance, Vector2 sourceDirection, bool __state)
        {
            if (!__state && __instance.IsDestroyed && __instance.GetComponent<FlippableCover>() == null)
                SendBreak(Kind.Major, __instance.gameObject, __instance.transform.position, sourceDirection);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlippableCover), "DestroyCover", new System.Type[] { typeof(bool), typeof(IntVector2?) })]
        private static void AfterDestroyCover(FlippableCover __instance)
        {
            TableInfo info;
            if (tables.TryGetValue(__instance, out info))
                SendBreak(Kind.Table, __instance.gameObject, info.Home, Vector2.zero);
        }

        private static void SendBreak(Kind kind, GameObject go, Vector2 pos, Vector2 direction)
        {
            if (applyingRemote || !Connected)
                return;
            string level = LevelSync.CurrentLevelKey();
            string name = BaseName(go);
            Plugin.Log.LogInfo("Broke " + kind + " '" + name + "' at " + pos + "; telling partner.");
            session.SendReliable(MsgType.Break, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)kind);
                w.Write(name);
                w.Write(pos.x); w.Write(pos.y);
                w.Write(direction.x); w.Write(direction.y);
            });
        }

        public static void OnBreak(BinaryReader r)
        {
            string level = r.ReadString();
            Kind kind = (Kind)r.ReadByte();
            string name = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            Vector2 direction = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (level != LevelSync.CurrentLevelKey())
                return;

            applyingRemote = true;
            try
            {
                if (kind == Kind.Minor)
                {
                    MinorBreakable m = Nearest<MinorBreakable>(name, pos, delegate (MinorBreakable b) { return !b.IsBroken; });
                    if (m != null)
                        m.Break(direction);
                    else
                        ExplainMiss<MinorBreakable>(kind, name, pos);
                }
                else if (kind == Kind.Major)
                {
                    MajorBreakable m = Nearest<MajorBreakable>(name, pos, delegate (MajorBreakable b) { return !b.IsDestroyed; });
                    if (m != null)
                        m.Break(direction);
                    else
                        ExplainMiss<MajorBreakable>(kind, name, pos);
                }
                else
                {
                    FlippableCover t = TableAtHome(name, pos);
                    if (t != null && !t.IsBroken)
                        t.DestroyCover();
                }
            }
            finally
            {
                applyingRemote = false;
            }
        }

        // Our copy of what the partner broke wasn't found: log the closest candidate so we can see why.
        private static void ExplainMiss<T>(Kind kind, string name, Vector2 pos) where T : Component
        {
            T closest = null;
            float dist = float.MaxValue;
            foreach (T c in Object.FindObjectsOfType(typeof(T)))
            {
                if (c == null)
                    continue;
                float d = Vector2.Distance(c.transform.position, pos);
                if (d < dist)
                {
                    closest = c;
                    dist = d;
                }
            }
            Plugin.Log.LogWarning("Partner broke " + kind + " '" + name + "' at " + pos + " but we have no unbroken match. Closest "
                + typeof(T).Name + ": " + (closest == null ? "none" : "'" + BaseName(closest.gameObject) + "' " + dist.ToString("F2") + " away"));
        }

        private static T Nearest<T>(string name, Vector2 pos, System.Predicate<T> ok) where T : Component
        {
            T best = null;
            float bestDist = MatchDistance;
            foreach (T c in Object.FindObjectsOfType(typeof(T)))
            {
                if (c == null || !ok(c) || BaseName(c.gameObject) != name)
                    continue;
                float d = Vector2.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    best = c;
                    bestDist = d;
                }
            }
            return best;
        }

        // ---- Table flips ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlippableCover), "Flip", new System.Type[] { typeof(DungeonData.Direction) })]
        private static void AfterFlip(FlippableCover __instance, DungeonData.Direction flipDirection)
        {
            if (applyingRemote || !Connected)
                return;
            TrackTable(__instance);
            TableInfo info = tables[__instance];
            string level = LevelSync.CurrentLevelKey();
            string name = BaseName(__instance.gameObject);
            Vector2 home = info.Home;
            session.SendReliable(MsgType.TableFlip, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(name);
                w.Write(home.x); w.Write(home.y);
                w.Write((int)flipDirection);
            });
        }

        public static void OnTableFlip(BinaryReader r)
        {
            string level = r.ReadString();
            string name = r.ReadString();
            Vector2 home = new Vector2(r.ReadSingle(), r.ReadSingle());
            DungeonData.Direction dir = (DungeonData.Direction)r.ReadInt32();
            if (level != LevelSync.CurrentLevelKey())
                return;
            FlippableCover t = TableAtHome(name, home);
            if (t == null || t.IsFlipped || t.IsBroken)
                return;
            applyingRemote = true;
            try { t.Flip(dir); }
            finally { applyingRemote = false; }
        }

        // ---- Rolling barrels (KickableObject) ----
        // A kick is replayed in the partner's game with the kicker where the partner stands (their invisible
        // body), so it rolls the same way with the same animation/goop trail. While rolling, positions are sent
        // like tables (see Update) so both copies end in the same place.
        private static readonly Dictionary<KickableObject, TableInfo> barrels = new Dictionary<KickableObject, TableInfo>();
        private static readonly System.Reflection.MethodInfo kickMethod = AccessTools.Method(typeof(KickableObject), "Kick");

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KickableObject), "Kick")]
        private static void AfterKick(KickableObject __instance, SpeculativeRigidbody kickerRigidbody)
        {
            if (applyingRemote || !Connected || kickerRigidbody == null)
                return;
            TrackBarrel(__instance);
            barrels[__instance].RemotelyKicked = false; // our kick: we report the roll
            Vector2 home = barrels[__instance].Home;
            Vector2 kicker = kickerRigidbody.UnitCenter;
            string level = LevelSync.CurrentLevelKey();
            string name = BaseName(__instance.gameObject);
            session.SendReliable(MsgType.Kick, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(name);
                w.Write(home.x); w.Write(home.y);
                w.Write(kicker.x); w.Write(kicker.y);
            });
        }

        public static void OnKick(BinaryReader r)
        {
            string level = r.ReadString();
            string name = r.ReadString();
            Vector2 home = new Vector2(r.ReadSingle(), r.ReadSingle());
            r.ReadSingle(); r.ReadSingle(); // kicker position (the partner's body is already there)
            if (level != LevelSync.CurrentLevelKey())
                return;
            KickableObject k = BarrelAtHome(name, home);
            SpeculativeRigidbody kicker = TargetSync.PartnerBody;
            if (k == null || kicker == null)
                return;
            applyingRemote = true;
            barrels[k].RemotelyKicked = true;
            try { kickMethod.Invoke(k, new object[] { kicker }); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Couldn't replay partner's kick: " + e.Message); }
            finally { applyingRemote = false; }
        }

        private static void TrackBarrel(KickableObject k)
        {
            if (k == null || barrels.ContainsKey(k))
                return;
            TableInfo info = new TableInfo();
            info.Home = k.transform.position;
            info.LastKnown = info.Home;
            barrels[k] = info;
        }

        private static KickableObject BarrelAtHome(string name, Vector2 home)
        {
            KickableObject best = null;
            float bestDist = MatchDistance;
            foreach (KeyValuePair<KickableObject, TableInfo> pair in barrels)
            {
                if (pair.Key == null || BaseName(pair.Key.gameObject) != name)
                    continue;
                float d = Vector2.Distance(pair.Value.Home, home);
                if (d < bestDist)
                {
                    best = pair.Key;
                    bestDist = d;
                }
            }
            return best;
        }

        // ---- Table positions (pushing) ----

        private static void TrackTable(FlippableCover t)
        {
            if (t == null || tables.ContainsKey(t))
                return;
            TableInfo info = new TableInfo();
            info.Home = t.transform.position;
            info.LastKnown = info.Home;
            tables[t] = info;
        }

        private static FlippableCover TableAtHome(string name, Vector2 home)
        {
            FlippableCover best = null;
            float bestDist = MatchDistance;
            foreach (KeyValuePair<FlippableCover, TableInfo> pair in tables)
            {
                if (pair.Key == null || BaseName(pair.Key.gameObject) != name)
                    continue;
                float d = Vector2.Distance(pair.Value.Home, home);
                if (d < bestDist)
                {
                    best = pair.Key;
                    bestDist = d;
                }
            }
            return best;
        }

        // Learn every table and rolling barrel on the floor while it's still where it started.
        private static void ScanTables()
        {
            Dungeonator.Dungeon d = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (d != tablesFloor)
            {
                tables.Clear();
                barrels.Clear();
                tablesFloor = d;
            }
            foreach (FlippableCover t in Object.FindObjectsOfType(typeof(FlippableCover)))
                TrackTable(t);
            foreach (KickableObject k in Object.FindObjectsOfType(typeof(KickableObject)))
                TrackBarrel(k);
        }

        public static void Update()
        {
            if (!Connected || !GameManager.HasInstance || GameManager.Instance.IsLoadingLevel)
                return;
            float now = Time.realtimeSinceStartup;
            // Searching the whole scene is slow, so only do it when we arrive on a floor or enter a new room
            // (and rarely otherwise), not every second (that caused hitches).
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            Dungeonator.RoomHandler room = me != null ? me.CurrentRoom : null;
            if (room != lastScanRoom || GameManager.Instance.Dungeon != tablesFloor || now >= nextTableScan)
            {
                lastScanRoom = room;
                nextTableScan = now + 15f;
                ScanTables();
            }

            // Slide things the partner moved toward where they put them.
            Glide(tables);
            Glide(barrels);

            if (now < nextTableSend)
                return;
            nextTableSend = now + TableInterval;

            // Send things WE moved (changed since we last sent or applied a position). A barrel's roll is only
            // reported by the game that kicked it (the other copy rolls too, and would echo it back).
            List<Component> moved = new List<Component>();
            List<TableInfo> infos = new List<TableInfo>();
            List<byte> kinds = new List<byte>();
            CollectMoved(tables, (byte)Kind.Table, moved, infos, kinds);
            CollectMoved(barrels, KickKind, moved, infos, kinds);
            SendSettledAndResync(now);
            if (moved.Count == 0)
                return;
            string level = LevelSync.CurrentLevelKey();
            session.SendUnreliable(MsgType.TablePositions, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((ushort)moved.Count);
                for (int i = 0; i < moved.Count; i++)
                {
                    w.Write(kinds[i]);
                    w.Write(BaseName(moved[i].gameObject));
                    w.Write(infos[i].Home.x); w.Write(infos[i].Home.y);
                    w.Write(moved[i].transform.position.x); w.Write(moved[i].transform.position.y);
                }
            });
        }

        private const byte KickKind = 4;

        // Movement is sent fast but without delivery guarantee, so a lost last message left the table in a
        // different place on the other screen. Safety nets:
        // - when a table WE moved comes to rest, its resting place is sent with guaranteed delivery;
        // - every few seconds, every table we moved away from its starting place is sent again.
        private static void SendSettledAndResync(float now)
        {
            List<Component> list = new List<Component>();
            List<TableInfo> infos = new List<TableInfo>();
            bool resync = now >= nextResync;
            if (resync)
                nextResync = now + ResyncInterval;
            foreach (KeyValuePair<FlippableCover, TableInfo> pair in tables)
            {
                TableInfo info = pair.Value;
                if (pair.Key == null || pair.Key.IsBroken || info.Target.HasValue || info.MovedAt < 0f)
                    continue;
                bool settled = !info.SettleSent && now - info.MovedAt > SettleTime;
                if (!settled && !resync)
                    continue;
                if (settled)
                    info.SettleSent = true;
                list.Add(pair.Key);
                infos.Add(info);
                if (list.Count >= 40)
                    break;
            }
            if (list.Count == 0)
                return;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.TablePositions, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((ushort)list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    w.Write((byte)Kind.Table);
                    w.Write(BaseName(list[i].gameObject));
                    w.Write(infos[i].Home.x); w.Write(infos[i].Home.y);
                    w.Write(list[i].transform.position.x); w.Write(list[i].transform.position.y);
                }
            });
        }

        private static void Glide<T>(Dictionary<T, TableInfo> items) where T : BraveBehaviour
        {
            foreach (KeyValuePair<T, TableInfo> pair in items)
            {
                T t = pair.Key;
                TableInfo info = pair.Value;
                if (t == null || !info.Target.HasValue)
                    continue;
                Vector2 delta = info.Target.Value - (Vector2)t.transform.position;
                if (delta.sqrMagnitude < 0.0001f)
                {
                    info.Target = null;
                    continue;
                }
                Vector2 step = delta * Mathf.Min(1f, Time.deltaTime * 10f);
                if (step.sqrMagnitude < 0.0001f)
                    step = delta;
                t.transform.position += new Vector3(step.x, step.y, 0f);
                if (t.specRigidbody != null)
                    t.specRigidbody.Reinitialize();
                info.LastKnown = t.transform.position;
            }
        }

        private static void CollectMoved<T>(Dictionary<T, TableInfo> items, byte kind, List<Component> moved, List<TableInfo> infos, List<byte> kinds)
            where T : Component
        {
            foreach (KeyValuePair<T, TableInfo> pair in items)
            {
                if (pair.Key == null || pair.Value.Target.HasValue || pair.Value.RemotelyKicked)
                    continue;
                if (((Vector2)pair.Key.transform.position - pair.Value.LastKnown).sqrMagnitude > TableMoved * TableMoved)
                {
                    pair.Value.LastKnown = pair.Key.transform.position;
                    pair.Value.MovedAt = Time.realtimeSinceStartup;
                    pair.Value.SettleSent = false;
                    moved.Add(pair.Key);
                    infos.Add(pair.Value);
                    kinds.Add(kind);
                }
            }
        }

        public static void OnTablePositions(BinaryReader r)
        {
            string level = r.ReadString();
            int count = r.ReadUInt16();
            bool sameLevel = level == LevelSync.CurrentLevelKey();
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadByte();
                string name = r.ReadString();
                Vector2 home = new Vector2(r.ReadSingle(), r.ReadSingle());
                Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
                if (!sameLevel)
                    continue;
                if (kind == KickKind)
                {
                    KickableObject k = BarrelAtHome(name, home);
                    if (k != null)
                        barrels[k].Target = pos;
                }
                else
                {
                    FlippableCover t = TableAtHome(name, home);
                    if (t == null || t.IsBroken)
                        continue;
                    TableInfo info = tables[t];
                    // We're pushing it ourselves right now: ours wins until it rests.
                    if (info.MovedAt >= 0f && Time.realtimeSinceStartup - info.MovedAt < SettleTime)
                        continue;
                    if (((Vector2)t.transform.position - pos).sqrMagnitude > TableMoved * TableMoved)
                        info.Target = pos;
                }
            }
        }
    }
}