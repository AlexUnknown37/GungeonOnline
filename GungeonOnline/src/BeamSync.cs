using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // Beam weapons (Wood Beam, lasers...): beams aren't single bullets, so the partner never saw them.
    // While our gun has beams going we send each one's kind (its projectile), start point and direction 15x a second;
    // the partner's game shows the same beam there. It's only a picture there: it hurts nothing (the damage happens in
    // our game). When we stop sending, the partner's beam stops.
    public static class BeamSync
    {
        private const float SendInterval = 1f / 15f;

        private static NetSession session;
        private static readonly System.Reflection.FieldInfo moduleDataField = HarmonyLib.AccessTools.Field(typeof(Gun), "m_moduleData");
        private static float nextSendAt;
        private static int lastSentCount;

        private class Shown
        {
            public string Prefab;
            public BeamController Beam;
            public Vector2 Origin, Direction;
            public float LastHeard;
        }
        // Each other player's beams, by slot.
        private static readonly List<Shown>[] shownBy = NewLists();

        private static List<Shown>[] NewLists()
        {
            List<Shown>[] lists = new List<Shown>[Protocol.MaxPlayers];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<Shown>();
            return lists;
        }

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static List<BeamController> OurBeams(PlayerController me)
        {
            List<BeamController> beams = new List<BeamController>();
            Gun gun = me.CurrentGun;
            if (gun == null || moduleDataField == null)
                return beams;
            System.Collections.IDictionary data = moduleDataField.GetValue(gun) as System.Collections.IDictionary;
            if (data == null)
                return beams;
            foreach (object v in data.Values)
            {
                ModuleShootData d = v as ModuleShootData;
                if (d != null && d.beam != null && d.beam.isActiveAndEnabled)
                    beams.Add(d.beam);
            }
            return beams;
        }

        public static void Update()
        {
            if (session == null || session.Status != NetSession.State.Connected)
            {
                foreach (List<Shown> list in shownBy)
                    Clear(list);
                return;
            }
            // Keep the shown beams where their player said, every frame (they're drawn from their start point).
            float now = Time.realtimeSinceStartup;
            foreach (List<Shown> shown in shownBy)
            for (int i = shown.Count - 1; i >= 0; i--)
            {
                Shown s = shown[i];
                if (s.Beam == null || now - s.LastHeard > 0.3f)
                {
                    Stop(s);
                    shown.RemoveAt(i);
                    continue;
                }
                s.Beam.Origin = s.Origin;
                s.Beam.Direction = s.Direction;
                s.Beam.LateUpdatePosition(new Vector3(s.Origin.x, s.Origin.y, 0f));
            }

            if (now < nextSendAt)
                return;
            nextSendAt = now + SendInterval;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null)
                return;
            List<BeamController> beams = OurBeams(me);
            if (beams.Count == 0 && lastSentCount == 0)
                return;
            lastSentCount = beams.Count;
            string level = LevelSync.CurrentLevelKey();
            session.SendUnreliable(MsgType.Beams, delegate (BinaryWriter w)
            {
                w.Write(level);
                w.Write((byte)Mathf.Min(beams.Count, 8));
                for (int i = 0; i < beams.Count && i < 8; i++)
                {
                    BeamController b = beams[i];
                    w.Write(b.gameObject.name.Replace("(Clone)", "").Trim());
                    w.Write(b.Origin.x); w.Write(b.Origin.y);
                    w.Write(b.Direction.x); w.Write(b.Direction.y);
                }
            });
        }

        public static void OnBeams(BinaryReader r)
        {
            string level = r.ReadString();
            int n = r.ReadByte();
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            bool here = me != null && level == LevelSync.CurrentLevelKey() && !GameManager.Instance.IsLoadingLevel;
            float now = Time.realtimeSinceStartup;
            List<Shown> shown = shownBy[Mathf.Clamp(session.From, 0, shownBy.Length - 1)];
            for (int i = 0; i < n; i++)
            {
                string prefabName = r.ReadString();
                Vector2 origin = new Vector2(r.ReadSingle(), r.ReadSingle());
                Vector2 dir = new Vector2(r.ReadSingle(), r.ReadSingle());
                if (!here)
                    continue;
                Shown s = i < shown.Count ? shown[i] : null;
                if (s != null && s.Prefab != prefabName)
                {
                    Stop(s);
                    shown.RemoveAt(i);
                    s = null;
                }
                if (s == null)
                {
                    BeamController beam = Start(prefabName, origin, dir, me);
                    if (beam == null)
                        continue;
                    s = new Shown();
                    s.Prefab = prefabName;
                    s.Beam = beam;
                    shown.Insert(Mathf.Min(i, shown.Count), s);
                }
                s.Origin = origin;
                s.Direction = dir;
                s.LastHeard = now;
            }
            // Fewer beams than before: the extra ones stop.
            for (int i = shown.Count - 1; i >= n; i--)
            {
                Stop(shown[i]);
                shown.RemoveAt(i);
            }
        }

        // The same way the game starts a beam (BeamController.FreeFireBeam), but harmless.
        private static BeamController Start(string prefabName, Vector2 origin, Vector2 dir, PlayerController me)
        {
            GameObject prefab = BulletSync.FindPrefab(prefabName);
            if (prefab == null || prefab.GetComponent<BeamController>() == null)
                return null;
            try
            {
                GameObject go = SpawnManager.SpawnProjectile(prefab, new Vector3(origin.x, origin.y, 0f), Quaternion.identity, true);
                Projectile p = go != null ? go.GetComponent<Projectile>() : null;
                BeamController beam = go != null ? go.GetComponent<BeamController>() : null;
                if (p == null || beam == null)
                    return null;
                p.Owner = me;
                p.baseData.damage = 0f;
                p.collidesWithEnemies = false;
                p.collidesWithPlayer = false;
                beam.Owner = me;
                beam.HitsPlayers = false;
                beam.HitsEnemies = false;
                beam.DamageModifier = 0f;
                beam.Direction = dir;
                beam.Origin = origin;
                return beam;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't show the partner's beam " + prefabName + ": " + e.Message);
                return null;
            }
        }

        private static void Stop(Shown s)
        {
            if (s == null || s.Beam == null)
                return;
            try
            {
                s.Beam.CeaseAttack();
            }
            catch
            {
                Object.Destroy(s.Beam.gameObject);
            }
        }

        private static void Clear(List<Shown> shown)
        {
            foreach (Shown s in shown)
                Stop(s);
            shown.Clear();
        }
    }
}
