using System.Collections.Generic;
using System.Reflection;
using Dungeonator;
using HarmonyLib;
using InControl;
using Pathfinding;
using UnityEngine;

namespace GungeonOnline
{
    // The bot that plays our own character: "Take a Break".
    //
    // It drives the player the same way a controller does: every input tick the game asks the player's control set
    // (GungeonActions) what is pressed, and while the autopilot is on we answer instead of the keyboard/pad. Aiming
    // uses the game's own "aim here" point (PlayerController.forceAimPoint). Everything else - online sync, damage,
    // deaths - works exactly as if we were playing, because as far as the game is concerned, we are.
    //
    // What it does: fights the closest visible enemy (keeps some distance, strafes), sidesteps or rolls away from
    // bullets about to hit, reloads, follows the closest teammate when there's nothing to fight, and keeps out of
    // pits and walls. When every player is a bot (test copies), it also opens chests (locked ones if it has keys) and
    // picks up guns; with a real person playing, it leaves the loot to them.
    //
    // It switches off as soon as the player moves (any key, mouse button or controller input).
    public class Brain
    {
        private const float GraceAfterStart = 1f;     // input right after switching on (the click/key itself) doesn't count
        private const float RepathInterval = 0.5f;
        private const float DodgeCooldown = 1.2f;

        private static NetSession session;
        private bool active;
        private bool forced;           // test copies: stays on whatever input arrives
        private float graceUntil;

        // What we "press" this tick.
        private Vector2 move;
        private bool shoot, dodge, reload, interact, blank;
        private float nextBlankAt;

        // Path following.
        private static readonly List<Vector2> waypoints = new List<Vector2>();
        private IntVector2 pathGoal = new IntVector2(-1, -1);
        private float nextPathAt;
        // Fighting.
        private float strafeSign = 1f, nextStrafeFlipAt, nextDodgeAt;
        private static readonly List<AIActor> enemies = new List<AIActor>();
        private List<AIActor> enemiesOut = new List<AIActor>();

        public bool Active
        {
            get { return active; }
        }

        // 0 = our own player (Take a Break), 1 = the co-op second player (a stand-in for someone who dropped).
        public readonly int PlayerIndex;
        public readonly string Label;

        public Brain(int playerIndex, string label)
        {
            PlayerIndex = playerIndex;
            Label = label;
        }

        public PlayerController Player
        {
            get
            {
                if (!GameManager.HasInstance)
                    return null;
                return PlayerIndex == 0 ? GameManager.Instance.PrimaryPlayer : GameManager.Instance.SecondaryPlayer;
            }
        }

        public static void SetSession(NetSession netSession)
        {
            session = netSession;
        }

        // Test copies: when there's nothing to fight and no one to follow, go to the nearest room nobody has
        // visited yet (so a bot-only test run actually plays the floor).
        public bool Explore;
        private RoomHandler exploreRoom;
        private static readonly HashSet<RoomHandler> explored = new HashSet<RoomHandler>();
        private RoomHandler progressRoom;
        private Dungeon exploredFloor;
        private float bestDistance, progressAt;

        public void Start(bool keepOn)
        {
            if (!active && PlayerIndex == 0)
            {
                Plugin.Log.LogInfo("Take a Break: the bot plays for us" + (keepOn ? " (test copy: stays on)." : "; moving takes control back."));
                if (!keepOn)
                    Toast.Show("Taking a break: the bot plays for you. Move to take control back.", 5f);
            }
            active = true;
            forced |= keepOn;
            graceUntil = Time.realtimeSinceStartup + GraceAfterStart;
            waypoints.Clear();
            pathGoal = new IntVector2(-1, -1);
        }

        public void Stop(string why)
        {
            if (!active)
                return;
            active = false;
            forced = false;
            move = Vector2.zero;
            shoot = dodge = reload = blank = false;
            PlayerController me = Player;
            if (me != null)
                me.forceAimPoint = null;
            if (PlayerIndex == 0)
            {
                Plugin.Log.LogInfo("Take a Break off (" + why + ").");
                Toast.Show("You're back in control.", 3f);
            }
        }

        public void Toggle()
        {
            if (active)
                Stop("switched off");
            else
                Start(false);
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (!active)
                return;
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            PlayerController me = Player;
            // Menus open (pause, ours): clicking around there isn't "taking control back".
            if (gm == null || me == null || gm.IsLoadingLevel || gm.IsPaused || (PlayerIndex == 0 && MultiplayerMenu.IsOpen)
                || me.specRigidbody == null || me.healthHaver == null || me.CurrentRoom == null)
            {
                graceUntil = now + 0.75f;
                move = Vector2.zero;
                shoot = dodge = reload = blank = false;
                return;
            }
            if (PlayerIndex == 0 && !forced && now > graceUntil && PlayerTouchedControls())
            {
                Stop("the player moved");
                return;
            }
            try
            {
                Think(me);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autopilot error: " + e.Message);
                move = Vector2.zero;
                shoot = false;
            }
        }

        // Real input from the person: a key or mouse button, or the controller's sticks/buttons.
        private bool PlayerTouchedControls()
        {
            if (Input.anyKeyDown)
                return true;
            InputDevice device = InputManager.ActiveDevice;
            if (device != null)
            {
                if (device.AnyButtonWasPressed || device.LeftStick.Vector.magnitude > 0.4f || device.DPad.Vector.magnitude > 0.4f)
                    return true;
            }
            return false;
        }

        // ---- Thinking: what to press ----

        private void Think(PlayerController me)
        {
            move = Vector2.zero;
            shoot = dodge = reload = blank = false;
            float now = Time.realtimeSinceStartup;
            Vector2 pos = me.CenterPosition;
            PickGun(me, now);

            AIActor target = me.IsGhost ? null : ClosestEnemy(me, pos);
            Vector2 threatDir = Vector2.zero;
            float timeToHit = float.MaxValue;
            int closeCalls = 0;
            bool threatened = !me.IsGhost && IncomingBullet(pos, out threatDir, out timeToHit, out closeCalls);
            // Too many bullets to dodge (or a few at our last heart): use a blank, like a person would.
            if (threatened && me.Blanks > 0 && now >= nextBlankAt && me.healthHaver != null
                && (closeCalls >= 5 || (closeCalls >= 2 && me.healthHaver.GetCurrentHealth() + me.healthHaver.Armor <= 1f)))
            {
                blank = true;
                nextBlankAt = now + 4f;
                Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": " + closeCalls + " bullets coming; using a blank.");
            }

            // Aim: at the enemy (with a little lead), else where we're going.
            if (target != null)
            {
                Vector2 tc = target.CenterPosition;
                Vector2 lead = target.specRigidbody != null ? target.specRigidbody.Velocity * 0.15f : Vector2.zero;
                me.forceAimPoint = tc + lead;
                shoot = !WallBetween(pos, tc);
                Gun gun = me.CurrentGun;
                if (gun != null && !gun.IsReloading && gun.ClipShotsRemaining == 0 && gun.ammo > 0)
                    reload = true;
            }

            if (threatened)
            {
                // Step sideways out of the bullet's line; roll when it's about to hit.
                move = threatDir;
                if (timeToHit < 0.3f && now >= nextDodgeAt && !me.IsDodgeRolling)
                {
                    dodge = true;
                    nextDodgeAt = now + DodgeCooldown;
                }
            }
            else if (target != null)
            {
                Vector2 toEnemy = target.CenterPosition - pos;
                float d = toEnemy.magnitude;
                Vector2 dir = d > 0.01f ? toEnemy / d : Vector2.right;
                if (now >= nextStrafeFlipAt)
                {
                    strafeSign = -strafeSign;
                    nextStrafeFlipAt = now + Random.Range(1.2f, 2.5f);
                }
                Vector2 strafe = new Vector2(-dir.y, dir.x) * strafeSign;
                if (d < 3.5f)
                    move = (-dir + strafe * 0.5f).normalized;          // too close: back off
                else if (d > 8f || WallBetween(pos, target.CenterPosition))
                    move = PathToward(me, target.CenterPosition);       // too far or out of sight: go there
                else
                    move = strafe;                                      // good distance: keep moving
            }
            else if (!me.IsGhost && EveryoneIsABot() && LootStep(me, now))
            {
                // walking to / opening a chest or a gun (LootStep set move and interact)
            }
            else
            {
                // Nothing to fight: stay with the closest teammate (a ghost follows the living). An exploring test bot
                // leads the way to the next unvisited room instead.
                Vector2 lp = Vector2.zero;
                bool leading = Explore && !me.IsGhost && !SomeoneElseLeads(me);
                bool hasLeader = !leading && ClosestTeammate(pos, out lp);
                if (hasLeader)
                {
                    if (Vector2.Distance(pos, lp) > 3f)
                        move = PathToward(me, lp);
                }
                else if (leading && ExitPoint(out exitAt))
                {
                    // The floor's boss is down: on to the next floor (walking onto the elevator's floor takes it).
                    move = PathToward(me, exitAt);
                }
                else if (leading)
                {
                    RoomHandler next = NextRoomToExplore(me);
                    if (next != null)
                    {
                        IntVector2 c = next.GetCenterCell();
                        Vector2 goal = new Vector2(c.x + 0.5f, c.y + 0.5f);
                        // No progress toward it for a while (center behind a counter, locked door...): try another room.
                        float d = Vector2.Distance(pos, goal);
                        if (next != progressRoom || d < bestDistance - 1f)
                        {
                            progressRoom = next;
                            bestDistance = d;
                            progressAt = now;
                        }
                        else if (now - progressAt > 6f)
                        {
                            Plugin.Log.LogInfo("Bot: can't get to " + next.GetRoomName() + "; trying another room.");
                            explored.Add(next);
                            exploreRoom = null;
                            progressRoom = null;
                        }
                        move = PathToward(me, goal);
                    }
                    else
                    {
                        // Nothing left to explore and the boss isn't beaten: go to the boss. Rooms we gave up on get
                        // another try now and then (a door may have opened since).
                        RoomHandler boss = BossRoom();
                        if (boss != null && me.CurrentRoom != boss)
                        {
                            IntVector2 c = boss.GetCenterCell();
                            move = PathToward(me, new Vector2(c.x + 0.5f, c.y + 0.5f));
                        }
                        if (now - lastRetryAt > 30f)
                        {
                            lastRetryAt = now;
                            explored.RemoveWhere(delegate (RoomHandler r) { return r != null && !r.hasEverBeenVisited; });
                        }
                    }
                }
                if (move.sqrMagnitude > 0.01f)
                    me.forceAimPoint = pos + move * 4f;
            }

            // Walking into a table (or a crate...) that blocks the way, with nothing to fight: shoot it to pieces.
            if (target == null && !me.IsGhost && blocker != null && now - blockedAt < 0.6f)
            {
                BraveBehaviour b = blocker as BraveBehaviour;
                Vector2 at = b != null && b.specRigidbody != null ? b.specRigidbody.UnitCenter : (Vector2)blocker.transform.position;
                me.forceAimPoint = at;
                shoot = true;
                if (now >= nextBlockerLogAt)
                {
                    nextBlockerLogAt = now + 5f;
                    Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": " + blocker.name + " is in the way; shooting it.");
                }
            }

            Vector2 wanted = move;
            Vector2 feet = Feet(me);
            Vector2 jumpDir;
            if (me.IsDodgeRolling && now < pitRollUntil)
            {
                // Mid-roll over a gap: hold the direction (steering away from the pit now would drop us in).
                move = pitRollDir;
            }
            else if (!threatened && target == null && !me.IsGhost && blocker is FlippableCover && now - blockedAt < 0.3f
                && now >= nextDodgeAt && !me.IsDodgeRolling && move.sqrMagnitude > 0.01f)
            {
                // A table in the way: roll over it (a roll clears tables), and shoot it if that doesn't work.
                move = move.normalized;
                dodge = true;
                nextDodgeAt = now + DodgeCooldown;
                pitRollDir = move;
                pitRollUntil = now + 0.7f;
                Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": rolling over " + blocker.name + ".");
            }
            else if (!threatened && PitJump(me, feet, now, out jumpDir))
            {
                move = jumpDir;
                dodge = true;
                nextDodgeAt = now + DodgeCooldown;
                pitRollDir = jumpDir;
                pitRollUntil = now + 1.2f;
                Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": rolling over a gap.");
            }
            else
            {
                move = KeepSafe(me, feet, move);
                move = Unstick(feet, move, now);
            }
            // Ease into new directions instead of snapping (snapping made it overcorrect around doors).
            if (move.sqrMagnitude > 0.01f && smoothMove.sqrMagnitude > 0.01f && Vector2.Dot(move, smoothMove) > 0f)
                move = Vector2.Lerp(smoothMove, move, Mathf.Clamp01(BraveTime.DeltaTime * 14f)).normalized;
            smoothMove = move;
            if (forced && now >= nextStatusAt)
            {
                nextStatusAt = now + 3f;
                Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": at " + pos.ToString("F1") + " want " + wanted.ToString("F2") + " move " + move.ToString("F2")
                    + (target != null ? ", fighting " + target.name : "") + (threatened ? ", dodging" : "")
                    + ", path " + waypoints.Count + (exploreRoom != null ? ", exploring " + exploreRoom.GetRoomName() : "")
                    + ", inputs seen " + inputTicks);
            }
        }

        private float nextStatusAt;
        private Component blocker;
        private float blockedAt, nextBlockerLogAt;
        private Vector2 smoothMove;
        private float nextPathLogAt;

        private string CellKind(DungeonData data, IntVector2 c)
        {
            if (data == null || !data.CheckInBoundsAndValid(c))
                return "invalid";
            CellData cell = data[c];
            return cell == null ? "null" : cell.type + (cell.isOccupied ? " occupied" : "");
        }
        private int inputTicks;

        private AIActor ClosestEnemy(PlayerController me, Vector2 pos)
        {
            enemies.Clear();
            RoomHandler room = me.CurrentRoom;
            if (room != null)
                room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All, ref enemiesOut);
            if (enemiesOut != null && room != null)
                enemies.AddRange(enemiesOut);
            AIActor best = null;
            float bestScore = float.MaxValue;
            foreach (AIActor a in enemies)
            {
                if (a == null || a.healthHaver == null || a.healthHaver.IsDead || !a.IsNormalEnemy || a.CompanionOwner != null
                    || a.IsGone || !a.gameObject.activeInHierarchy)
                    continue;
                float d = Vector2.Distance(pos, a.CenterPosition);
                if (WallBetween(pos, a.CenterPosition))
                    d += 20f; // prefer ones we can see
                if (d < bestScore)
                {
                    best = a;
                    bestScore = d;
                }
            }
            return best;
        }

        // A bullet heading for us: which way to step (sideways from its path, away from it) and how soon it hits.
        private bool IncomingBullet(Vector2 pos, out Vector2 stepDir, out float timeToHit, out int closeCalls)
        {
            stepDir = Vector2.zero;
            timeToHit = float.MaxValue;
            closeCalls = 0;   // bullets that will hit within about half a second
            bool found = false;
            foreach (Projectile p in StaticReferenceManager.AllProjectiles)
            {
                if (p == null || !p.isActiveAndEnabled || !p.collidesWithPlayer || p.Owner is PlayerController)
                    continue;
                Vector2 pp = p.specRigidbody != null ? p.specRigidbody.UnitCenter : (Vector2)p.transform.position;
                Vector2 rel = pos - pp;
                float dist = rel.magnitude;
                if (dist > 5f)
                    continue;
                Vector2 vel = p.LastVelocity;
                float speed = vel.magnitude;
                if (speed < 0.5f)
                    continue;
                Vector2 dir = vel / speed;
                float along = Vector2.Dot(rel, dir);             // how far ahead of the bullet we are
                if (along <= 0f)
                    continue;                                     // moving away from us
                float miss = Mathf.Abs(rel.x * dir.y - rel.y * dir.x); // how far its line passes from us
                if (miss > 1.1f)
                    continue;
                float t = along / speed;
                if (t < 0.5f)
                    closeCalls++;
                if (t < timeToHit)
                {
                    timeToHit = t;
                    Vector2 side = new Vector2(-dir.y, dir.x);
                    // Step to the side we're already on (shorter way out of its line).
                    if (Vector2.Dot(rel, side) < 0f)
                        side = -side;
                    stepDir = side;
                    found = true;
                }
            }
            return found;
        }

        private RoomHandler NextRoomToExplore(PlayerController me)
        {
            if (GameManager.Instance.Dungeon != exploredFloor)
            {
                exploredFloor = GameManager.Instance.Dungeon;
                explored.Clear();
                lootGivenUp.Clear();
                exploreRoom = null;
            }
            // The room we're in counts as seen (the start room is never marked visited by the game).
            if (me.CurrentRoom != null)
                explored.Add(me.CurrentRoom);
            if (exploreRoom != null && !exploreRoom.hasEverBeenVisited && !explored.Contains(exploreRoom))
                return exploreRoom;
            exploreRoom = null;
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return null;
            // Like a player reading the map: the unseen rooms right next to rooms we've seen (through a real
            // connection), never secret rooms, and the one that's closest to walk to.
            Vector2 feet = Feet(me);
            IntVector2 start = new IntVector2(Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y));
            float best = float.MaxValue;
            foreach (RoomHandler room in dungeon.data.rooms)
            {
                if (room == null || room.area == null || !Seen(room, me))
                    continue;
                foreach (RoomHandler next in room.connectedRooms)
                {
                    if (next == null || next.area == null || Seen(next, me)
                        || next.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.SECRET)
                        continue;
                    IntVector2 c = next.GetCenterCell();
                    Path path = null;
                    float d;
                    if (Pathfinder.HasInstance && Pathfinder.Instance.GetPath(start, c, out path, new IntVector2?(IntVector2.One),
                            CellTypes.FLOOR, null, null, true) && path != null)
                        d = path.Count;
                    else
                        d = 1000f + Vector2.Distance(feet, new Vector2(c.x, c.y)); // no path: only as a last resort
                    if (d < best)
                    {
                        best = d;
                        exploreRoom = next;
                    }
                }
            }
            return exploreRoom;
        }

        // Who leads the way: a real person playing, else the living bot with the lowest player number (the host
        // first). Before, only the host's bot explored: once it was a ghost the others stood waiting for it.
        private bool SomeoneElseLeads(PlayerController me)
        {
            if (session == null || session.Status != NetSession.State.Connected)
                return false;
            int mySlot = PlayerIndex == 0 ? session.LocalSlot : StandIn.Slot;
            if (PlayerIndex != 0)
            {
                PlayerController host = GameManager.Instance.PrimaryPlayer;
                if (host != null && !host.IsGhost && !host.healthHaver.IsDead)
                    return true;
            }
            string level = LevelSync.CurrentLevelKey();
            foreach (RemotePlayerState r in session.Remotes)
            {
                if (!r.HasPlayer || r.IsDead || r.Scene != level || r.Slot == mySlot)
                    continue;
                if (!r.OnBreak || r.Slot < mySlot)
                    return true;
            }
            return false;
        }

        private float lastRetryAt;

        // This floor's boss room, if any.
        private static RoomHandler BossRoom()
        {
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return null;
            foreach (RoomHandler room in dungeon.data.rooms)
            {
                if (room != null && room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS)
                    return room;
            }
            return null;
        }

        // Where the exit elevator is, once this floor's boss is beaten (its room open and empty).
        private Vector2 exitAt;
        private float nextExitScanAt;
        private ElevatorDepartureController exit;
        private bool bossDone;

        private bool ExitPoint(out Vector2 at)
        {
            at = Vector2.zero;
            float now = Time.realtimeSinceStartup;
            if (now >= nextExitScanAt)
            {
                nextExitScanAt = now + 1f;
                bossDone = false;
                exit = null;
                Dungeon dungeon = GameManager.Instance.Dungeon;
                if (dungeon != null && dungeon.data != null)
                {
                    foreach (RoomHandler room in dungeon.data.rooms)
                    {
                        if (room != null && room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS
                            && room.hasEverBeenVisited && !room.IsSealed
                            && room.GetActiveEnemiesCount(RoomHandler.ActiveEnemyType.RoomClear) == 0)
                            bossDone = true;
                    }
                    if (bossDone)
                        exit = Object.FindObjectOfType<ElevatorDepartureController>();
                }
            }
            if (!bossDone || exit == null || exit.elevatorFloor == null)
                return false;
            SpeculativeRigidbody floor = exit.elevatorFloor.GetComponent<SpeculativeRigidbody>();
            at = floor != null ? floor.UnitCenter : (Vector2)exit.elevatorFloor.transform.position;
            return true;
        }

        private bool Seen(RoomHandler room, PlayerController me)
        {
            return room.hasEverBeenVisited || explored.Contains(room) || room == me.CurrentRoom;
        }

        // ---- Loot (only when nobody real is playing) ----

        // Every player is a bot right now (Take a Break / test copies), us included.
        private bool EveryoneIsABot()
        {
            if (!active || session == null || session.Status != NetSession.State.Connected)
                return false;
            foreach (RemotePlayerState r in session.Remotes)
            {
                if (!r.OnBreak)
                    return false;
            }
            // A stand-in in the host's game: the host must be on a break too.
            if (PlayerIndex != 0 && !Autopilot.Self.Active)
                return false;
            return true;
        }

        private Component lootTarget;
        private float lootChosenAt, nextLootScanAt, nextInteractAt;
        private int lootPresses;
        private static readonly HashSet<Component> lootGivenUp = new HashSet<Component>();

        // Walk to the nearest chest/gun in a room we've seen and press interact next to it. False: nothing to loot.
        private bool LootStep(PlayerController me, float now)
        {
            RoomHandler room = me.CurrentRoom;
            if (room != null && room.IsSealed)
                return false;
            if (lootTarget == null || !StillLoot(lootTarget, me) || now >= nextLootScanAt)
            {
                Component before = lootTarget;
                nextLootScanAt = now + 1f;
                lootTarget = FindLoot(me);
                if (lootTarget != before)
                {
                    lootChosenAt = now;
                    lootPresses = 0;
                }
            }
            if (lootTarget == null)
                return false;
            Vector2 at = LootPoint(lootTarget);
            Vector2 feet = Feet(me);
            if (now - lootChosenAt > 15f || lootPresses >= 5)
            {
                Plugin.Log.LogInfo("Bot: giving up on " + lootTarget.name + ".");
                lootGivenUp.Add(lootTarget);
                lootTarget = null;
                return false;
            }
            if (Vector2.Distance(feet, at) > 1.3f)
            {
                move = PathToward(me, at);
                me.forceAimPoint = at;
                return true;
            }
            me.forceAimPoint = at;
            // Hearts, keys and money are taken by walking over them: keep going until we stand on it.
            if (!(lootTarget is Chest) && Vector2.Distance(feet, at) > 0.3f)
                move = (at - feet).normalized;
            if (now >= nextInteractAt)
            {
                nextInteractAt = now + 0.7f;
                interact = true;
                lootPresses++;
                if (lootPresses == 1 && !(lootTarget is CurrencyPickup))
                    Plugin.Log.LogInfo("Bot: opening/picking up " + lootTarget.name + ".");
            }
            return true;
        }

        private Vector2 LootPoint(Component c)
        {
            BraveBehaviour b = c as BraveBehaviour;
            if (b != null && b.sprite != null)
                return b.sprite.WorldCenter;
            return c.transform.position;
        }

        private bool StillLoot(Component c, PlayerController me)
        {
            if (c == null || !c.gameObject.activeInHierarchy || lootGivenUp.Contains(c))
                return false;
            Chest chest = c as Chest;
            if (chest != null)
                return !chest.IsOpen && !chest.IsBroken && !chest.pickedUp && (!chest.IsLocked || me.carriedConsumables.KeyBullets > 0);
            PickupObject item = c as PickupObject;
            if (item == null || item.IsBeingSold || item.GetComponentInParent<ShopItemController>() != null)
                return false;
            // Never anything a player owned before: what someone dropped is theirs to come back for (the host's
            // drops stay put for the host even when everyone else is a bot).
            Gun gun = item as Gun;
            if (gun != null)
                return gun.CurrentOwner == null && !gun.HasBeenPickedUp && !gun.HasEverBeenAcquiredByPlayer && !LootSync.IsPlayerDrop(gun);
            PassiveItem passive = item as PassiveItem;
            if (passive != null)
                return !passive.PickedUp && passive.Owner == null && !Flag(passive, "m_pickedUpThisRun");
            PlayerItem active = item as PlayerItem;
            if (active != null)
                return !active.PickedUp && active.LastOwner == null && !Flag(active, "m_pickedUpThisRun");
            HealthPickup heart = item as HealthPickup;
            if (heart != null)
                return !Flag(heart, "m_pickedUp") && (heart.healAmount <= 0f
                    || (me.healthHaver.GetMaxHealth() > 0f && me.healthHaver.GetCurrentHealth() < me.healthHaver.GetMaxHealth()));
            AmmoPickup ammo = item as AmmoPickup;
            if (ammo != null)
                return !ammo.pickedUp && NeedsAmmo(me);
            if (item is KeyBulletPickup)
                return !Flag(item, "m_hasBeenPickedUp");
            CurrencyPickup money = item as CurrencyPickup;
            if (money != null)
                return !money.PreventPickup && !Flag(money, "m_hasBeenPickedUp");
            return false;
        }

        // A private "picked up" flag of a pickup (looked up once per kind).
        private static readonly Dictionary<string, FieldInfo> flagFields = new Dictionary<string, FieldInfo>();
        private static bool Flag(object o, string name)
        {
            string key = o.GetType().FullName + "." + name;
            FieldInfo f;
            if (!flagFields.TryGetValue(key, out f))
            {
                f = AccessTools.Field(o.GetType(), name);
                flagFields[key] = f;
            }
            return f != null && (bool)f.GetValue(o);
        }

        private static bool NeedsAmmo(PlayerController me)
        {
            if (me.inventory == null)
                return false;
            foreach (Gun g in me.inventory.AllGuns)
            {
                if (g != null && !g.InfiniteAmmo && g.ammo < g.AdjustedMaxAmmo)
                    return true;
            }
            return false;
        }

        // Use the best gun that still has ammo (better quality first; starting guns with endless ammo last).
        // Test copies can switch on a gun tour instead: every gun in turn for 15 seconds, refilled.
        private float nextGunPickAt;
        private int tourIndex;
        private void PickGun(PlayerController me, float now)
        {
            if (now < nextGunPickAt || me.inventory == null || me.IsGhost)
                return;
            nextGunPickAt = now + 2f;
            Gun current = me.CurrentGun;
            if (current != null && current.IsReloading)
                return;
            List<Gun> guns = me.inventory.AllGuns;
            if (Autopilot.GunTour && guns.Count > 1)
            {
                nextGunPickAt = now + 15f;
                tourIndex = (tourIndex + 1) % guns.Count;
                Gun next = guns[tourIndex];
                if (next != null)
                {
                    next.ammo = next.AdjustedMaxAmmo;
                    Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": gun tour - " + next.EncounterNameOrDisplayName + ".");
                    me.ChangeToGunSlot(tourIndex, false);
                }
                return;
            }
            int best = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < guns.Count; i++)
            {
                Gun g = guns[i];
                if (g == null || (!g.InfiniteAmmo && g.ammo <= 0))
                    continue;
                int q = (int)g.quality;
                int score = (q >= 1 && q <= 5 ? q * 10 : 0) + (g.InfiniteAmmo ? 0 : 1);
                if (score > bestScore)
                {
                    best = i;
                    bestScore = score;
                }
            }
            if (best < 0 || guns[best] == current)
                return;
            Plugin.Log.LogInfo("Bot" + (PlayerIndex == 0 ? "" : " (" + Label + ")") + ": switching to " + guns[best].DisplayName + ".");
            me.ChangeToGunSlot(best, false);
        }

        private Component FindLoot(PlayerController me)
        {
            Vector2 feet = Feet(me);
            Component best = null;
            float bestDist = 40f;
            foreach (Chest chest in Object.FindObjectsOfType<Chest>())
                Consider(chest, me, feet, ref best, ref bestDist);
            foreach (PickupObject item in Object.FindObjectsOfType<PickupObject>())
                Consider(item, me, feet, ref best, ref bestDist);
            return best;
        }

        private void Consider(Component c, PlayerController me, Vector2 feet, ref Component best, ref float bestDist)
        {
            if (!StillLoot(c, me))
                return;
            Vector2 at = LootPoint(c);
            Dungeon dungeon = GameManager.Instance.Dungeon;
            RoomHandler room = dungeon != null ? dungeon.data.GetAbsoluteRoomFromPosition(new IntVector2(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y))) : null;
            if (room == null || !Seen(room, me) || room.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear))
                return;
            float d = Vector2.Distance(feet, at);
            if (d < bestDist)
            {
                best = c;
                bestDist = d;
            }
        }

        // Who to stay with: a real person playing if there is one (the closest), else the host - never just the closest
        // bot (three bots following each other stood still forever while the host's bot went exploring). Living
        // players before ghosts. A stand-in counts the host's own player too.
        private const float RealBonus = 10000f, HostBonus = 5000f, GhostPenalty = 50000f;

        private bool ClosestTeammate(Vector2 pos, out Vector2 at)
        {
            at = Vector2.zero;
            float bestScore = float.MaxValue;
            if (PlayerIndex != 0 && GameManager.HasInstance && GameManager.Instance.PrimaryPlayer != null)
            {
                PlayerController host = GameManager.Instance.PrimaryPlayer;
                at = host.CenterPosition;
                bestScore = Vector2.Distance(pos, at) - HostBonus - (Autopilot.Self.Active ? 0f : RealBonus)
                    + (host.IsGhost || host.healthHaver.IsDead ? GhostPenalty : 0f);
            }
            if (session != null && session.Status == NetSession.State.Connected)
            {
                string level = LevelSync.CurrentLevelKey();
                foreach (RemotePlayerState r in session.Remotes)
                {
                    if (!r.HasPlayer || r.Scene != level)
                        continue;
                    float score = Vector2.Distance(pos, r.Position) - (r.OnBreak ? 0f : RealBonus) - (r.Slot == 0 ? HostBonus : 0f)
                        + (r.IsDead ? GhostPenalty : 0f);
                    if (score < bestScore)
                    {
                        at = r.Position;
                        bestScore = score;
                    }
                }
            }
            return bestScore < float.MaxValue;
        }

        // ---- Getting around ----

        // Direction to walk now to get to a spot, along the game's own pathfinding (walls, pits, doors).
        // Where the character stands: the middle of its ground collider (the part walls, doors and pits care about).
        // Steering by the body's center put the feet a tile off the path and the bot kept clipping door frames.
        private Vector2 Feet(PlayerController me)
        {
            SpeculativeRigidbody body = me.specRigidbody;
            if (body != null && body.PrimaryPixelCollider != null)
                return body.PrimaryPixelCollider.UnitCenter;
            return me.CenterPosition;
        }

        // ---- Gaps ----
        // Small pits can be crossed with a dodge roll. With no path on floor alone, a path across pits is used if
        // every gap on it is at most MaxGap tiles; at the edge of one the bot rolls toward the floor beyond it.
        private const int MaxGap = 2;
        private Vector2 pitRollDir;
        private float pitRollUntil;
        private float lastGapRollAt = -100f, noGapsUntil;
        private int gapRolls;

        private static bool IsPit(Vector2 p)
        {
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return false;
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
            return dungeon.data.CheckInBoundsAndValid(x, y) && dungeon.data.isPit(x, y);
        }

        // Every pit stretch along the path is short enough to roll over.
        private static bool GapsRollable(List<Vector2> path)
        {
            int run = 0;
            foreach (Vector2 p in path)
            {
                run = IsPit(p) ? run + 1 : 0;
                if (run > MaxGap)
                    return false;
            }
            return run == 0;
        }

        // At the edge of a gap on our path, ready to roll: which way to roll (toward the floor beyond it).
        private bool PitJump(PlayerController me, Vector2 feet, float now, out Vector2 dir)
        {
            dir = Vector2.zero;
            if (me.IsGhost || me.IsDodgeRolling || now < nextDodgeAt || waypoints.Count == 0)
                return false;
            int first = -1;
            for (int i = 0; i < waypoints.Count && i < 4; i++)
            {
                if (IsPit(waypoints[i]))
                {
                    first = i;
                    break;
                }
            }
            if (first < 0 || Vector2.Distance(feet, waypoints[first]) > 1.6f)
                return false;
            int land = -1;
            for (int j = first + 1; j < waypoints.Count && j <= first + MaxGap; j++)
            {
                if (!IsPit(waypoints[j]))
                {
                    land = j;
                    break;
                }
            }
            if (land < 0)
                return false;
            // Aim a little past the first floor tile so the roll ends on solid ground.
            Vector2 target = land + 1 < waypoints.Count && !IsPit(waypoints[land + 1]) ? waypoints[land + 1] : waypoints[land];
            float length = Vector2.Distance(feet, target);
            if (length > 3.5f)
                return false;
            // Along the actual roll line: pit must start right ahead and be short.
            Vector2 step = (target - feet) / Mathf.Max(1, Mathf.CeilToInt(length * 8f));
            float pitStart = -1f, pitLength = 0f;
            for (Vector2 p = feet; Vector2.Distance(feet, p) <= length; p += step)
            {
                if (!IsPit(p))
                    continue;
                if (pitStart < 0f)
                    pitStart = Vector2.Distance(feet, p);
                pitLength += step.magnitude;
            }
            if (pitStart < 0f || pitStart > 0.9f || pitLength > 2.2f)
                return false;
            // The same gap keeps failing (we roll, fall, come back): stop trying gaps for a while.
            gapRolls = now - lastGapRollAt < 8f ? gapRolls + 1 : 1;
            lastGapRollAt = now;
            if (gapRolls > 2)
            {
                noGapsUntil = now + 30f;
                waypoints.Clear();
                Plugin.Log.LogInfo("Bot: this gap keeps failing; walking round for a while.");
                return false;
            }
            dir = (target - feet).normalized;
            return true;
        }

        private Vector2 PathToward(PlayerController me, Vector2 goal)
        {
            Vector2 pos = Feet(me);
            float now = Time.realtimeSinceStartup;
            IntVector2 goalCell = new IntVector2(Mathf.FloorToInt(goal.x), Mathf.FloorToInt(goal.y));
            if (now >= nextPathAt || goalCell != pathGoal || waypoints.Count == 0)
            {
                nextPathAt = now + RepathInterval;
                pathGoal = goalCell;
                waypoints.Clear();
                Path path = null;
                IntVector2 start = new IntVector2(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
                bool found = Pathfinder.HasInstance && Pathfinder.Instance.GetPath(start, goalCell, out path, new IntVector2?(IntVector2.One),
                        CellTypes.FLOOR, null, null, true);
                if (found && path != null && path.Positions != null)
                {
                    foreach (IntVector2 cell in path.Positions)
                        waypoints.Add(new Vector2(cell.x + 0.5f, cell.y + 0.5f));
                }
                // No way round on foot: maybe across small gaps (rolled over, see PitJump).
                if (waypoints.Count == 0 && !me.IsGhost && now >= noGapsUntil && Pathfinder.HasInstance
                    && Pathfinder.Instance.GetPath(start, goalCell, out path, new IntVector2?(IntVector2.One), CellTypes.FLOOR | CellTypes.PIT,
                        null, null, true) && path != null && path.Positions != null)
                {
                    foreach (IntVector2 cell in path.Positions)
                        waypoints.Add(new Vector2(cell.x + 0.5f, cell.y + 0.5f));
                    if (!GapsRollable(waypoints))
                        waypoints.Clear();
                }
                if (forced && waypoints.Count == 0 && now >= nextPathLogAt)
                {
                    nextPathLogAt = now + 3f;
                    DungeonData data = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data : null;
                    Plugin.Log.LogInfo("Bot path: " + start + " -> " + goalCell + " found " + found + ", start cell " + CellKind(data, start)
                        + ", goal cell " + CellKind(data, goalCell) + ", pathfinder " + Pathfinder.HasInstance);
                }
            }
            while (waypoints.Count > 0 && Vector2.Distance(pos, waypoints[0]) < 0.45f)
                waypoints.RemoveAt(0);
            Vector2 next = waypoints.Count > 0 ? waypoints[0] : goal;
            for (int i = 1; i < waypoints.Count && i < 6; i++)
            {
                if (!ClearLine(pos, waypoints[i]))
                    break;
                next = waypoints[i];
            }
            Vector2 d = next - pos;
            return d.sqrMagnitude > 0.01f ? d.normalized : Vector2.zero;
        }

        // Caught on a corner or a door frame (trying to move, not moving): sidestep for a moment, alternating sides,
        // then try again.
        private Vector2 lastPos;
        private float stuckSince = -1f, sidestepUntil, sidestepSign = 1f;
        private Vector2 sidestepDir;
        private int sidesteps, backoffs;
        private RoomHandler backoffRoom;

        private Vector2 Unstick(Vector2 pos, Vector2 want, float now)
        {
            if (now < sidestepUntil)
                return sidestepDir;
            // Stuck = trying to move but less than half a tile of real progress in a while (jiggling against a corner
            // moves us a little back and forth, which used to count as moving).
            bool trying = want.sqrMagnitude > 0.01f;
            if (!trying || stuckSince < 0f || Vector2.Distance(pos, lastPos) > 0.5f)
            {
                if (stuckSince >= 0f && Vector2.Distance(pos, lastPos) > 0.5f && now > sidestepUntil + 1f)
                    sidesteps = 0;
                lastPos = pos;
                stuckSince = trying ? now : -1f;
                return want;
            }
            if (now - stuckSince < 1.2f)
                return want;
            lastPos = pos;
            stuckSince = -1f;
            sidestepSign = -sidestepSign;
            PlayerController me = Player;
            sidesteps++;
            if (sidesteps >= 3 && me != null && me.CurrentRoom != null)
            {
                // Sidesteps didn't free us (a corner: walls two ways): back off the way we came (or to the middle of
                // the room if that's behind us) for a moment.
                IntVector2 c = me.CurrentRoom.GetCenterCell();
                Vector2 toCenter = new Vector2(c.x + 0.5f, c.y + 0.5f) - pos;
                Vector2 back = toCenter.sqrMagnitude > 0.01f && Vector2.Dot(toCenter, want) < 0f ? toCenter.normalized : -want;
                sidestepDir = KeepSafe(me, pos, back);
                sidestepUntil = now + 1.2f;
                sidesteps = 0;
                waypoints.Clear();
                // Stuck twice on the way to the same unexplored room: pick another "?" room off the map.
                if (exploreRoom != null)
                {
                    backoffs = exploreRoom == backoffRoom ? backoffs + 1 : 1;
                    backoffRoom = exploreRoom;
                    if (backoffs >= 2)
                    {
                        Plugin.Log.LogInfo("Bot: stuck on the way to " + exploreRoom.GetRoomName() + "; going back, trying another room.");
                        explored.Add(exploreRoom);
                        exploreRoom = null;
                        progressRoom = null;
                        backoffs = 0;
                    }
                }
                return sidestepDir;
            }
            Vector2 side = new Vector2(-want.y, want.x) * sidestepSign;
            sidestepDir = KeepSafe(me, pos, side);
            if (sidestepDir.sqrMagnitude < 0.01f)
                sidestepDir = KeepSafe(me, pos, -side);
            sidestepUntil = now + 0.2f;
            waypoints.Clear(); // and look for a fresh path afterwards
            return sidestepDir;
        }

        // Never walk into a pit or a wall: try turning a bit, else stand still.
        // Feel ahead before stepping: no pit there, and our real hitbox wouldn't bump into anything (walls, tables,
        // barrels, statues, enemies - the physics engine's own check). If the way is blocked, try slightly turned
        // directions to slide around it; nothing free at all: stand still.
        private static readonly float[] turns = { 0f, 20f, -20f, 40f, -40f, 65f, -65f, 90f, -90f };

        private Vector2 KeepSafe(PlayerController me, Vector2 pos, Vector2 want)
        {
            if (want.sqrMagnitude < 0.01f)
                return Vector2.zero;
            Vector2 dir = want.normalized;
            Vector2 firstWithoutPit = Vector2.zero;
            bool wallAhead = false;
            foreach (float t in turns)
            {
                // A wall in the way (door frames, corners): only small turns, then keep pushing the planned way and let
                // the game slide us along it - turning hard along a wall walked us away from doorways.
                if (wallAhead && Mathf.Abs(t) > 40f)
                    break;
                Vector2 d = Rotate(dir, t);
                if (!me.IsGhost && (!SafeStep(pos + d * 0.8f) || !SafeStep(pos + d * 0.4f)))
                    continue;
                if (firstWithoutPit == Vector2.zero)
                    firstWithoutPit = d;
                bool wall;
                if (!Bumps(me, d, out wall))
                    return d;
                if (t == 0f && wall)
                    wallAhead = true;
            }
            // Blocked (or only by a wall): keep pushing the best pit-free way.
            return firstWithoutPit;
        }

        // Would our hitbox hit something if it moved most of a tile this way?
        private bool Bumps(PlayerController me, Vector2 dir, out bool wall)
        {
            wall = false;
            SpeculativeRigidbody body = me.specRigidbody;
            if (body == null || !PhysicsEngine.HasInstance)
                return false;
            IntVector2 pixels = new IntVector2(Mathf.RoundToInt(dir.x * 8f), Mathf.RoundToInt(dir.y * 8f));
            if (pixels == IntVector2.Zero)
                return false;
            CollisionData hit = null;
            bool blocked = false;
            try
            {
                blocked = PhysicsEngine.Instance.RigidbodyCastWithIgnores(body, pixels, out hit, true, true, null, false, OnlineBodies());
                // Doors open when walked into: don't treat them as walls.
                if (blocked && hit != null && hit.OtherRigidbody != null && hit.OtherRigidbody.GetComponentInParent<DungeonDoorController>() != null)
                    blocked = false;
                // Bullets aren't walls (dodging handles them): treating them as walls froze the bot in a hail of them.
                if (blocked && hit != null && hit.OtherRigidbody != null && hit.OtherRigidbody.GetComponent<Projectile>() != null)
                    blocked = false;
                // A table or something else that breaks in the way: remember it, Think shoots it out of the way.
                if (blocked && hit != null && hit.OtherRigidbody != null)
                {
                    Component breakable = hit.OtherRigidbody.GetComponentInParent<FlippableCover>();
                    if (breakable == null)
                        breakable = hit.OtherRigidbody.GetComponentInParent<MajorBreakable>();
                    if (breakable != null)
                    {
                        blocker = breakable;
                        blockedAt = Time.realtimeSinceStartup;
                    }
                }
                wall = blocked && (hit == null || hit.OtherRigidbody == null);
                if (blocked && forced && Time.realtimeSinceStartup >= nextBumpLogAt)
                {
                    nextBumpLogAt = Time.realtimeSinceStartup + 2f;
                    Plugin.Log.LogInfo("Bot bumps " + dir.ToString("F2") + ": " + (hit == null ? "nothing?" : hit.collisionType + " "
                        + (hit.OtherRigidbody != null ? hit.OtherRigidbody.name + " (parent " + (hit.OtherRigidbody.transform.parent != null ? hit.OtherRigidbody.transform.parent.name : "-") + ")" : "tile")));
                }
            }
            finally
            {
                if (hit != null)
                    CollisionData.Pool.Free(ref hit);
            }
            return blocked;
        }

        // Our own invisible helper bodies for the other players aren't obstacles.
        private SpeculativeRigidbody[] OnlineBodies()
        {
            List<SpeculativeRigidbody> list = ignoreScratch;
            list.Clear();
            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                SpeculativeRigidbody b = TargetSync.BodyOf(i);
                if (b != null)
                    list.Add(b);
                b = TablePush.BodyOf(i);
                if (b != null)
                    list.Add(b);
            }
            return list.ToArray();
        }
        private static readonly List<SpeculativeRigidbody> ignoreScratch = new List<SpeculativeRigidbody>();
        private float nextBumpLogAt;

        private bool SafeStep(Vector2 p)
        {
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return true;
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
            if (!dungeon.data.CheckInBoundsAndValid(x, y))
                return false;
            return !dungeon.data.isPit(x, y);
        }

        private Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        // A straight walk with room for the character's width (checked on both sides of the line).
        private bool ClearLine(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            if (d.sqrMagnitude < 0.0001f)
                return true;
            Vector2 side = new Vector2(-d.y, d.x).normalized * 0.4f;
            return !WallOrPitBetween(from + side, to + side) && !WallOrPitBetween(from - side, to - side);
        }

        private bool WallOrPitBetween(Vector2 from, Vector2 to)
        {
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return false;
            float length = Vector2.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length * 4f));
            for (int i = 1; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (!dungeon.data.CheckInBoundsAndValid(x, y) || dungeon.data.isWall(x, y) || dungeon.data.isPit(x, y))
                    return true;
            }
            return false;
        }

        private bool WallBetween(Vector2 from, Vector2 to)
        {
            Dungeon dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null)
                return false;
            float length = Vector2.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length * 3f));
            for (int i = 1; i < steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (!dungeon.data.CheckInBoundsAndValid(x, y) || dungeon.data.isWall(x, y))
                    return true;
            }
            return false;
        }

        // ---- Pressing the buttons ----
        // The game reads the player's controls from its action set once per input tick. While the autopilot is on,
        // the player's set gets our values instead of the keyboard's/pad's.

        private static readonly FieldInfo actionsField = AccessTools.Field(typeof(PlayerActionSet), "actions");
        private static readonly FieldInfo oneAxisField = AccessTools.Field(typeof(PlayerActionSet), "oneAxisActions");
        private static readonly FieldInfo twoAxisField = AccessTools.Field(typeof(PlayerActionSet), "twoAxisActions");
        // Internal in the game's InControl copy, so called by reflection.
        private static readonly MethodInfo oneAxisUpdate = AccessTools.Method(typeof(PlayerOneAxisAction), "Update");
        private static readonly MethodInfo twoAxisUpdate = AccessTools.Method(typeof(PlayerTwoAxisAction), "Update");
        private static readonly object[] tickArgs = new object[2];

        // True if this brain answered for that action set (it's the one of the player it drives).
        public bool FillActions(PlayerActionSet __instance, ulong updateTick, float deltaTime)
        {
            if (!active || !GameManager.HasInstance || GameManager.Instance.IsPaused || (PlayerIndex == 0 && MultiplayerMenu.IsOpen))
                return false;
            if (!BraveInput.HasInstanceForPlayer(PlayerIndex) || !ReferenceEquals(BraveInput.GetInstanceForPlayer(PlayerIndex).ActiveActions, __instance))
                return false;
            GungeonActions set = __instance as GungeonActions;
            if (set == null || actionsField == null)
                return false;

            inputTicks++;
            Dictionary<PlayerAction, float> pressed = new Dictionary<PlayerAction, float>();
            pressed[set.Left] = Mathf.Max(0f, -move.x);
            pressed[set.Right] = Mathf.Max(0f, move.x);
            pressed[set.Down] = Mathf.Max(0f, -move.y);
            pressed[set.Up] = Mathf.Max(0f, move.y);
            pressed[set.ShootAction] = shoot ? 1f : 0f;
            pressed[set.DodgeRollAction] = dodge ? 1f : 0f;
            pressed[set.ReloadAction] = reload ? 1f : 0f;
            pressed[set.InteractAction] = interact ? 1f : 0f;
            pressed[set.BlankAction] = blank ? 1f : 0f;
            dodge = false;   // one tick each
            blank = false;
            reload = false;
            interact = false;

            List<PlayerAction> actions = (List<PlayerAction>)actionsField.GetValue(__instance);
            for (int i = 0; i < actions.Count; i++)
            {
                float v;
                pressed.TryGetValue(actions[i], out v);
                actions[i].CommitWithValue(v, updateTick, deltaTime);
            }
            // The combined controls (move stick, aim) are worked out from the single ones, like the game does.
            tickArgs[0] = updateTick;
            tickArgs[1] = deltaTime;
            List<PlayerOneAxisAction> oneAxis = oneAxisField != null ? (List<PlayerOneAxisAction>)oneAxisField.GetValue(__instance) : null;
            if (oneAxis != null && oneAxisUpdate != null)
                foreach (PlayerOneAxisAction a in oneAxis)
                    oneAxisUpdate.Invoke(a, tickArgs);
            List<PlayerTwoAxisAction> twoAxis = twoAxisField != null ? (List<PlayerTwoAxisAction>)twoAxisField.GetValue(__instance) : null;
            if (twoAxis != null && twoAxisUpdate != null)
                foreach (PlayerTwoAxisAction a in twoAxis)
                    twoAxisUpdate.Invoke(a, tickArgs);
            return true;
        }
    }

    // "Take a Break" for our own player, plus any stand-in bots, all driven through the game's controls.
    public static class Autopilot
    {
        public static readonly Brain Self = new Brain(0, "us");
        private static readonly List<Brain> brains = new List<Brain> { Self };

        public static bool Active
        {
            get { return Self.Active; }
        }

        public static bool GunTour; // test copies: rotate through every gun (TestLoadout)

        public static bool Explore
        {
            get { return Self.Explore; }
            set { Self.Explore = value; }
        }

        public static void Init(NetSession netSession, Harmony harmony)
        {
            Brain.SetSession(netSession);
            harmony.PatchAll(typeof(Autopilot));
        }

        public static void Start(bool keepOn) { Self.Start(keepOn); }
        public static void Stop(string why) { Self.Stop(why); }
        public static void Toggle() { Self.Toggle(); }

        public static void Add(Brain b)
        {
            if (!brains.Contains(b))
                brains.Add(b);
        }

        public static void Remove(Brain b)
        {
            if (b != Self)
                brains.Remove(b);
        }

        public static void Update()
        {
            if (Plugin.TakeABreakKey != null && Input.GetKeyDown(Plugin.TakeABreakKey.Value) && !Self.Active)
            {
                Self.Start(false);
                return;
            }
            for (int i = 0; i < brains.Count; i++)
                brains[i].Update();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerActionSet), "Update", new System.Type[] { typeof(ulong), typeof(float) })]
        private static bool BeforeActionsUpdate(PlayerActionSet __instance, ulong updateTick, float deltaTime)
        {
            for (int i = 0; i < brains.Count; i++)
            {
                if (brains[i].FillActions(__instance, updateTick, deltaTime))
                    return false;
            }
            return true;
        }
    }
}
