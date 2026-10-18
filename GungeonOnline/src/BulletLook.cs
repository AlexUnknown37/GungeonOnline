using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // How one of our bullets looks after items and synergies changed it, so the copies in the other games look the
    // same. The shooter's own game has already worked out every effect (with every item, synergy and modded item),
    // so we copy the finished bullet instead of working the effects out again from the inventory.
    // Only what differs from the gun's normal bullet is sent:
    // - Tint: the colour items give bullets (fire orange, poison green, frost blue, charm pink...)
    // - Sprite: a different bullet picture
    // - Sprite colour: a recoloured picture
    // - Trails: trail effects switched on or off
    // - Bounces and piercing: so the copy keeps flying where the real bullet does, instead of vanishing at the
    //   first wall or enemy
    // - Chain lightning (Shock Rounds, Battery Bullets...): the arcs between bullets, harmless on copies
    // Size is sent with every shot already (BulletSync). These are looks only: what a bullet does to enemies is still
    // decided in the shooter's game.
    public class BulletLook
    {
        private const byte TintFlag = 1, SpriteFlag = 2, ColorFlag = 4, TrailFlag = 8, BounceFlag = 16, PierceFlag = 32, ChainFlag = 64;

        private byte flags;
        private Color tint;
        private string collection = "";
        private int spriteId;
        private Color spriteColor = Color.white;
        private byte trails;
        private int bounces;
        private int pierces;
        private string chainVfx = "";
        private float chainDistance;
        private bool chainAny;

        // Tints our bullets got while being fired (the game's tint step), read when the shot is sent.
        private static readonly Dictionary<Projectile, Color> tints = new Dictionary<Projectile, Color>();

        public static void Tinted(Projectile p, Color c)
        {
            if (tints.Count > 400)
                tints.Clear();
            tints[p] = c;
        }

        // ---- Sending ----

        public static BulletLook Read(Projectile p, GameObject prefab)
        {
            BulletLook look = new BulletLook();
            Color c;
            if (tints.TryGetValue(p, out c))
            {
                tints.Remove(p);
                look.flags |= TintFlag;
                look.tint = c;
            }
            Projectile normal = prefab != null ? prefab.GetComponent<Projectile>() : null;
            tk2dBaseSprite s = p.sprite;
            tk2dBaseSprite ns = normal != null ? normal.GetComponentInChildren<tk2dBaseSprite>(true) : null;
            if (s != null && s.Collection != null)
            {
                bool otherPicture = ns == null || ns.Collection != s.Collection || ns.spriteId != s.spriteId;
                if (otherPicture)
                {
                    look.flags |= SpriteFlag;
                    look.collection = s.Collection.name ?? "";
                    look.spriteId = s.spriteId;
                }
                Color normalColor = ns != null ? ns.color : Color.white;
                if (s.color != normalColor)
                {
                    look.flags |= ColorFlag;
                    look.spriteColor = s.color;
                }
            }
            byte bits = TrailBits(p);
            if (normal == null || bits != TrailBits(normal))
            {
                look.flags |= TrailFlag;
                look.trails = bits;
            }
            BounceProjModifier bounce = p.GetComponent<BounceProjModifier>();
            BounceProjModifier normalBounce = normal != null ? normal.GetComponent<BounceProjModifier>() : null;
            if (bounce != null && (normalBounce == null || normalBounce.numberOfBounces != bounce.numberOfBounces))
            {
                look.flags |= BounceFlag;
                look.bounces = bounce.numberOfBounces;
            }
            PierceProjModifier pierce = p.GetComponent<PierceProjModifier>();
            PierceProjModifier normalPierce = normal != null ? normal.GetComponent<PierceProjModifier>() : null;
            if (pierce != null && (normalPierce == null || normalPierce.penetration != pierce.penetration))
            {
                look.flags |= PierceFlag;
                look.pierces = pierce.penetration;
            }
            ChainLightningModifier chain = p.GetComponent<ChainLightningModifier>();
            if (chain != null && chain.LinkVFXPrefab != null && (normal == null || normal.GetComponent<ChainLightningModifier>() == null))
            {
                look.flags |= ChainFlag;
                look.chainVfx = chain.LinkVFXPrefab.name;
                look.chainDistance = chain.maximumLinkDistance;
                look.chainAny = chain.CanChainToAnyProjectile;
            }
            return look;
        }

        private static byte TrailBits(Projectile p)
        {
            byte b = 0;
            if (p.TrailRenderer != null && p.TrailRenderer.enabled)
                b |= 1;
            if (p.CustomTrailRenderer != null && p.CustomTrailRenderer.enabled)
                b |= 2;
            if (p.ParticleTrail != null && p.ParticleTrail.gameObject.activeSelf)
                b |= 4;
            return b;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(flags);
            if ((flags & TintFlag) != 0)
                WriteColor(w, tint);
            if ((flags & SpriteFlag) != 0)
            {
                w.Write(collection);
                w.Write(spriteId);
            }
            if ((flags & ColorFlag) != 0)
                WriteColor(w, spriteColor);
            if ((flags & TrailFlag) != 0)
                w.Write(trails);
            if ((flags & BounceFlag) != 0)
                w.Write((short)Mathf.Clamp(bounces, 0, 999));
            if ((flags & PierceFlag) != 0)
                w.Write((short)Mathf.Clamp(pierces, 0, 999));
            if ((flags & ChainFlag) != 0)
            {
                w.Write(chainVfx);
                w.Write(chainDistance);
                w.Write(chainAny);
            }
        }

        private static void WriteColor(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }

        // ---- Receiving ----

        public static BulletLook ReadFrom(BinaryReader r)
        {
            BulletLook look = new BulletLook();
            look.flags = r.ReadByte();
            if ((look.flags & TintFlag) != 0)
                look.tint = ReadColor(r);
            if ((look.flags & SpriteFlag) != 0)
            {
                look.collection = r.ReadString();
                look.spriteId = r.ReadInt32();
            }
            if ((look.flags & ColorFlag) != 0)
                look.spriteColor = ReadColor(r);
            if ((look.flags & TrailFlag) != 0)
                look.trails = r.ReadByte();
            if ((look.flags & BounceFlag) != 0)
                look.bounces = r.ReadInt16();
            if ((look.flags & PierceFlag) != 0)
                look.pierces = r.ReadInt16();
            if ((look.flags & ChainFlag) != 0)
            {
                look.chainVfx = r.ReadString();
                look.chainDistance = r.ReadSingle();
                look.chainAny = r.ReadBoolean();
            }
            return look;
        }

        private static Color ReadColor(BinaryReader r)
        {
            return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        // Copies are pooled bullets: one that wore a look before gets the normal look back when the next shot
        // doesn't have that look.
        private static readonly HashSet<Projectile> tintedCopies = new HashSet<Projectile>();
        private static readonly Dictionary<Projectile, KeyValuePair<tk2dSpriteCollectionData, int>> repicturedCopies =
            new Dictionary<Projectile, KeyValuePair<tk2dSpriteCollectionData, int>>();
        private static readonly Dictionary<Projectile, Color> recolouredCopies = new Dictionary<Projectile, Color>();
        private static readonly Dictionary<Projectile, byte> retrailedCopies = new Dictionary<Projectile, byte>();

        // A pooled bullet that was a copy is now one of ours (or another copy): take off everything we put on it.
        public static void Reset(Projectile p)
        {
            if (p == null)
                return;
            bool touched = tintedCopies.Contains(p) || repicturedCopies.ContainsKey(p) || recolouredCopies.ContainsKey(p)
                || retrailedCopies.ContainsKey(p) || addedParts.ContainsKey(p) || changedBounces.ContainsKey(p) || changedPierces.ContainsKey(p);
            if (touched)
            {
                BulletLook plain = new BulletLook();
                plain.ApplyQuietly(p);
            }
        }

        // How many copied bullets arrived with each kind of look (logged now and then, to check it works).
        private static readonly int[] seen = new int[8];
        private static int copies;
        private static float nextReportAt;

        public static void Report()
        {
            if (Time.realtimeSinceStartup < nextReportAt || copies == 0 && EffectSync.Sent + EffectSync.Applied == 0)
                return;
            nextReportAt = Time.realtimeSinceStartup + 60f;
            Plugin.Log.LogInfo("Copied bullets in the last minute: " + copies + " (tint " + seen[0] + ", picture " + seen[1] + ", colour "
                + seen[2] + ", trails " + seen[3] + ", bounces " + seen[4] + ", piercing " + seen[5] + ", chain lightning " + seen[6]
                + "; circling " + BulletMotion.Orbits + " (no body to circle " + BulletMotion.OrbitsWithoutBody + "), spiral " + BulletMotion.Helixes + ")");
            BulletMotion.Orbits = BulletMotion.Helixes = BulletMotion.OrbitsWithoutBody = 0;
            Plugin.Log.LogInfo("Enemy status effects in the last minute: sent " + EffectSync.Sent + ", copied from others " + EffectSync.Applied
                + ", not found here " + EffectSync.NotFound);
            EffectSync.Sent = EffectSync.Applied = EffectSync.NotFound = 0;
            copies = 0;
            for (int i = 0; i < seen.Length; i++)
                seen[i] = 0;
        }

        public void Apply(Projectile p)
        {
            if (p == null)
                return;
            copies++;
            for (int i = 0; i < 7; i++)
            {
                if ((flags & (1 << i)) != 0)
                    seen[i]++;
            }
            ApplyQuietly(p);
        }

        private void ApplyQuietly(Projectile p)
        {
            if (tintedCopies.Count > 400) tintedCopies.Clear();
            if (repicturedCopies.Count > 400) repicturedCopies.Clear();
            if (recolouredCopies.Count > 400) recolouredCopies.Clear();
            if (retrailedCopies.Count > 400) retrailedCopies.Clear();
            try
            {
                // The game's own tint step only works on a bullet with an owner (copies have none): use the
                // shader change it ends with directly.
                if ((flags & TintFlag) != 0)
                {
                    p.ChangeTintColorShader(0f, tint);
                    tintedCopies.Add(p);
                }
                else if (tintedCopies.Remove(p))
                    p.ChangeTintColorShader(0f, Color.clear);

                tk2dBaseSprite s = p.sprite;
                if (s != null)
                {
                    KeyValuePair<tk2dSpriteCollectionData, int> before;
                    if ((flags & SpriteFlag) != 0)
                    {
                        tk2dSpriteCollectionData coll = FindCollection(collection);
                        if (coll != null && spriteId >= 0 && spriteId < coll.Count)
                        {
                            if (!repicturedCopies.ContainsKey(p))
                                repicturedCopies[p] = new KeyValuePair<tk2dSpriteCollectionData, int>(s.Collection, s.spriteId);
                            s.SetSprite(coll, spriteId);
                        }
                    }
                    else if (repicturedCopies.TryGetValue(p, out before))
                    {
                        repicturedCopies.Remove(p);
                        if (before.Key != null)
                            s.SetSprite(before.Key, before.Value);
                    }

                    Color normal;
                    if ((flags & ColorFlag) != 0)
                    {
                        if (!recolouredCopies.ContainsKey(p))
                            recolouredCopies[p] = s.color;
                        s.color = spriteColor;
                    }
                    else if (recolouredCopies.TryGetValue(p, out normal))
                    {
                        recolouredCopies.Remove(p);
                        s.color = normal;
                    }
                }

                ApplyParts(p);

                byte normalTrails;
                if ((flags & TrailFlag) != 0)
                {
                    if (!retrailedCopies.ContainsKey(p))
                        retrailedCopies[p] = TrailBits(p);
                    SetTrails(p, trails);
                }
                else if (retrailedCopies.TryGetValue(p, out normalTrails))
                {
                    retrailedCopies.Remove(p);
                    SetTrails(p, normalTrails);
                }
            }
            catch (System.Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    Plugin.Log.LogWarning("Couldn't copy a bullet's look: " + e.Message);
                }
            }
        }
        private static bool warned;

        // ---- Bounces, piercing, chain lightning ----
        // Copies are pooled: parts we add are removed again before the bullet is reused, and values we change on
        // parts the bullet already had are put back.
        private static readonly Dictionary<Projectile, List<Component>> addedParts = new Dictionary<Projectile, List<Component>>();
        private static readonly Dictionary<Projectile, int> changedBounces = new Dictionary<Projectile, int>();
        private static readonly Dictionary<Projectile, int> changedPierces = new Dictionary<Projectile, int>();

        private void ApplyParts(Projectile p)
        {
            if (addedParts.Count > 400) addedParts.Clear();
            if (changedBounces.Count > 400) changedBounces.Clear();
            if (changedPierces.Count > 400) changedPierces.Clear();
            List<Component> added;
            if (addedParts.TryGetValue(p, out added))
            {
                addedParts.Remove(p);
                foreach (Component c in added)
                {
                    if (c != null)
                        Object.DestroyImmediate(c);
                }
            }
            int original;
            BounceProjModifier bounce = p.GetComponent<BounceProjModifier>();
            if (bounce != null && changedBounces.TryGetValue(p, out original))
            {
                bounce.numberOfBounces = original;
                changedBounces.Remove(p);
            }
            PierceProjModifier pierce = p.GetComponent<PierceProjModifier>();
            if (pierce != null && changedPierces.TryGetValue(p, out original))
            {
                pierce.penetration = original;
                changedPierces.Remove(p);
            }

            added = null;
            if ((flags & BounceFlag) != 0)
            {
                if (bounce == null)
                {
                    bounce = p.gameObject.AddComponent<BounceProjModifier>();
                    (added = added ?? new List<Component>()).Add(bounce);
                }
                else
                    changedBounces[p] = bounce.numberOfBounces;
                bounce.numberOfBounces = bounces;
            }
            if ((flags & PierceFlag) != 0)
            {
                if (pierce == null)
                {
                    pierce = p.gameObject.AddComponent<PierceProjModifier>();
                    (added = added ?? new List<Component>()).Add(pierce);
                }
                else
                    changedPierces[p] = pierce.penetration;
                pierce.penetration = pierces;
            }
            if ((flags & ChainFlag) != 0)
            {
                GameObject vfx = FindChainVfx(chainVfx);
                if (vfx != null && p.GetComponent<ChainLightningModifier>() == null)
                {
                    ChainLightningModifier chain = p.gameObject.AddComponent<ChainLightningModifier>();
                    chain.LinkVFXPrefab = vfx;
                    chain.maximumLinkDistance = chainDistance;
                    chain.CanChainToAnyProjectile = chainAny;
                    // Only the picture: the real arcs' damage happens in the shooter's game.
                    chain.DamagesEnemies = false;
                    chain.DamagesPlayers = false;
                    chain.damagePerHit = 0f;
                    (added = added ?? new List<Component>()).Add(chain);
                }
            }
            if (added != null)
                addedParts[p] = added;
        }

        // Chain lightning pictures by name, from the items and guns that make them.
        private static Dictionary<string, GameObject> chainVfxByName;

        private static GameObject FindChainVfx(string name)
        {
            if (chainVfxByName == null)
            {
                chainVfxByName = new Dictionary<string, GameObject>();
                foreach (ComplexProjectileModifier m in Resources.FindObjectsOfTypeAll<ComplexProjectileModifier>())
                {
                    if (m != null && m.ChainLightningVFX != null && !chainVfxByName.ContainsKey(m.ChainLightningVFX.name))
                        chainVfxByName[m.ChainLightningVFX.name] = m.ChainLightningVFX;
                }
                foreach (ChainLightningModifier c in Resources.FindObjectsOfTypeAll<ChainLightningModifier>())
                {
                    if (c != null && c.LinkVFXPrefab != null && !chainVfxByName.ContainsKey(c.LinkVFXPrefab.name))
                        chainVfxByName[c.LinkVFXPrefab.name] = c.LinkVFXPrefab;
                }
            }
            GameObject found;
            if (!chainVfxByName.TryGetValue(name, out found))
            {
                // Something new (another mod's item): look again next time.
                chainVfxByName = null;
                return null;
            }
            return found;
        }

        private static void SetTrails(Projectile p, byte bits)
        {
            if (p.TrailRenderer != null)
                p.TrailRenderer.enabled = (bits & 1) != 0;
            if (p.CustomTrailRenderer != null)
                p.CustomTrailRenderer.enabled = (bits & 2) != 0;
            if (p.ParticleTrail != null)
                p.ParticleTrail.gameObject.SetActive((bits & 4) != 0);
        }

        // Sprite sheets by name (looked up once each; items and mods can use any sheet).
        private static readonly Dictionary<string, tk2dSpriteCollectionData> collections = new Dictionary<string, tk2dSpriteCollectionData>();

        private static tk2dSpriteCollectionData FindCollection(string name)
        {
            tk2dSpriteCollectionData found;
            if (collections.TryGetValue(name, out found) && found != null)
                return found;
            found = null;
            foreach (tk2dSpriteCollectionData c in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>())
            {
                if (c != null && c.name == name)
                {
                    found = c;
                    break;
                }
            }
            collections[name] = found;
            return found;
        }
    }
}
