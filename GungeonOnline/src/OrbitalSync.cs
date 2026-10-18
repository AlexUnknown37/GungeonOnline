using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Things that float around a player (Guon Stones, orbiting glass, trailing orbitals): the partner's game didn't
    // draw them. We send what each one looks like (sprite sheet + frame) and where it is, ~10 times a second; the
    // other game draws copies there. Just pictures: blocking bullets etc. still happens in the owner's game.
    public static class OrbitalSync
    {
        private const float SendInterval = 0.1f;

        private static NetSession session;
        private static float nextSendAt;
        private static int lastSentCount;

        private class Shown
        {
            public tk2dBaseSprite Sprite;
            public Material Material;
            public Texture Texture;
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
        private static readonly Dictionary<string, tk2dSpriteCollectionData> collections = new Dictionary<string, tk2dSpriteCollectionData>();
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

            if (Time.realtimeSinceStartup < nextSendAt)
                return;
            nextSendAt = Time.realtimeSinceStartup + SendInterval;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null)
                return;
            List<tk2dBaseSprite> sprites = new List<tk2dBaseSprite>();
            foreach (tk2dBaseSprite s in OurOrbitalSprites(me))
            {
                if (s.Collection != null && s.renderer != null && s.renderer.enabled && s.gameObject.activeInHierarchy)
                    sprites.Add(s);
            }
            if (sprites.Count == 0 && lastSentCount == 0)
                return;
            lastSentCount = sprites.Count;
            string level = LevelSync.CurrentLevelKey();
            session.SendUnreliable(MsgType.Orbitals, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)Mathf.Min(sprites.Count, 32));
                for (int i = 0; i < sprites.Count && i < 32; i++)
                {
                    tk2dBaseSprite s = sprites[i];
                    w.Write(s.Collection.name);
                    w.Write(s.spriteId);
                    Vector3 p = s.transform.position;
                    w.Write(p.x); w.Write(p.y);
                    w.Write(s.HeightOffGround);
                    w.Write(s.FlipX);
                    w.Write(s.transform.eulerAngles.z);
                }
            });
        }

        public static void OnOrbitals(BinaryReader r)
        {
            string level = r.ReadString();
            int count = r.ReadByte();
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool here = me != null && level == LevelSync.CurrentLevelKey() && !GameManager.Instance.IsLoadingLevel;
            int from = Mathf.Clamp(session.From, 0, shownBy.Length - 1);
            List<Shown> shown = shownBy[from];
            lastReceivedAt[from] = Time.realtimeSinceStartup;
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                string collectionName = r.ReadString();
                int spriteId = r.ReadInt32();
                Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
                float height = r.ReadSingle();
                bool flip = r.ReadBoolean();
                float angle = r.ReadSingle();
                if (!here)
                    continue;
                tk2dSpriteCollectionData collection = FindCollection(collectionName);
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
                s.Sprite.transform.position = new Vector3(pos.x, pos.y, pos.y);
                s.Sprite.transform.rotation = Quaternion.Euler(0f, 0f, angle);
                RemotePuppet.ApplySheet(s.Sprite, ref s.Material, ref s.Texture);
                if (s.Sprite.renderer != null)
                    s.Sprite.renderer.enabled = true;
                s.Sprite.UpdateZDepth();
            }
            for (int i = used; i < shown.Count; i++)
            {
                if (shown[i].Sprite != null)
                    shown[i].Sprite.gameObject.SetActive(false);
            }
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

        private static tk2dSpriteCollectionData FindCollection(string name)
        {
            tk2dSpriteCollectionData c;
            if (collections.TryGetValue(name, out c) && c != null)
                return c;
            if (Time.realtimeSinceStartup < nextCollectionScanAt)
                return null;
            nextCollectionScanAt = Time.realtimeSinceStartup + 2f;
            foreach (tk2dSpriteCollectionData found in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>())
            {
                if (found != null && !collections.ContainsKey(found.name))
                    collections[found.name] = found;
            }
            collections.TryGetValue(name, out c);
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
