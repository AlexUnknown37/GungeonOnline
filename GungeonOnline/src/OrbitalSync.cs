using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Things that float around a player (Guon Stones, orbiting glass, trailing orbitals): the partner's game didn't
    // draw them. We send what each one looks like (sprite sheet + frame) and where it is, ~10 times a second; the
    // other game draws copies there. Just pictures: blocking bullets etc. still happens in the owner's game.
    //
    // Positions go relative to the owner's feet. Drawn only at those 10 snapshots a second, a fast Guon Stone just
    // jumped around (or looked like it stood still), so between snapshots each orbiting copy keeps turning around
    // where the owner is drawn, at the speed the last snapshots showed; trailing ones glide after their spot.
    public static class OrbitalSync
    {
        private const float SendInterval = 0.1f;

        private static NetSession session;
        private static float nextSendAt;
        private static int lastSentCount;
        private static float nextReportAt;

        private class Shown
        {
            public tk2dBaseSprite Sprite;
            public Material Material;
            public Texture Texture;
            public bool Orbit;          // turns around the owner (else trails after them)
            public bool Placed;
            public Vector2 Rel;         // newest offset from the owner's feet
            public float RelAt;         // when it arrived
            public float Spin;          // degrees a second around the owner
        }
        // Each other player's orbitals, by slot.
        private static readonly List<Shown>[] shownBy = NewLists();
        private static readonly float[] lastReceivedAt = new float[Protocol.MaxPlayers];

        private static List<Shown>[] NewLists()
        {
            List<Shown>[] lists = new List<Shown>[Protocol.MaxPlayers];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<Shown>();
            return lists;
        }
        private static readonly Dictionary<int, tk2dSpriteCollectionData> collections = new Dictionary<int, tk2dSpriteCollectionData>();
        private static float nextCollectionScanAt;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static IEnumerable<tk2dBaseSprite> OurOrbitalSprites(PlayerController me)
        {
            if (me.orbitals != null)
            {
                foreach (IPlayerOrbital o in me.orbitals)
                {
                    Component c = o as Component;
                    tk2dBaseSprite s = c != null ? c.GetComponentInChildren<tk2dBaseSprite>() : null;
                    if (s != null)
                        yield return s;
                }
            }
            if (me.trailOrbitals != null)
            {
                foreach (PlayerOrbitalFollower f in me.trailOrbitals)
                {
                    tk2dBaseSprite s = f != null ? f.GetComponentInChildren<tk2dBaseSprite>() : null;
                    if (s != null)
                        yield return s;
                }
            }
        }

        public static void Update()
        {
            if (session == null || session.Status != NetSession.State.Connected)
            {
                for (int i = 0; i < shownBy.Length; i++)
                    Clear(shownBy[i]);
                return;
            }
            // Nothing heard from a player for a while (they have none now, or left the floor): hide theirs.
            for (int i = 0; i < shownBy.Length; i++)
            {
                if (shownBy[i].Count > 0 && Time.realtimeSinceStartup - lastReceivedAt[i] > 1f)
                    Clear(shownBy[i]);
            }

            // Every frame: keep the copies going round (or following) where their owner is drawn.
            for (int i = 0; i < shownBy.Length; i++)
            {
                foreach (Shown s in shownBy[i])
                {
                    if (s.Sprite != null && s.Sprite.gameObject.activeSelf)
                        Place(s, i, false);
                }
            }

            if (Time.realtimeSinceStartup < nextSendAt)
                return;
            nextSendAt = Time.realtimeSinceStartup + SendInterval;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null)
                return;
            List<tk2dBaseSprite> sprites = new List<tk2dBaseSprite>();
            List<bool> orbiting = new List<bool>();
            int orbitCount = me.orbitals != null ? me.orbitals.Count : 0, n = 0;
            foreach (tk2dBaseSprite s in OurOrbitalSprites(me))
            {
                bool orbit = n++ < orbitCount;
                if (s.Collection != null && s.renderer != null && s.renderer.enabled && s.gameObject.activeInHierarchy)
                {
                    sprites.Add(s);
                    orbiting.Add(orbit);
                }
            }
            Vector2 feet = me.sprite != null ? me.sprite.WorldBottomCenter : me.CenterPosition;
            if (sprites.Count == 0 && lastSentCount == 0)
                return;
            lastSentCount = sprites.Count;
            int level = LevelSync.CurrentLevelCode();
            session.SendUnreliable(MsgType.Orbitals, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)Mathf.Min(sprites.Count, 32));
                for (int i = 0; i < sprites.Count && i < 32; i++)
                {
                    tk2dBaseSprite s = sprites[i];
                    w.Write(Protocol.Hash(s.Collection.name));
                    w.Write((ushort)Mathf.Clamp(s.spriteId, 0, 65535));
                    Protocol.WriteQ(w, (Vector2)s.transform.position - feet);
                    w.Write(Protocol.Q(s.HeightOffGround));
                    w.Write((byte)((s.FlipX ? 1 : 0) | (orbiting[i] ? 2 : 0)));
                    w.Write((short)Mathf.RoundToInt(Mathf.Repeat(s.transform.eulerAngles.z, 360f) * 10f));
                }
            });
        }

        public static void OnOrbitals(BinaryReader r)
        {
            int level = r.ReadInt32();
            int count = r.ReadByte();
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool here = me != null && level == LevelSync.CurrentLevelCode() && !GameManager.Instance.IsLoadingLevel;
            int from = Mathf.Clamp(session.From, 0, shownBy.Length - 1);
            List<Shown> shown = shownBy[from];
            lastReceivedAt[from] = Time.realtimeSinceStartup;
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                int collectionCode = r.ReadInt32();
                int spriteId = r.ReadUInt16();
                Vector2 rel = Protocol.ReadQ(r);
                float height = Protocol.DQ(r.ReadInt16());
                byte bits = r.ReadByte();
                bool flip = (bits & 1) != 0, orbit = (bits & 2) != 0;
                float angle = r.ReadInt16() / 10f;
                if (!here)
                    continue;
                tk2dSpriteCollectionData collection = FindCollection(collectionCode);
                if (collection == null || spriteId < 0 || spriteId >= collection.spriteDefinitions.Length)
                    continue;
                Shown s = GetShown(shown, used, me);
                if (s == null)
                    continue;
                used++;
                if (!s.Sprite.gameObject.activeSelf)
                    s.Sprite.gameObject.SetActive(true);
                s.Sprite.SetSprite(collection, spriteId);
                s.Sprite.FlipX = flip;
                s.Sprite.HeightOffGround = height;
                float now = Time.realtimeSinceStartup;
                if (orbit && s.Orbit && s.Placed && s.Rel.sqrMagnitude > 0.01f && rel.sqrMagnitude > 0.01f)
                {
                    // How fast it went round since the last snapshot (smoothed: snapshots don't arrive evenly).
                    float dt = Mathf.Clamp(now - s.RelAt, 0.05f, 0.5f);
                    float seen = Mathf.Clamp(Vector2.SignedAngle(s.Rel, rel) / dt, -1080f, 1080f);
                    s.Spin = Mathf.Lerp(s.Spin, seen, 0.5f);
                }
                else if (!orbit)
                    s.Spin = 0f;
                s.Orbit = orbit;
                s.Rel = rel;
                s.RelAt = now;
                s.Sprite.transform.rotation = Quaternion.Euler(0f, 0f, angle);
                RemotePuppet.ApplySheet(s.Sprite, ref s.Material, ref s.Texture);
                if (s.Sprite.renderer != null)
                    s.Sprite.renderer.enabled = true;
                Place(s, from, true);
            }
            // Now and then, for the log: what we draw of this player's and how fast it goes round.
            if (used > 0 && Time.realtimeSinceStartup >= nextReportAt)
            {
                nextReportAt = Time.realtimeSinceStartup + 30f;
                string spins = "";
                for (int i = 0; i < used; i++)
                    spins += (i > 0 ? ", " : "") + (shown[i].Orbit ? Mathf.RoundToInt(shown[i].Spin) + " deg/s" : "trailing");
                Plugin.Log.LogInfo("Drawing " + used + " orbital(s) of player " + (from + 1) + ": " + spins + ".");
            }
            for (int i = used; i < shown.Count; i++)
            {
                if (shown[i].Sprite != null)
                    shown[i].Sprite.gameObject.SetActive(false);
            }
        }

        // Where the owner's character is drawn right now (smoothed), if they have one.
        private static bool OwnerFeet(int slot, out Vector2 feet)
        {
            feet = Vector2.zero;
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (p.Slot == slot && p.HasPlayer)
                {
                    feet = p.Motion.HasData ? p.Motion.Sample() : p.FeetPosition;
                    return true;
                }
            }
            return false;
        }

        private static void Place(Shown s, int slot, bool snapshot)
        {
            Vector2 feet;
            if (!OwnerFeet(slot, out feet))
                return;
            Vector2 target;
            if (s.Orbit)
            {
                // Turned on from the newest snapshot by the speed it's been going round (at most half a second on).
                float turned = s.Spin * Mathf.Min(Time.realtimeSinceStartup - s.RelAt, 0.5f);
                target = feet + (Vector2)(Quaternion.Euler(0f, 0f, turned) * s.Rel);
            }
            else
            {
                target = feet + s.Rel;
                if (s.Placed && snapshot)
                    return; // trailing ones glide there over the next frames
                if (s.Placed)
                    target = Vector2.Lerp(s.Sprite.transform.position, target, Mathf.Clamp01(Time.deltaTime * 12f));
            }
            s.Placed = true;
            s.Sprite.transform.position = new Vector3(target.x, target.y, target.y);
            s.Sprite.UpdateZDepth();
        }

        private static Shown GetShown(List<Shown> shown, int index, PlayerController me)
        {
            while (shown.Count <= index)
            {
                tk2dBaseSprite s = me.sprite != null ? RemotePuppet.CloneStrippedSprite(me.sprite, "GungeonOnline Partner Orbital") : null;
                if (s == null)
                    return null;
                Shown entry = new Shown();
                entry.Sprite = s;
                shown.Add(entry);
            }
            if (shown[index].Sprite == null)
            {
                // Destroyed by a floor change: start over.
                Clear(shown);
                return GetShown(shown, index, me);
            }
            return shown[index];
        }

        // Sprite sheets by the code of their name (Protocol.Hash).
        private static tk2dSpriteCollectionData FindCollection(int code)
        {
            tk2dSpriteCollectionData c;
            if (collections.TryGetValue(code, out c) && c != null)
                return c;
            if (Time.realtimeSinceStartup < nextCollectionScanAt)
                return null;
            nextCollectionScanAt = Time.realtimeSinceStartup + 2f;
            foreach (tk2dSpriteCollectionData found in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>())
            {
                if (found != null)
                {
                    int key = Protocol.Hash(found.name);
                    if (!collections.ContainsKey(key))
                        collections[key] = found;
                }
            }
            collections.TryGetValue(code, out c);
            return c;
        }

        private static void Clear(List<Shown> shown)
        {
            foreach (Shown s in shown)
            {
                if (s.Sprite != null)
                    Object.Destroy(s.Sprite.gameObject);
            }
            shown.Clear();
        }
    }
}
