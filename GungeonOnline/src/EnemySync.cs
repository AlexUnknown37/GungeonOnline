using System.Collections.Generic;
using System.IO;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Enemy sync: shared health, deaths and positions.
    // Every game runs its own copy of every enemy (same seed = same rooms and enemies). Each enemy gets
    // the same ID in both games: its room's index in the dungeon plus the order it joined that room.
    // - Damage dealt to an enemy in one game is sent to the other, so health matches (bosses too).
    // - An enemy that dies in one game dies in the other; enemies already killed by the partner die as
    //   soon as they appear when you enter that room later.
    // - The game in charge of a room (the lowest-numbered living player in it) sends its enemies' positions,
    //   velocities, targets and health 15x a second; the other games walk their copies along the same path and
    //   never let an enemy have more health than the sender's copy.
    // Which attack an enemy starts, and at whom, is decided by the game in charge of the room (see AttackSync).
    public static class EnemySync
    {
        private const float StateInterval = 1f / 15f;
        private const float KillHealth = 1000000f;

        private static NetSession session;

        private static Dungeon currentDungeon;
        private static readonly Dictionary<AIActor, int> idOf = new Dictionary<AIActor, int>();
        private static readonly Dictionary<int, AIActor> byId = new Dictionary<int, AIActor>();
        private static readonly Dictionary<RoomHandler, int> roomCounters = new Dictionary<RoomHandler, int>();
        private static readonly HashSet<int> deadIds = new HashSet<int>();
        private static readonly List<AIActor> killQueue = new List<AIActor>();
        private static readonly Dictionary<AIActor, float> queuedAt = new Dictionary<AIActor, float>();
        private static float nextStateAt;

        // Set while we apply damage that came from the network, so it isn't sent back.
        private static bool applyingRemote;
        // Set while one of the partner's (copied) bullets is hitting something: its real damage is applied
        // in the partner's game and arrives as an EnemyDamage message, so it must not count twice.
        public static bool RemoteBulletHitting;
        // Set while one of OUR bullets is hitting something. Only those hits are sent as EnemyDamage: other
        // damage (fire, explosions, acid from thrown items, ...) happens in both games by itself because the
        // causes are copied, so sending it too would count it twice.
        public static bool LocalBulletHitting;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(EnemySync));
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        // A new floor means new rooms: forget all IDs.
        private static void CheckFloor()
        {
            Dungeon d = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (d == currentDungeon)
                return;
            currentDungeon = d;
            idOf.Clear();
            byId.Clear();
            roomCounters.Clear();
            deadIds.Clear();
            killQueue.Clear();
            queuedAt.Clear();
            corrections.Clear();
            driven.Clear();
            clearedSent.Clear();
            clearedByOthers.Clear();
        }

        // ---- IDs ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomHandler), "RegisterEnemy")]
        private static void AfterRegisterEnemy(RoomHandler __instance, AIActor enemy)
        {
            if (enemy == null || !GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return;
            CheckFloor();
            if (idOf.ContainsKey(enemy))
                return;
            int roomIndex = GameManager.Instance.Dungeon.data.rooms.IndexOf(__instance);
            if (roomIndex < 0)
                return;
            int counter;
            roomCounters.TryGetValue(__instance, out counter);
            roomCounters[__instance] = counter + 1;
            int id = roomIndex * 1000 + counter;
            idOf[enemy] = id;
            byId[id] = enemy;
            if (deadIds.Contains(id))
            {
                killQueue.Add(enemy); // the partner already killed this one
                queuedAt[enemy] = Time.realtimeSinceStartup;
            }
        }

        public static bool TryGetId(AIActor a, out int id)
        {
            id = 0;
            return a != null && idOf.TryGetValue(a, out id);
        }

        public static AIActor ById(int id)
        {
            AIActor a;
            return byId.TryGetValue(id, out a) ? a : null;
        }

        // Does our game lead this enemy (decide its attacks)? Yes when we're in charge of its room, or when
        // nobody is online.
        public static bool LeadsEnemy(AIActor a)
        {
            if (!Connected || a == null)
                return true;
            RoomHandler room = a.ParentRoom;
            if (Time.frameCount != leadFrame)
            {
                leadFrame = Time.frameCount;
                leads.Clear();
            }
            bool lead;
            if (room == null)
                return true;
            if (!leads.TryGetValue(room, out lead))
            {
                lead = InCharge(room) && !LowerPlayerInRoom(room);
                leads[room] = lead;
            }
            return lead;
        }
        private static readonly Dictionary<RoomHandler, bool> leads = new Dictionary<RoomHandler, bool>();
        private static int leadFrame = -1;

        // Put an enemy right where the leader's game has it (before copying an attack, so it starts from there).
        public static void PlaceAt(AIActor a, Vector2 center)
        {
            if (a == null || a.specRigidbody == null)
                return;
            Vector2 delta = center - a.specRigidbody.UnitCenter;
            if (delta.sqrMagnitude < 0.01f)
                return;
            a.transform.position += new Vector3(delta.x, delta.y, 0f);
            a.specRigidbody.Reinitialize();
            corrections.Remove(a);
            Release(a);
        }

        private static AIActor Find(int id, string guid)
        {
            AIActor a;
            if (!byId.TryGetValue(id, out a) || a == null || a.healthHaver == null || a.healthHaver.IsDead)
                return null;
            // Same ID but a different kind of enemy means the two games spawned differently; ignore it.
            return a.EnemyGuid == guid ? a : null;
        }

        // ---- Damage ----

        // __state: the health before this hit, or -1 when the hit doesn't happen here or the enemy was already dead
        // (hits on a dead body happen a lot in a busy fight and must not report the death again).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealthHaver), "ApplyDamage")]
        private static bool BeforeApplyDamage(HealthHaver __instance, out float __state)
        {
            __state = -1f;
            if (__instance.aiActor != null && Connected && !applyingRemote)
            {
                if (RemoteBulletHitting)
                    return false; // the partner's game deals this damage and tells us
                // While we're a ghost, only the living partner's game decides what enemies take damage or die
                // (a ghost's pulse may knock enemies around, but must never kill them).
                if (LocalIsGhost)
                    return false;
            }
            if (!__instance.IsDead)
                __state = __instance.GetCurrentHealth();
            return true;
        }

        private static bool LocalIsGhost
        {
            get
            {
                PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                return me != null && (me.IsGhost || me.healthHaver.IsDead);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HealthHaver), "ApplyDamage")]
        private static void AfterApplyDamage(HealthHaver __instance, DamageCategory __4, float __state)
        {
            if (applyingRemote || !Connected || __instance.aiActor == null || __state < 0f)
                return;
            int id;
            if (!idOf.TryGetValue(__instance.aiActor, out id))
                return;
            float dealt = __state - __instance.GetCurrentHealth();
            int level = LevelSync.CurrentLevelCode();
            string guid = __instance.aiActor.EnemyGuid;
            if (__instance.IsDead)
            {
                deadIds.Add(id);
                // Burning or poison ticks in every game at once: then only the room leader's game reports the death.
                // Anything else that kills here (our bullet, explosion, companion) is ours to report.
                if (__4 == DamageCategory.DamageOverTime && !LocalBulletHitting && !LeadsEnemy(__instance.aiActor))
                    return;
                session.SendReliable(MsgType.EnemyDied, delegate (BinaryWriter w)
                {
                    w.Write(level); w.Write(id); w.Write(guid);
                });
            }
            else if (dealt > 0f && LocalBulletHitting)
            {
                session.SendReliable(MsgType.EnemyDamage, delegate (BinaryWriter w)
                {
                    w.Write(level); w.Write(id); w.Write(guid); w.Write(dealt);
                });
            }
        }

        public static void OnEnemyDamage(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            string guid = r.ReadString();
            float amount = r.ReadSingle();
            if (level != LevelSync.CurrentLevelCode())
                return;
            CheckFloor();
            AIActor a = Find(id, guid);
            if (a != null)
                ApplyRemoteDamage(a, amount);
        }

        public static void OnEnemyDied(BinaryReader r)
        {
            int level = r.ReadInt32();
            int id = r.ReadInt32();
            string guid = r.ReadString();
            if (level != LevelSync.CurrentLevelCode())
                return;
            CheckFloor();
            deadIds.Add(id);
            AIActor a = Find(id, guid);
            if (a != null)
                KillFromPartner(a);
        }

        // The game refuses a killing blow on a boss while the (single-player) player is dead, so a host who was
        // a ghost could never kill the boss the guest had killed (it kept trying). Damage from the partner's
        // game is always allowed through.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealthHaver), "BossHealthSanityCheck")]
        private static bool BeforeBossHealthSanityCheck(ref bool __result)
        {
            if (!applyingRemote)
                return true;
            __result = true;
            return false;
        }

        // Enemies that died because the partner's game said so. Their money and items dropped in the partner's game
        // (the purse is shared), so here they die without a drop: entering a room the partner cleared paid out again.
        // Bosses still drop (Master Round, boss rewards are each player's own).
        private static readonly HashSet<AIActor> remotelyKilled = new HashSet<AIActor>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AIActor), "HandleLootPinata")]
        private static bool BeforeLootDrop(AIActor __instance)
        {
            if (!remotelyKilled.Remove(__instance))
                return true;
            return __instance.healthHaver != null && __instance.healthHaver.IsBoss;
        }

        private static void KillFromPartner(AIActor a)
        {
            if (remotelyKilled.Count > 500)
                remotelyKilled.Clear();
            remotelyKilled.Add(a);
            ApplyRemoteDamage(a, KillHealth);
            if (a == null || a.healthHaver == null || !a.healthHaver.IsDead)
                remotelyKilled.Remove(a);
        }

        private static void ApplyRemoteDamage(AIActor a, float amount)
        {
            applyingRemote = true;
            try
            {
                a.healthHaver.ApplyDamage(amount, Vector2.zero, "Partner", CoreDamageTypes.None, DamageCategory.Normal, true, null, true);
            }
            finally
            {
                applyingRemote = false;
            }
        }

        // ---- Positions and health ----
        // Whoever is ALIVE and IN the room is in charge of that room's enemies: they send positions/health,
        // the other game follows. (Before, only the host sent them, so with the host a ghost or elsewhere the
        // enemies froze for the host and rubber-banded on the guest.)

        private class Correction
        {
            public Vector2 Target;
            public Vector2 Velocity;
            public float ReceivedAt;
        }

        public static int KindOf(AIActor a)
        {
            return KindHash(a != null ? a.EnemyGuid : null);
        }

        // The same number for the same kind of enemy in every game (string.GetHashCode isn't promised to be).
        private static int KindHash(string guid)
        {
            unchecked
            {
                int h = (int)2166136261;
                if (guid != null)
                {
                    foreach (char ch in guid)
                        h = (h ^ ch) * 16777619;
                }
                return h;
            }
        }

        // Enemies whose walking we drive from the leading game's reports (their own movement is switched off).
        private static readonly HashSet<AIActor> driven = new HashSet<AIActor>();
        private const float FollowGain = 5f;       // how hard a driven enemy steers back onto the reported path

        private static void Release(AIActor a)
        {
            if (!driven.Remove(a) || a == null)
                return;
            a.BehaviorOverridesVelocity = false;
            a.BehaviorVelocity = Vector2.zero;
            if (a.behaviorSpeculator != null)
                a.behaviorSpeculator.PreventMovement = false;
        }
        private static readonly Dictionary<AIActor, Correction> corrections = new Dictionary<AIActor, Correction>();
        private const float SnapDistance = 4f;      // further than this: jump
        private const float SettleDistance = 0.25f; // closer than this: leave it alone
        private const float PullSpeed = 6f;         // how quickly a drifted enemy glides back

        private static float sealedEmptySince = -1f;

        private static PlayerController Me
        {
            get { return GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null; }
        }

        // Are we in charge of this room's enemies?
        private static bool InCharge(RoomHandler room)
        {
            PlayerController me = Me;
            if (me != null && !LocalIsGhost && room != null && me.CurrentRoom == room)
                return true;
            // The host's game also runs a stand-in bot: where it fights alone, the host's game is in charge.
            PlayerController bot = StandIn.Active ? StandIn.Player : null;
            return room != null && bot != null && !bot.IsGhost && bot.healthHaver != null && !bot.healthHaver.IsDead
                && bot.CurrentRoom == room;
        }

        // Is a player with a lower number (the host first) alive in this room too? Then their game reports its
        // enemies and ours doesn't, so the other games don't get two different reports.
        private static bool LowerPlayerInRoom(RoomHandler room)
        {
            if (room == null || GameManager.Instance.Dungeon == null)
                return false;
            int level = LevelSync.CurrentLevelCode();
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (p.Slot >= session.LocalSlot || !p.HasPlayer || p.IsDead || p.Scene != LevelSync.CurrentLevelKey())
                    continue;
                Vector2 f = p.FeetPosition;
                RoomHandler theirs = GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(
                    new IntVector2(Mathf.FloorToInt(f.x), Mathf.FloorToInt(f.y)));
                if (theirs == room)
                    return true;
            }
            return false;
        }

        public static void Update()
        {
            if (!Connected || !GameManager.HasInstance)
                return;
            CheckFloor();
            float now = Time.realtimeSinceStartup;

            // Enemies the partner already killed: finish them a moment after they appear (killing them during
            // the room's own setup left the room sealed).
            for (int i = killQueue.Count - 1; i >= 0; i--)
            {
                AIActor a = killQueue[i];
                if (a == null || a.healthHaver == null || a.healthHaver.IsDead)
                {
                    killQueue.RemoveAt(i);
                    continue;
                }
                float since;
                queuedAt.TryGetValue(a, out since);
                if ((a.ParentRoom != null && a.ParentRoom.IsSealed) || now - since > 1.5f)
                {
                    killQueue.RemoveAt(i);
                    KillFromPartner(a);
                }
            }

            ApplyCorrections();
            UnsealIfStuck(now);
            KeepClearedRoomOpen(now);
            SendClearedRoomsToArrivals();

            if (now < nextStateAt)
                return;
            nextStateAt = now + StateInterval;

            List<int> ids = new List<int>();
            List<AIActor> actors = new List<AIActor>();
            foreach (KeyValuePair<int, AIActor> pair in byId)
            {
                AIActor a = pair.Value;
                if (a != null && a.healthHaver != null && !a.healthHaver.IsDead && a.gameObject.activeInHierarchy
                    && InCharge(a.ParentRoom) && !LowerPlayerInRoom(a.ParentRoom))
                {
                    ids.Add(pair.Key);
                    actors.Add(a);
                }
            }
            if (ids.Count == 0)
                return;
            int level = LevelSync.CurrentLevelCode();
            session.SendUnreliable(MsgType.EnemyStates, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((ushort)ids.Count);
                for (int i = 0; i < ids.Count; i++)
                {
                    Vector2 p = actors[i].specRigidbody != null ? actors[i].specRigidbody.UnitCenter : (Vector2)actors[i].transform.position;
                    w.Write(ids[i]);
                    w.Write(p.x); w.Write(p.y);
                    w.Write(actors[i].healthHaver.GetCurrentHealth());
                    w.Write((byte)TargetSync.SlotOfTarget(actors[i]));
                    Vector2 v = actors[i].specRigidbody != null ? actors[i].specRigidbody.Velocity : Vector2.zero;
                    w.Write(v.x); w.Write(v.y);
                    w.Write(KindHash(actors[i].EnemyGuid));
                }
            });
        }

        public static void OnEnemyStates(BinaryReader r)
        {
            int level = r.ReadInt32();
            int count = r.ReadUInt16();
            if (level != LevelSync.CurrentLevelCode())
                return;
            CheckFloor();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                Vector2 theirPos = new Vector2(r.ReadSingle(), r.ReadSingle());
                float theirHealth = r.ReadSingle();
                int theirTarget = r.ReadByte();
                Vector2 theirVelocity = new Vector2(r.ReadSingle(), r.ReadSingle());
                int theirKind = r.ReadInt32();

                AIActor a;
                if (!byId.TryGetValue(id, out a) || a == null || a.healthHaver == null || a.healthHaver.IsDead)
                    continue;
                // Same number but another kind of enemy: the games numbered this room's enemies differently. Leave it
                // alone rather than drag the wrong enemy around.
                if (KindHash(a.EnemyGuid) != theirKind)
                    continue;
                // If we are alive in that room too, the lower player number wins (the host first); otherwise the
                // sender is in charge.
                if (InCharge(a.ParentRoom) && session.LocalSlot < session.From)
                    continue;

                // Health only ever goes down to theirs (our own hits may not have reached them yet).
                if (theirHealth < a.healthHaver.GetCurrentHealth())
                    a.healthHaver.ForceSetCurrentHealth(theirHealth);
                // Chase the same player as in the sender's game.
                TargetSync.Force(a, theirTarget);

                Correction c;
                if (!corrections.TryGetValue(a, out c))
                {
                    c = new Correction();
                    corrections[a] = c;
                }
                c.Target = theirPos;
                c.Velocity = theirVelocity;
                c.ReceivedAt = now;
            }
        }

        // Glide drifted enemies back each frame instead of snapping them (the snapping made them shudder).
        private static void ApplyCorrections()
        {
            if (corrections.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            List<AIActor> done = null;
            foreach (KeyValuePair<AIActor, Correction> pair in corrections)
            {
                AIActor a = pair.Key;
                if (a == null || a.healthHaver == null || a.healthHaver.IsDead || a.specRigidbody == null
                    || now - pair.Value.ReceivedAt > 0.5f)
                {
                    if (done == null) done = new List<AIActor>();
                    done.Add(a);
                    continue;
                }
                Correction c = pair.Value;
                // Where the leading game has it now: its last report moved on by its velocity (a short way only).
                Vector2 predicted = c.Target + c.Velocity * Mathf.Min(now - c.ReceivedAt, 0.2f);
                Vector2 delta = predicted - a.specRigidbody.UnitCenter;
                float dist = delta.magnitude;
                if (dist > SnapDistance)
                {
                    a.transform.position += new Vector3(delta.x, delta.y, 0f);
                    a.specRigidbody.Reinitialize();
                    continue;
                }
                BehaviorSpeculator spec = a.behaviorSpeculator;
                bool attacking = spec != null && spec.ActiveContinuousAttackBehavior != null;
                if (spec != null && !attacking)
                {
                    // Walk it along the leader's path at the leader's pace (walking animations and walls included),
                    // instead of letting our copy wander its own way and pulling it back.
                    if (!driven.Contains(a))
                    {
                        driven.Add(a);
                        spec.PreventMovement = true;
                    }
                    Vector2 v = c.Velocity + delta * FollowGain;
                    float max = Mathf.Max(c.Velocity.magnitude, a.MovementSpeed) * 1.5f + 2f;
                    if (v.magnitude > max)
                        v = v.normalized * max;
                    a.BehaviorOverridesVelocity = true;
                    a.BehaviorVelocity = dist < 0.05f && c.Velocity.sqrMagnitude < 0.01f ? Vector2.zero : v;
                    continue;
                }
                // Mid-attack (a charge, a leap...): the attack moves it; only gently pull a drift back.
                Release(a);
                if (dist < SettleDistance)
                    continue;
                Vector2 step = delta * Mathf.Min(1f, Time.deltaTime * PullSpeed);
                a.transform.position += new Vector3(step.x, step.y, 0f);
                a.specRigidbody.Reinitialize();
            }
            if (done != null)
            {
                foreach (AIActor a in done)
                {
                    corrections.Remove(a);
                    Release(a);
                }
            }
        }

        // A room the partner already cleared can end up sealed with nobody left in it. Open it after a moment.
        private static void UnsealIfStuck(float now)
        {
            PlayerController me = Me;
            RoomHandler room = me != null ? me.CurrentRoom : null;
            if (room == null || !room.IsSealed || room.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear))
            {
                sealedEmptySince = -1f;
                return;
            }
            if (sealedEmptySince < 0f)
            {
                sealedEmptySince = now;
                return;
            }
            if (now - sealedEmptySince > 2f)
            {
                Plugin.Log.LogInfo("Room is sealed with no enemies left (partner cleared it); unsealing.");
                room.UnsealRoom();
                sealedEmptySince = -1f;
            }
        }

        // ---- Room cleared ----
        // IDs can disagree when the two games spawn a room's enemies in a different order (an enemy then
        // never gets its "died" message and stays alive in one game). As a backstop: when a room opens
        // because it was cleared, the other game removes whatever is left in that room and opens it too.
        private static bool applyingRemoteRoom;
        // Each room's "cleared" is sent once per floor (a room that kept re-opening sent it dozens of times).
        private static readonly HashSet<int> clearedSent = new HashSet<int>();
        // Rooms another player cleared: they stay open and empty here. (Killing the leftovers could start the
        // room's next wave of reinforcements in our copy, which locked it again and kept us from following the
        // others to their next fight.)
        private static readonly HashSet<int> clearedByOthers = new HashSet<int>();
        private static float nextClearedCheckAt;

        // Host: a player who arrives on our floor later (rejoined, came back from the Breach) builds it fresh, with every
        // room full again. Tell them which rooms are already cleared.
        private static readonly Dungeon[] clearedListSentFor = new Dungeon[Protocol.MaxPlayers];

        private static void SendClearedRoomsToArrivals()
        {
            if (!session.Link.IsHost || GameManager.Instance.Dungeon == null)
                return;
            Dungeon d = GameManager.Instance.Dungeon;
            int level = LevelSync.CurrentLevelCode();
            for (int slot = 1; slot < clearedListSentFor.Length; slot++)
            {
                RemotePlayerState p = session.Players[slot];
                if (!p.Connected || !p.HasPlayer || p.Scene != LevelSync.CurrentLevelKey())
                {
                    clearedListSentFor[slot] = null;
                    continue;
                }
                if (clearedListSentFor[slot] == d)
                    continue;
                clearedListSentFor[slot] = d;
                List<int> rooms = new List<int>(clearedSent);
                foreach (int r in clearedByOthers)
                {
                    if (!rooms.Contains(r))
                        rooms.Add(r);
                }
                if (rooms.Count == 0)
                    continue;
                Plugin.Log.LogInfo(p.Name + " arrived on our floor: telling them the " + rooms.Count + " rooms already cleared.");
                foreach (int r in rooms)
                {
                    int roomIndex = r;
                    session.SendReliableTo(slot, MsgType.RoomCleared, delegate (BinaryWriter w)
                    {
                        w.Write(level);
                        w.Write(roomIndex);
                    });
                }
            }
        }

        private static void KeepClearedRoomOpen(float now)
        {
            if (clearedByOthers.Count == 0 || now < nextClearedCheckAt)
                return;
            nextClearedCheckAt = now + 0.5f;
            PlayerController me = Me;
            RoomHandler room = me != null ? me.CurrentRoom : null;
            if (room == null || !room.IsSealed || GameManager.Instance.Dungeon == null)
                return;
            int index = GameManager.Instance.Dungeon.data.rooms.IndexOf(room);
            if (index < 0 || !clearedByOthers.Contains(index))
                return;
            int killed = 0;
            List<AIActor> left = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
            if (left != null)
            {
                foreach (AIActor a in new List<AIActor>(left))
                {
                    if (a != null && a.healthHaver != null && !a.healthHaver.IsDead)
                    {
                        KillFromPartner(a);
                        killed++;
                    }
                }
            }
            Plugin.Log.LogInfo("Room " + room.GetRoomName() + " was cleared by another player but locked again here ("
                + killed + " enemies left); opening it.");
            applyingRemoteRoom = true;
            try { room.UnsealRoom(); }
            finally { applyingRemoteRoom = false; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomHandler), "UnsealRoom")]
        private static void AfterUnsealRoom(RoomHandler __instance)
        {
            if (applyingRemoteRoom || !Connected || LocalIsGhost || !GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return;
            if (__instance.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear))
                return;
            CheckFloor();
            int roomIndex = GameManager.Instance.Dungeon.data.rooms.IndexOf(__instance);
            // Every game in the room clears it at about the same time: only the one leading it says so.
            if (roomIndex < 0 || !(InCharge(__instance) && !LowerPlayerInRoom(__instance)) || !clearedSent.Add(roomIndex))
                return;
            int level = LevelSync.CurrentLevelCode();
            session.SendReliable(MsgType.RoomCleared, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(roomIndex);
            });
        }

        public static void OnRoomCleared(BinaryReader r)
        {
            int level = r.ReadInt32();
            int roomIndex = r.ReadInt32();
            if (level != LevelSync.CurrentLevelCode() || !GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return;
            List<RoomHandler> rooms = GameManager.Instance.Dungeon.data.rooms;
            if (roomIndex < 0 || roomIndex >= rooms.Count)
                return;
            RoomHandler room = rooms[roomIndex];
            CheckFloor();
            clearedByOthers.Add(roomIndex);

            // Every enemy we know in that room counts as dead from now on (also ones that haven't spawned yet).
            foreach (KeyValuePair<AIActor, int> pair in idOf)
            {
                if (pair.Key != null && pair.Key.ParentRoom == room)
                    deadIds.Add(pair.Value);
            }
            List<AIActor> left = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
            int killed = 0;
            if (left != null)
            {
                foreach (AIActor a in new List<AIActor>(left))
                {
                    if (a != null && a.healthHaver != null && !a.healthHaver.IsDead)
                    {
                        KillFromPartner(a);
                        killed++;
                    }
                }
            }
            if (killed > 0)
                Plugin.Log.LogInfo("Partner cleared room " + room.GetRoomName() + "; removed " + killed + " enemies left in our copy.");
            if (room.IsSealed)
            {
                applyingRemoteRoom = true;
                try { room.UnsealRoom(); }
                finally { applyingRemoteRoom = false; }
            }
        }

        // ---- Bringing ghosts along ----

        // When a room locks for a fight, bring the others into it: ghosts always, living players for the boss fight
        // and, with the host's "teleport to fights" rule on, for every fight.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomHandler), "SealRoom")]
        private static void AfterSealRoom(RoomHandler __instance)
        {
            PlayerController me = Me;
            if (!Connected || me == null || LocalIsGhost || GameManager.Instance.Dungeon == null)
                return;
            // The room may lock before the game updates which room we're in, so also check where we stand.
            Vector2 pos = me.CenterPosition;
            RoomHandler standing = GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(
                new IntVector2(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y)));
            if (me.CurrentRoom != __instance && standing != __instance)
                return;
            bool bossRoom = __instance.area != null && __instance.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
            if (!DeathSync.PartnerIsGhost && !bossRoom && !session.FightPull)
                return;
            Plugin.Log.LogInfo("Fight started in " + __instance.GetRoomName() + (bossRoom ? " (boss)" : "") + ": calling the others here.");
            int level = LevelSync.CurrentLevelCode();
            session.SendReliable(MsgType.PullGhost, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnPullGhost(BinaryReader r)
        {
            int level = r.ReadInt32();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            PlayerController me = Me;
            if (me == null || level != LevelSync.CurrentLevelCode())
                return;
            Plugin.Log.LogInfo(session.PeerName + " started a fight" + (session.FightPull ? "" : " (teleport to fights is off)") + ".");
            if (!me.IsGhost)
            {
                RoomHandler fightRoom = GameManager.Instance.Dungeon != null
                    ? GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(new IntVector2(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y)))
                    : null;
                bool boss = fightRoom != null && fightRoom.area != null && fightRoom.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
                // Living players go for the boss fight, or any fight with the host's "teleport to fights" rule.
                // Not when already there, dead, or in a boss room of our own.
                if (!boss && !session.FightPull)
                    return;
                string skip = me.healthHaver.IsDead ? "we're dead"
                    : fightRoom != null && me.CurrentRoom == fightRoom ? "we're already in that room"
                    : me.CurrentRoom != null && me.CurrentRoom.IsSealed && me.CurrentRoom.area != null
                        && me.CurrentRoom.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS ? "we're in a boss fight"
                    : null;
                if (skip != null)
                {
                    Plugin.Log.LogInfo("Not joining that fight: " + skip + ".");
                    return;
                }
                Toast.Show((boss ? "Boss fight! " : "Fight! ") + "Bringing you to " + session.PeerName + ".", 3f);
            }
            PlayerWarp.ScheduleGhostPull();
        }
    }
}
