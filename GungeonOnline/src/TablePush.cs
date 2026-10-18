using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Other players push tables in OUR game too, so a pushed table moves right away on every screen.
    //
    // The game pushes a flipped table (FlippableCover sets CanBePushed) when a moving body that CanPush runs into
    // it: the physics engine does it while moving bodies by their Velocity. So every other player gets a "feet"
    // body here: a copy of our player's ground collider that follows their smoothed position by velocity and can
    // push. It ignores everything except tables (bullets, enemies, us, doors... pass through).
    //
    // Who reports the table's final position: the game of the player who pushed it (WorldSync). Every other game
    // pushes its own copy along and, once it stops moving, glides it to that reported resting place.
    public static class TablePush
    {
        private const float WarpDistance = 2f;   // further than this from where they are: jump, don't push

        private static NetSession session;
        private static readonly GameObject[] gos = new GameObject[Protocol.MaxPlayers];
        private static readonly SpeculativeRigidbody[] bodies = new SpeculativeRigidbody[Protocol.MaxPlayers];
        private static readonly bool[] placed = new bool[Protocol.MaxPlayers];
        private static float nextTouchLog;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        public static SpeculativeRigidbody BodyOf(int slot)
        {
            return slot >= 0 && slot < bodies.Length && gos[slot] != null && gos[slot].activeSelf ? bodies[slot] : null;
        }

        // Where a player's feet are drawn right now (their smoothed position), for "who pushed this table".
        public static bool TryGetFeet(int slot, out Vector2 feet)
        {
            feet = Vector2.zero;
            if (slot < 0 || slot >= bodies.Length || !placed[slot] || gos[slot] == null || !gos[slot].activeSelf)
                return false;
            feet = gos[slot].transform.position;
            return true;
        }

        public static void Update()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool ready = me != null && me.specRigidbody != null && PhysicsEngine.HasInstance && !GameManager.Instance.IsLoadingLevel
                && session != null && session.Status == NetSession.State.Connected;
            string level = ready ? LevelSync.CurrentLevelKey() : "";
            for (int i = 0; i < bodies.Length; i++)
            {
                RemotePlayerState r = session != null ? session.Players[i] : null;
                bool want = ready && i != session.LocalSlot && r.Connected && r.HasPlayer && !r.IsDead && r.Scene == level;
                if (!want)
                {
                    if (gos[i] != null && gos[i].activeSelf)
                        gos[i].SetActive(false);
                    placed[i] = false;
                    continue;
                }
                Follow(i, r, me);
            }
        }

        private static void Follow(int slot, RemotePlayerState r, PlayerController me)
        {
            if (!Ensure(slot, me))
                return;
            GameObject go = gos[slot];
            SpeculativeRigidbody body = bodies[slot];
            Vector2 feet = r.Motion.HasData ? r.Motion.Sample() : r.FeetPosition;
            Vector2 target = feet + r.RootOffset; // their transform position (the collider is set up around it)
            if (!go.activeSelf)
            {
                go.SetActive(true);
                placed[slot] = false;
            }
            try
            {
                Vector2 now = go.transform.position;
                if (!placed[slot] || Vector2.Distance(now, target) > WarpDistance)
                {
                    // Arrived, teleported, or fell behind: jump there instead of shoving everything on the way.
                    go.transform.position = new Vector3(target.x, target.y, go.transform.position.z);
                    body.Velocity = Vector2.zero;
                    body.Reinitialize();
                    placed[slot] = true;
                    return;
                }
                // Move by velocity so the physics engine pushes whatever table is in the way.
                float dt = Mathf.Max(BraveTime.DeltaTime, 0.001f);
                body.Velocity = (target - now) / dt;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Table push body for player " + (slot + 1) + " failed (" + e.GetType().Name + "); rebuilding.");
                Object.Destroy(go);
                gos[slot] = null;
                bodies[slot] = null;
                placed[slot] = false;
            }
        }

        // Only tables (and their parts) are pushed; everything else passes through.
        private static void OnPreCollision(SpeculativeRigidbody mine, PixelCollider myCollider, SpeculativeRigidbody other, PixelCollider otherCollider)
        {
            if (other == null || other.GetComponentInParent<FlippableCover>() == null)
                PhysicsEngine.SkipCollision = true;
        }

        private static bool Ensure(int slot, PlayerController me)
        {
            if (bodies[slot] != null && gos[slot] != null)
                return true;
            // Our player's ground collider (the one that walks into things), copied.
            PixelCollider template = null;
            foreach (PixelCollider c in me.specRigidbody.PixelColliders)
            {
                if (c.CollisionLayer == CollisionLayer.PlayerCollider)
                {
                    template = c;
                    break;
                }
            }
            GameObject go = new GameObject("GungeonOnline Table Pusher " + (slot + 1));
            go.SetActive(false);
            SpeculativeRigidbody body = go.AddComponent<SpeculativeRigidbody>();
            body.CollideWithOthers = true;
            body.CollideWithTileMap = false;
            body.CanPush = true;
            body.CanBePushed = false;
            body.PixelColliders = new List<PixelCollider>();
            PixelCollider box = new PixelCollider();
            box.ColliderGenerationMode = PixelCollider.PixelColliderGeneration.Manual;
            box.CollisionLayer = CollisionLayer.PlayerCollider;
            if (template != null && template.ColliderGenerationMode == PixelCollider.PixelColliderGeneration.Manual)
            {
                box.ManualOffsetX = template.ManualOffsetX;
                box.ManualOffsetY = template.ManualOffsetY;
                box.ManualWidth = template.ManualWidth;
                box.ManualHeight = template.ManualHeight;
            }
            else
            {
                box.ManualOffsetX = 3;    // a small box at the feet, like the players'
                box.ManualOffsetY = 0;
                box.ManualWidth = 10;
                box.ManualHeight = 6;
            }
            body.PixelColliders.Add(box);
            body.OnPreRigidbodyCollision += OnPreCollision;
            int s = slot;
            body.OnRigidbodyCollision += delegate (CollisionData data)
            {
                SpeculativeRigidbody other = data != null ? data.OtherRigidbody : null;
                FlippableCover table = other != null ? other.GetComponentInParent<FlippableCover>() : null;
                if (table == null || Time.realtimeSinceStartup < nextTouchLog)
                    return;
                nextTouchLog = Time.realtimeSinceStartup + 1f;
                Plugin.Log.LogInfo("Player " + (s + 1) + "'s body touched table '" + table.name + "' (flipped " + table.IsFlipped
                    + ", pushable " + other.CanBePushed + ").");
            };
            gos[slot] = go;
            bodies[slot] = body;
            placed[slot] = false;
            Plugin.Log.LogInfo("Created player " + (slot + 1) + "'s table pusher (" + box.ManualWidth + "x" + box.ManualHeight + " px).");
            return true;
        }
    }
}
