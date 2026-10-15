using System.Collections.Generic;
using System.IO;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Enemy sync, step 1 ("shared health"):
    // Both games run their own copy of every enemy (same seed = same rooms and enemies). Each enemy gets
    // the same ID in both games: its room's index in the dungeon plus the order it joined that room.
    // - Damage dealt to an enemy in one game is sent to the other, so health matches (bosses too).
    // - An enemy that dies in one game dies in the other; enemies already killed by the partner die as
    //   soon as they appear when you enter that room later.
    // - Whoever is alive in a room sends its enemies' positions and health ~10x a second; the other game
    //   glides drifted enemies back and never lets an enemy have more health than the sender's copy.
    // Each game's enemies still choose their own target and attacks (aimed at the player in that game).
    public static class EnemySync
    {
        private const float StateInterval = 0.1f;
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
            clearedSent.Clear();
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

        private static AIActor Find(int id, string guid)
        {
            AIActor a;
            if (!byId.TryGetValue(id, out a) || a == null || a.healthHaver == null || a.healthHaver.IsDead)
                return null;
            // Same ID but a different kind of enemy means the two games spawned differently; ignore it.
            return a.EnemyGuid == guid ? a : null;
        }

        // ---- Damage ----

        private static float healthBefore;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealthHaver), "ApplyDamage")]
        private static bool BeforeApplyDamage(HealthHaver __instance)
        {
            if (__instance.aiActor != null && Connected && !applyingRemote)
            {
                if (RemoteBulletHitting)
                    return false; // the partner's game deals this damage and tells us
                // While we're a ghost, only the living partner's game decides what enemies take damage or die
                // (a ghost's pulse may knock enemies around, but must never kill them).
                if (LocalIsGhost)
                    return false;
            }
            healthBefore = __instance.GetCurrentHealth();
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
        private static void AfterApplyDamage(HealthHaver __instance)
        {
            if (applyingRemote || !Connected || __instance.aiActor == null)
                return;
            int id;
            if (!idOf.TryGetValue(__instance.aiActor, out id))
                return;
            float dealt = healthBefore - __instance.GetCurrentHealth();
            string level = LevelSync.CurrentLevelKey();
            string guid = __instance.aiActor.EnemyGuid;
            if (__instance.IsDead)
            {
                deadIds.Add(id);
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
            string level = r.ReadString();
            int id = r.ReadInt32();
            string guid = r.ReadString();
            float amount = r.ReadSingle();
            if (level != LevelSync.CurrentLevelKey())
                return;
            CheckFloor();
            AIActor a = Find(id, guid);
            if (a != null)
                ApplyRemoteDamage(a, amount);
        }

        public static void OnEnemyDied(BinaryReader r)
        {
            string level = r.ReadString();
            int id = r.ReadInt32();
            string guid = r.ReadString();
            if (level != LevelSync.CurrentLevelKey())
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
            public float ReceivedAt;
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
            return me != null && !LocalIsGhost && room != null && me.CurrentRoom == room;
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

            if (now < nextStateAt)
                return;
            nextStateAt = now + StateInterval;

            List<int> ids = new List<int>();
            List<AIActor> actors = new List<AIActor>();
            foreach (KeyValuePair<int, AIActor> pair in byId)
            {
                AIActor a = pair.Value;
                if (a != null && a.healthHaver != null && !a.healthHaver.IsDead && a.gameObject.activeInHierarchy
                    && InCharge(a.ParentRoom))
                {
                    ids.Add(pair.Key);
                    actors.Add(a);
                }
            }
            if (ids.Count == 0)
                return;
            string level = LevelSync.CurrentLevelKey();
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
                }
            });
        }

        public static void OnEnemyStates(BinaryReader r)
        {
            string level = r.ReadString();
            int count = r.ReadUInt16();
            if (level != LevelSync.CurrentLevelKey())
                return;
            CheckFloor();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                Vector2 theirPos = new Vector2(r.ReadSingle(), r.ReadSingle());
                float theirHealth = r.ReadSingle();

                AIActor a;
                if (!byId.TryGetValue(id, out a) || a == null || a.healthHaver == null || a.healthHaver.IsDead)
                    continue;
                // If we're alive in that room too, the host's copy wins; otherwise the sender is in charge.
                if (InCharge(a.ParentRoom) && session.Link.IsHost)
                    continue;

                // Health only ever goes down to theirs (our own hits may not have reached them yet).
                if (theirHealth < a.healthHaver.GetCurrentHealth())
                    a.healthHaver.ForceSetCurrentHealth(theirHealth);

                Correction c;
                if (!corrections.TryGetValue(a, out c))
                {
                    c = new Correction();
                    corrections[a] = c;
                }
                c.Target = theirPos;
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
                Vector2 delta = pair.Value.Target - a.specRigidbody.UnitCenter;
                float dist = delta.magnitude;
                if (dist < SettleDistance)
                    continue;
                Vector2 step = dist > SnapDistance ? delta : delta * Mathf.Min(1f, Time.deltaTime * PullSpeed);
                a.transform.position += new Vector3(step.x, step.y, 0f);
                a.specRigidbody.Reinitialize();
            }
            if (done != null)
            {
                foreach (AIActor a in done)
                    corrections.Remove(a);
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
            if (roomIndex < 0 || !clearedSent.Add(roomIndex))
                return;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.RoomCleared, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write(roomIndex);
            });
        }

        public static void OnRoomCleared(BinaryReader r)
        {
            string level = r.ReadString();
            int roomIndex = r.ReadInt32();
            if (level != LevelSync.CurrentLevelKey() || !GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return;
            List<RoomHandler> rooms = GameManager.Instance.Dungeon.data.rooms;
            if (roomIndex < 0 || roomIndex >= rooms.Count)
                return;
            RoomHandler room = rooms[roomIndex];
            CheckFloor();

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

        // When a room locks for a fight and our partner is a ghost, bring them into the room.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomHandler), "SealRoom")]
        private static void AfterSealRoom(RoomHandler __instance)
        {
            PlayerController me = Me;
            if (!Connected || me == null || LocalIsGhost || me.CurrentRoom != __instance)
                return;
            bool bossRoom = __instance.area != null && __instance.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
            // A ghost partner always comes along; a living one too when it's the boss fight.
            if (!DeathSync.PartnerIsGhost && !bossRoom)
                return;
            Vector2 pos = me.CenterPosition;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.PullGhost, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnPullGhost(BinaryReader r)
        {
            string level = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            PlayerController me = Me;
            if (me == null || level != LevelSync.CurrentLevelKey())
                return;
            if (!me.IsGhost)
            {
                // A boss fight started where the partner is: everyone goes.
                if (me.healthHaver.IsDead || (me.CurrentRoom != null && me.CurrentRoom.area != null
                    && me.CurrentRoom.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS))
                    return;
                Toast.Show("Boss fight! Bringing you to your partner.", 3f);
            }
            PlayerWarp.ScheduleGhostPull();
        }
    }
}
