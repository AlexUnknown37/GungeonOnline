using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Bullets that circle a player (Orbital Bullets and orbit synergies) or fly in a spiral (helix): the game moves
    // them with a "motion module". Copies used to fly straight. The module's settings are sent - with the shot if the
    // bullet starts that way, or as soon as an item switches a flying bullet over - and the copy gets the same module.
    // A copied orbiting bullet circles the shooter's invisible body in our game (TargetSync), which follows them.
    public static class BulletMotion
    {
        public const byte None = 0, Orbit = 1, Helix = 2;
        public static int Orbits, Helixes, OrbitsWithoutBody; // copies given a module (for the minute report)

        public static byte KindOf(Projectile p)
        {
            if (p == null || p.OverrideMotionModule == null)
                return None;
            if (p.OverrideMotionModule is OrbitProjectileMotionModule)
                return Orbit;
            if (p.OverrideMotionModule is HelixProjectileMotionModule)
                return Helix;
            return None;
        }

        public static void Write(BinaryWriter w, Projectile p)
        {
            byte kind = KindOf(p);
            w.Write(kind);
            OrbitProjectileMotionModule orbit = kind == Orbit ? (OrbitProjectileMotionModule)p.OverrideMotionModule : null;
            HelixProjectileMotionModule helix = kind == Helix ? (HelixProjectileMotionModule)p.OverrideMotionModule : null;
            if (orbit != null)
            {
                w.Write(orbit.MinRadius);
                w.Write(orbit.MaxRadius);
                w.Write(orbit.lifespan);
                w.Write(orbit.ForceInvert);
                w.Write(orbit.OrbitGroup);
            }
            else if (helix != null)
            {
                w.Write(helix.helixWavelength);
                w.Write(helix.helixAmplitude);
                w.Write(helix.ForceInvert);
            }
        }

        // Read what Write wrote and put it on the copy (shooter: the slot whose body an orbit circles).
        public static void ReadAndApply(BinaryReader r, Projectile copy, int shooter)
        {
            byte kind = r.ReadByte();
            if (kind == Orbit)
            {
                float min = r.ReadSingle(), max = r.ReadSingle(), life = r.ReadSingle();
                bool invert = r.ReadBoolean();
                int group = r.ReadInt32();
                SpeculativeRigidbody center = TargetSync.BodyOf(shooter);
                if (copy != null && center == null)
                    OrbitsWithoutBody++;
                if (copy == null || center == null)
                    return;
                Orbits++;
                OrbitProjectileMotionModule m = new OrbitProjectileMotionModule();
                m.MinRadius = min;
                m.MaxRadius = max;
                m.lifespan = life;
                m.ForceInvert = invert;
                m.OrbitGroup = group;
                m.usesAlternateOrbitTarget = true;
                m.alternateOrbitTarget = center;
                copy.OverrideMotionModule = m;
                moved.Add(copy);
            }
            else if (kind == Helix)
            {
                float wavelength = r.ReadSingle(), amplitude = r.ReadSingle();
                bool invert = r.ReadBoolean();
                if (copy == null)
                    return;
                Helixes++;
                HelixProjectileMotionModule m = new HelixProjectileMotionModule();
                m.helixWavelength = wavelength;
                m.helixAmplitude = amplitude;
                m.ForceInvert = invert;
                copy.OverrideMotionModule = m;
                moved.Add(copy);
            }
            else if (copy != null && moved.Remove(copy))
            {
                copy.OverrideMotionModule = null; // a pooled copy that circled before
            }
        }

        // Copies we gave a motion module (pooled bullets get reused).
        private static readonly System.Collections.Generic.HashSet<Projectile> moved = new System.Collections.Generic.HashSet<Projectile>();

        public static void Reset(Projectile p)
        {
            if (moved.Count > 400)
                moved.Clear();
            if (p != null && moved.Remove(p))
                p.OverrideMotionModule = null;
        }
    }
}
