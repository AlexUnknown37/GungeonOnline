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
    // - A table is pushed → every game pushes its own copy (other players have a pushing body here, TablePush),
    //   and the pusher's game sends where it went; the others glide their copy to that resting place once it stops.
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
            public float RemotePushAt = -1f; // when another player's body last pushed it here
            public Vector2? Final;           // where the pusher says it ended up (applied once ours stops moving)
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
            bool known = tables.ContainsKey(__instance);
            TrackTable(__instance);
            TableInfo info = tables[__instance];
            Plugin.Log.LogInfo("Flipped table '" + BaseName(__instance.gameObject) + "' (home " + info.Home + ", tracked before: " + known
                + ", pushable now: " + (__instance.specRigidbody != null && __instance.specRigidbody.CanBePushed) + ").");
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

        // Where a table or barrel is. A table's root object never moves: pushing moves its physics body, which sits
        // on a child (with the sprite). So tables are known by their root ("home") but tracked and moved by the body.
        private static Transform Mover(Component c)
        {
            FlippableCover t = c as FlippableCover;
            if (t != null && t.specRigidbody != null)
                return t.specRigidbody.transform;
            return c.transform;
        }

        private static Vector2 Pos(Component c)
        {
            return Mover(c).position;
        }

        private static void MoveBy(Component c, Vector2 step)
        {
            Transform m = Mover(c);
            m.position += new Vector3(step.x, step.y, 0f);
            BraveBehaviour b = c as BraveBehaviour;
            if (b != null && b.specRigidbody != null)
                b.specRigidbody.Reinitialize();
            // What the game does after a push (FlippableCover.OnPostMovement): shadow follows, depth updated.
            FlippableCover t = c as FlippableCover;
            if (t != null)
            {
                if (t.sprite != null)
                    t.sprite.UpdateZDepth();
                if (t.shadowSprite != null && t.sprite != null)
                {
                    t.shadowSprite.transform.localPosition = t.sprite.transform.localPosition;
                    t.shadowSprite.UpdateZDepth();
                }
            }
        }

        // For the test key: what we know about a table.
        public static string Describe(FlippableCover t)
        {
            TableInfo info;
            if (!tables.TryGetValue(t, out info))
                return "no (" + tables.Count + " tables known)";
            return "home " + info.Home + ", last " + info.LastKnown + ", moved at " + info.MovedAt + ", remote push at " + info.RemotePushAt;
        }

        private static void TrackTable(FlippableCover t)
        {
            if (t == null || tables.ContainsKey(t))
                return;
            TableInfo info = new TableInfo();
            info.Home = t.transform.position;
            info.LastKnown = Pos(t);
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

            // Slide things other players moved toward where they put them.
            ApplyFinals();
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
                    Vector2 at = Pos(moved[i]);
                    w.Write(at.x); w.Write(at.y);
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
                    Vector2 at = Pos(list[i]);
                    w.Write(at.x); w.Write(at.y);
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
                Vector2 delta = info.Target.Value - Pos(t);
                if (delta.sqrMagnitude < 0.0001f)
                {
                    info.Target = null;
                    continue;
                }
                Vector2 step = delta * Mathf.Min(1f, Time.deltaTime * 10f);
                if (step.sqrMagnitude < 0.0001f)
                    step = delta;
                MoveBy(t, step);
                info.LastKnown = Pos(t);
            }
        }

        // Who is closest to this table: us, or another player? (The one touching it is the one pushing.)
        private static bool PushedByOther(Component table)
        {
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            BraveBehaviour b = table as BraveBehaviour;
            Vector2 c = b != null && b.specRigidbody != null ? b.specRigidbody.UnitCenter : (Vector2)table.transform.position;
            float mine = me != null ? Vector2.Distance(me.specRigidbody != null ? me.specRigidbody.UnitBottomCenter : (Vector2)me.transform.position, c) : float.MaxValue;
            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                Vector2 feet;
                if (TablePush.TryGetFeet(i, out feet) && Vector2.Distance(feet, c) < mine)
                    return true;
            }
            return false;
        }

        // Tables other players pushed here: once our copy has stopped moving, glide it to where the pusher's game
        // says it came to rest.
        private static void ApplyFinals()
        {
            float now = Time.realtimeSinceStartup;
            foreach (KeyValuePair<FlippableCover, TableInfo> pair in tables)
            {
                TableInfo info = pair.Value;
                if (!info.Final.HasValue || pair.Key == null)
                    continue;
                if (info.RemotePushAt >= 0f && now - info.RemotePushAt < SettleTime)
                    continue; // still being pushed here
                if ((Pos(pair.Key) - info.Final.Value).sqrMagnitude > TableMoved * TableMoved)
                    info.Target = info.Final;
                info.Final = null;
            }
        }

        private static void CollectMoved<T>(Dictionary<T, TableInfo> items, byte kind, List<Component> moved, List<TableInfo> infos, List<byte> kinds)
            where T : Component
        {
            foreach (KeyValuePair<T, TableInfo> pair in items)
            {
                if (pair.Key == null || pair.Value.Target.HasValue || pair.Value.RemotelyKicked)
                    continue;
                if ((Pos(pair.Key) - pair.Value.LastKnown).sqrMagnitude > TableMoved * TableMoved)
                {
                    bool startOfMove = Time.realtimeSinceStartup - Mathf.Max(pair.Value.MovedAt, pair.Value.RemotePushAt) > 1f;
                    if (kind == (byte)Kind.Table && PushedByOther(pair.Key))
                    {
                        if (startOfMove)
                            Plugin.Log.LogInfo("Table '" + BaseName(pair.Key.gameObject) + "' (home " + pair.Value.Home + ") is being pushed here by another player.");
                        // Another player's body pushed our copy: their game reports where it goes.
                        pair.Value.LastKnown = Pos(pair.Key);
                        pair.Value.RemotePushAt = Time.realtimeSinceStartup;
                        continue;
                    }
                    if (startOfMove)
                        Plugin.Log.LogInfo((kind == (byte)Kind.Table ? "Table '" : "Barrel '") + BaseName(pair.Key.gameObject) + "' (home " + pair.Value.Home
                            + ") moved by us; reporting its position.");
                    pair.Value.LastKnown = Pos(pair.Key);
                    pair.Value.MovedAt = Time.realtimeSinceStartup;
                    pair.Value.SettleSent = false;
                    moved.Add(pair.Key);
                    infos.Add(pair.Value);
                    kinds.Add(kind);
                }
            }
        }

        private static float nextMissingLog;

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
                    {
                        if (t == null && Time.realtimeSinceStartup >= nextMissingLog)
                        {
                            nextMissingLog = Time.realtimeSinceStartup + 2f;
                            Plugin.Log.LogWarning("Got a position for table '" + name + "' (home " + home + ") but we have no such table (" + tables.Count + " known).");
                        }
                        continue;
                    }
                    TableInfo info = tables[t];
                    // We're pushing it ourselves right now: ours wins until it rests.
                    if (info.MovedAt >= 0f && Time.realtimeSinceStartup - info.MovedAt < SettleTime)
                        continue;
                    // Their body is pushing our copy too: let it play out, then settle where they say.
                    if (info.RemotePushAt >= 0f && Time.realtimeSinceStartup - info.RemotePushAt < 2f)
                    {
                        info.Final = pos;
                        continue;
                    }
                    if ((Pos(t) - pos).sqrMagnitude > TableMoved * TableMoved)
                        info.Target = pos;
                }
            }
        }
    }
}