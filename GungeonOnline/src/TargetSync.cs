using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Lets enemies go after the partner, not just the player in their own game.
    //
    // The game's AI aims, walks and fires its bullet patterns at AIActor.TargetRigidbody, which returns
    // AIActor.OverrideTarget when one is set (the game uses this itself, e.g. for charmed enemies). So the
    // partner gets an invisible physics body that follows them, and every enemy picks the closer of the two
    // players. Enemies that pick the partner get OverrideTarget = that body.
    //
    // Both games run this with the same positions, so they mostly agree: an enemy chasing the partner on
    // your screen is chasing them in their game too (where it simply targets their real player).
    // The body has no collision: bullets fly through the partner's picture on your screen; in the
    // partner's own game the same attack really hits or misses them.
    public static class TargetSync
    {
        private const float SwitchBias = 0.8f;      // switch only when the other player is 20% closer
        private const float Interval = 0.25f;       // re-pick targets 4x a second
        private const int Me = -1;
        private const int Bot = -2;                 // the host's stand-in bot (a second player in the host's game)
        private const float ForceFor = 1f;          // how long a target the leading game sent holds

        private static NetSession session;
        private static readonly GameObject[] targetGos = new GameObject[Protocol.MaxPlayers];
        private static readonly SpeculativeRigidbody[] targets = new SpeculativeRigidbody[Protocol.MaxPlayers];
        private static readonly bool[] active = new bool[Protocol.MaxPlayers];
        private static readonly Vector2[] centers = new Vector2[Protocol.MaxPlayers];
        // Which player each enemy is after: a slot, or Me.
        private static readonly Dictionary<AIActor, int> chasing = new Dictionary<AIActor, int>();
        private static float nextPickAt;
        // Enemies whose target the game leading their room sent: the slot, and when.
        private static readonly Dictionary<AIActor, KeyValuePair<int, float>> forced = new Dictionary<AIActor, KeyValuePair<int, float>>();

        // Which player (slot) an enemy is after in our game, or Protocol.NoSlot.
        public static int SlotOfTarget(AIActor enemy)
        {
            SpeculativeRigidbody t = enemy != null ? enemy.TargetRigidbody : null;
            if (t == null || session == null)
                return Protocol.NoSlot;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me != null && t == me.specRigidbody)
                return session.LocalSlot;
            PlayerController bot = StandIn.Active ? StandIn.Player : null;
            if (bot != null && t == bot.specRigidbody)
                return StandIn.Slot;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == t)
                    return i;
            }
            return Protocol.NoSlot;
        }

        // The leading game says this enemy is after that player: go after the same one here.
        public static void Force(AIActor enemy, int slot)
        {
            if (enemy == null || slot < 0 || slot >= Protocol.MaxPlayers)
                return;
            forced[enemy] = new KeyValuePair<int, float>(slot, Time.realtimeSinceStartup);
            int want;
            if (Resolve(slot, out want))
            {
                chasing[enemy] = want;
                ReapplyOverrides();
            }
        }

        // A slot as a chase target in our game: us, the stand-in, or another player's invisible body.
        private static bool Resolve(int slot, out int want)
        {
            want = Me;
            if (session == null)
                return false;
            if (slot == session.LocalSlot)
                return true;
            if (StandIn.Active && slot == StandIn.Slot && StandIn.Player != null)
            {
                want = Bot;
                return true;
            }
            want = slot;
            return active[slot];
        }

        // A player's invisible body (where they stand), when it's set up. Also used to replay their kicks.
        public static SpeculativeRigidbody BodyOf(int slot)
        {
            if (slot < 0 || slot >= targets.Length)
                return null;
            SpeculativeRigidbody t = targets[slot];
            return t != null && targetGos[slot] != null && targetGos[slot].activeInHierarchy && t.enabled ? t : null;
        }

        // The body of the player whose message is being handled.
        public static SpeculativeRigidbody PartnerBody
        {
            get { return BodyOf(session != null ? session.From : -1); }
        }

        private static bool IsOurBody(SpeculativeRigidbody body)
        {
            if (body == null)
                return false;
            if (StandIn.Active && StandIn.Player != null && body == StandIn.Player.specRigidbody)
                return true;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == body)
                    return true;
            }
            return false;
        }

        public static void Init(NetSession netSession, HarmonyLib.Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(TargetSync));
        }

        public static void Update()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            // The physics body can only be set up once the floor has finished loading: right after a level load
            // the game's physics system doesn't exist yet, the setup crashed, and the body stayed broken for the
            // whole floor (enemies then followed the partner but aimed at us and never fired).
            bool physicsReady = PhysicsEngine.HasInstance && GameManager.HasInstance && !GameManager.Instance.IsLoadingLevel;
            bool online = physicsReady && session != null && session.Status == NetSession.State.Connected && me != null;
            string level = online ? LevelSync.CurrentLevelKey() : "";
            bool anyone = false;
            for (int i = 0; i < targets.Length; i++)
            {
                RemotePlayerState remote = session != null ? session.Players[i] : null;
                active[i] = online && i != session.LocalSlot && remote.Connected && remote.HasPlayer && !remote.IsDead && remote.Scene == level
                    && PlaceBody(i, remote);
                if (!active[i] && targetGos[i] != null && targetGos[i].activeSelf)
                    targetGos[i].SetActive(false);
                anyone |= active[i];
            }
            if (!anyone)
            {
                ReleaseAll();
                return;
            }

            if (Time.realtimeSinceStartup < nextPickAt)
            {
                ReapplyOverrides();
                return;
            }
            nextPickAt = Time.realtimeSinceStartup + Interval;

            bool meTargetable = !me.IsGhost && !me.healthHaver.IsDead;
            Vector2 myCenter = me.CenterPosition;
            RoomHandlerList(me);
            foreach (AIActor enemy in enemies)
            {
                if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead || !enemy.CanTargetPlayers
                    || enemy.CompanionOwner != null)
                    continue;
                KeyValuePair<int, float> sent;
                int sentWant;
                if (forced.TryGetValue(enemy, out sent) && Time.realtimeSinceStartup - sent.Value < ForceFor
                    && Resolve(sent.Key, out sentWant))
                {
                    chasing[enemy] = sentWant;
                    continue;
                }
                Vector2 pos = enemy.CenterPosition;
                int current;
                if (!chasing.TryGetValue(enemy, out current) || current == Bot || (current != Me && !active[current]))
                    current = Me;
                float currentDist = current == Me ? (meTargetable ? Vector2.Distance(pos, myCenter) : float.MaxValue)
                    : Vector2.Distance(pos, centers[current]);

                // The closest player, but only switch when they're clearly closer than the one being chased.
                int best = current;
                float bestDist = currentDist;
                if (meTargetable && current != Me)
                {
                    float d = Vector2.Distance(pos, myCenter);
                    if (d < bestDist * SwitchBias)
                    {
                        best = Me;
                        bestDist = d;
                    }
                }
                for (int i = 0; i < targets.Length; i++)
                {
                    if (!active[i] || i == current)
                        continue;
                    float d = Vector2.Distance(pos, centers[i]);
                    if (d < bestDist * SwitchBias || (bestDist == float.MaxValue && d < bestDist))
                    {
                        best = i;
                        bestDist = d;
                    }
                }
                chasing[enemy] = best;
            }
            ReapplyOverrides();
        }

        // Moves (and sets up) the invisible body for one player. False if it couldn't be set up this frame.
        private static bool PlaceBody(int slot, RemotePlayerState remote)
        {
            // Follow the player's (smoothed) position. Center = smoothed feet + their center-to-feet offset.
            Vector2 feet = remote.Motion.HasData ? remote.Motion.Sample() : remote.FeetPosition;
            Vector2 center = feet + (remote.Position - remote.FeetPosition);
            centers[slot] = center;
            if (!EnsureTarget(slot))
                return false;
            GameObject go = targetGos[slot];
            if (!go.activeSelf)
                go.SetActive(true);
            go.transform.position = new Vector3(center.x, center.y, go.transform.position.z);
            try
            {
                if (!targets[slot].enabled)
                    targets[slot].enabled = true; // created disabled; switched on now that physics exists
                targets[slot].Reinitialize();
                return true;
            }
            catch (System.Exception e)
            {
                // Throw it away and build a fresh one next frame.
                Plugin.Log.LogWarning("Target body for " + remote.Name + " failed to set up (" + e.GetType().Name + "); retrying.");
                ReleaseAll();
                Object.Destroy(go);
                targetGos[slot] = null;
                targets[slot] = null;
                return false;
            }
        }

        // Some behaviours clear OverrideTarget; set it again every frame for enemies chasing another player.
        private static void ReapplyOverrides()
        {
            List<AIActor> gone = null;
            foreach (KeyValuePair<AIActor, int> pair in chasing)
            {
                AIActor enemy = pair.Key;
                if (enemy == null || enemy.healthHaver == null || enemy.healthHaver.IsDead)
                {
                    if (gone == null) gone = new List<AIActor>();
                    gone.Add(enemy);
                    continue;
                }
                SpeculativeRigidbody want = pair.Value == Bot ? (StandIn.Player != null ? StandIn.Player.specRigidbody : null)
                    : pair.Value != Me && active[pair.Value] ? targets[pair.Value] : null;
                if (want != null)
                {
                    if (enemy.OverrideTarget != want)
                        enemy.OverrideTarget = want;
                }
                else if (IsOurBody(enemy.OverrideTarget))
                {
                    enemy.OverrideTarget = null;
                }
            }
            if (gone != null)
            {
                foreach (AIActor enemy in gone)
                {
                    chasing.Remove(enemy);
                    forced.Remove(enemy);
                }
            }
        }

        private static void ReleaseAll()
        {
            foreach (KeyValuePair<AIActor, int> pair in chasing)
            {
                if (pair.Key != null && IsOurBody(pair.Key.OverrideTarget))
                    pair.Key.OverrideTarget = null;
            }
            chasing.Clear();
            forced.Clear();
        }

        private static readonly List<AIActor> enemies = new List<AIActor>();

        // Enemies in the room we're in (the only ones that are fighting).
        private static void RoomHandlerList(PlayerController me)
        {
            enemies.Clear();
            if (me.CurrentRoom != null)
                me.CurrentRoom.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.All, ref enemiesOut);
            if (enemiesOut != null)
                enemies.AddRange(enemiesOut);
        }
        private static List<AIActor> enemiesOut = new List<AIActor>();

        // ---- Rockets without a target ----
        // The Gatling Gull's rockets take their target when fired. In a game whose own player is a ghost the boss may
        // have none: the rocket then failed every frame and never came down. Give it the nearest living player (ours
        // or another player's body), or remove it.
        [HarmonyLib.HarmonyPrefix]
        [HarmonyLib.HarmonyPatch(typeof(SkyRocket), "Update")]
        private static bool BeforeSkyRocket(SkyRocket __instance)
        {
            if (__instance.Target != null)
                return true;
            Vector2 at = __instance.transform.position;
            SpeculativeRigidbody best = null;
            float bestDist = float.MaxValue;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me != null && !me.IsGhost && !me.healthHaver.IsDead && me.specRigidbody != null)
            {
                best = me.specRigidbody;
                bestDist = Vector2.Distance(at, me.CenterPosition);
            }
            for (int i = 0; i < targets.Length; i++)
            {
                SpeculativeRigidbody b = BodyOf(i);
                if (b == null || !active[i])
                    continue;
                float d = Vector2.Distance(at, centers[i]);
                if (d < bestDist)
                {
                    best = b;
                    bestDist = d;
                }
            }
            if (best != null)
            {
                __instance.Target = best;
                return true;
            }
            Object.Destroy(__instance.gameObject);
            return false;
        }

        // ---- Line of sight ----
        // Enemies only fire when they "see" their target: AIActor casts a ray that must HIT the target's body.
        // Ours has no collisions, so the ray never hit it and enemies aimed but never fired. For our body we
        // answer the question ourselves: visible when no wall cell lies between the enemy and the partner.

        [HarmonyLib.HarmonyPostfix]
        [HarmonyLib.HarmonyPatch(typeof(AIActor), "HasLineOfSightToRigidbody")]
        private static void AfterHasLineOfSightToRigidbody(AIActor __instance, SpeculativeRigidbody __0, ref bool __result)
        {
            if (__instance.specRigidbody != null && IsOurBody(__0))
                __result = !WallBetween(__instance.specRigidbody.UnitCenter, __0.UnitCenter);
        }

        [HarmonyLib.HarmonyPostfix]
        [HarmonyLib.HarmonyPatch(typeof(AIActor), "HasLineOfSightToTargetFromPosition")]
        private static void AfterHasLineOfSightFromPosition(AIActor __instance, Vector2 __0, ref bool __result)
        {
            SpeculativeRigidbody t = __instance.TargetRigidbody;
            if (IsOurBody(t))
                __result = !WallBetween(__0, t.UnitCenter);
        }

        // Walk the line between two points in quarter-tile steps and look for wall cells.
        private static bool WallBetween(Vector2 from, Vector2 to)
        {
            Dungeonator.Dungeon dungeon = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (dungeon == null || dungeon.data == null)
                return false;
            float length = Vector2.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length * 4f));
            for (int i = 1; i < steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (!dungeon.data.CheckInBoundsAndValid(x, y) || dungeon.data.isWall(x, y))
                    return true;
            }
            return false;
        }

        // The invisible body enemies can aim at: one player-sized hitbox, no collisions.
        private static bool EnsureTarget(int slot)
        {
            if (targets[slot] != null && targetGos[slot] != null)
                return true;
            GameObject targetGo = new GameObject("GungeonOnline Partner Target " + (slot + 1));
            SpeculativeRigidbody target = targetGo.AddComponent<SpeculativeRigidbody>();
            targetGos[slot] = targetGo;
            targets[slot] = target;
            target.CollideWithOthers = false;
            target.CollideWithTileMap = false;
            target.CanBePushed = false;
            target.PixelColliders = new List<PixelCollider>();
            PixelCollider box = new PixelCollider();
            box.ColliderGenerationMode = PixelCollider.PixelColliderGeneration.Manual;
            box.CollisionLayer = CollisionLayer.PlayerHitBox;
            box.ManualOffsetX = -6;   // 12 x 16 pixels around the center (16 pixels = 1 tile)
            box.ManualOffsetY = -8;
            box.ManualWidth = 12;
            box.ManualHeight = 16;
            target.PixelColliders.Add(box);
            // Disabled until Update has checked the physics system is ready (it then enables + initializes it).
            target.enabled = false;
            Plugin.Log.LogInfo("Created player " + (slot + 1) + "'s target body for enemies.");
            return true;
        }
    }
}
