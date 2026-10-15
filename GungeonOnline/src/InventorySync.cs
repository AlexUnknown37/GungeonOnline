using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // The host remembers the joiner's character and items, so a joiner who drops out (crash, disconnect, game
    // restart) and joins the same run again gets everything back.
    // - Joiner: sends a small list of what it carries whenever it changes (checked every 2 s): guns with ammo,
    //   passive and active items, health, armor, blanks.
    // - Host: keeps the latest list per partner name, tagged with the run's seed.
    // - Joiner starting on the host's run again (not via Continue, which restores its own save): asks the host;
    //   if the host has a list for this partner and this run, the joiner gets those items given back.
    public static class InventorySync
    {
        private const float CheckInterval = 2f;

        private class Snapshot
        {
            public int Seed;
            public List<int> GunIds = new List<int>();
            public List<int> GunAmmo = new List<int>();
            public List<int> Passives = new List<int>();
            public List<int> Actives = new List<int>();
            public float Health, MaxHealth, Armor;
            public int Blanks;

            public void Write(BinaryWriter w)
            {
                w.Write(Seed);
                w.Write(GunIds.Count);
                for (int i = 0; i < GunIds.Count; i++)
                {
                    w.Write(GunIds[i]);
                    w.Write(GunAmmo[i]);
                }
                WriteList(w, Passives);
                WriteList(w, Actives);
                w.Write(Health); w.Write(MaxHealth); w.Write(Armor);
                w.Write(Blanks);
            }

            public static Snapshot Read(BinaryReader r)
            {
                Snapshot s = new Snapshot();
                s.Seed = r.ReadInt32();
                int guns = r.ReadInt32();
                for (int i = 0; i < guns; i++)
                {
                    s.GunIds.Add(r.ReadInt32());
                    s.GunAmmo.Add(r.ReadInt32());
                }
                s.Passives = ReadList(r);
                s.Actives = ReadList(r);
                s.Health = r.ReadSingle(); s.MaxHealth = r.ReadSingle(); s.Armor = r.ReadSingle();
                s.Blanks = r.ReadInt32();
                return s;
            }

            public string Key()
            {
                MemoryStream m = new MemoryStream();
                BinaryWriter w = new BinaryWriter(m);
                Write(w);
                w.Flush();
                return System.Convert.ToBase64String(m.ToArray());
            }
        }

        private static void WriteList(BinaryWriter w, List<int> list)
        {
            w.Write(list.Count);
            foreach (int v in list)
                w.Write(v);
        }

        private static List<int> ReadList(BinaryReader r)
        {
            int n = r.ReadInt32();
            List<int> list = new List<int>();
            for (int i = 0; i < n && i < 1000; i++)
                list.Add(r.ReadInt32());
            return list;
        }

        private static NetSession session;
        private static float nextCheckAt;
        private static string lastSentKey = "";
        // Host: latest list per partner name.
        private static readonly Dictionary<string, Snapshot> remembered = new Dictionary<string, Snapshot>();
        // Joiner: we just started on the host's run (fresh character): ask for our items once we're in.
        private static bool askWhenReady;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected && session.Link != null; }
        }

        // LevelSync: the joiner started a run onto the host's floor. savedRun = it came from Continue.
        public static void OnJoinedHostRun(bool savedRun)
        {
            askWhenReady = !savedRun;
            lastSentKey = ""; // our list starts over with the new character
        }

        private static Snapshot Take(PlayerController me)
        {
            Snapshot s = new Snapshot();
            s.Seed = GameManager.Instance.CurrentRunSeed;
            if (me.inventory != null)
            {
                foreach (Gun g in me.inventory.AllGuns)
                {
                    if (g == null)
                        continue;
                    s.GunIds.Add(g.PickupObjectId);
                    s.GunAmmo.Add(g.CurrentAmmo);
                }
            }
            foreach (PassiveItem p in me.passiveItems)
            {
                if (p != null)
                    s.Passives.Add(p.PickupObjectId);
            }
            foreach (PlayerItem a in me.activeItems)
            {
                if (a != null)
                    s.Actives.Add(a.PickupObjectId);
            }
            s.Health = me.healthHaver.GetCurrentHealth();
            s.MaxHealth = me.healthHaver.GetMaxHealth();
            s.Armor = me.healthHaver.Armor;
            s.Blanks = me.Blanks;
            return s;
        }

        public static void Update()
        {
            if (!Connected || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            PlayerController me = gm.PrimaryPlayer;
            bool inRun = me != null && !gm.IsFoyer && !gm.IsLoadingLevel && gm.Dungeon != null && gm.CurrentRunSeed != 0;
            if (session.Link.IsHost || !inRun)
                return;

            if (askWhenReady)
            {
                askWhenReady = false;
                int seed = gm.CurrentRunSeed;
                session.SendReliable(MsgType.InventoryRequest, delegate (BinaryWriter w) { w.Write(seed); });
            }

            // Joiner: tell the host what we carry (only when it changed).
            if (Time.realtimeSinceStartup < nextCheckAt || me.IsGhost || me.healthHaver.IsDead)
                return;
            nextCheckAt = Time.realtimeSinceStartup + CheckInterval;
            Snapshot s = Take(me);
            string key = s.Key();
            if (key == lastSentKey)
                return;
            lastSentKey = key;
            session.SendReliable(MsgType.Inventory, delegate (BinaryWriter w) { s.Write(w); });
        }

        // Host: remember the joiner's latest list.
        public static void OnInventory(BinaryReader r)
        {
            Snapshot s = Snapshot.Read(r);
            remembered[session.PeerName ?? ""] = s;
        }

        // Host: the joiner is back on our run and asks for its items.
        public static void OnInventoryRequest(BinaryReader r)
        {
            int seed = r.ReadInt32();
            Snapshot s;
            if (!remembered.TryGetValue(session.PeerName ?? "", out s) || s.Seed != seed || !GameManager.HasInstance
                || GameManager.Instance.CurrentRunSeed != seed)
            {
                return;
            }
            Plugin.Log.LogInfo(session.PeerName + " rejoined our run: sending back their items.");
            session.SendReliable(MsgType.InventoryRestore, delegate (BinaryWriter w) { s.Write(w); });
        }

        // Joiner: get our items back.
        public static void OnInventoryRestore(BinaryReader r)
        {
            Snapshot s = Snapshot.Read(r);
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || GameManager.Instance.CurrentRunSeed != s.Seed)
                return;
            int given = 0;
            for (int i = 0; i < s.GunIds.Count; i++)
            {
                Gun owned = null;
                foreach (Gun g in me.inventory.AllGuns)
                {
                    if (g != null && g.PickupObjectId == s.GunIds[i])
                        owned = g;
                }
                if (owned == null)
                {
                    Gun prefab = PickupObjectDatabase.GetById(s.GunIds[i]) as Gun;
                    if (prefab == null)
                        continue;
                    owned = me.inventory.AddGunToInventory(prefab, false);
                    given++;
                }
                if (owned != null)
                    owned.CurrentAmmo = s.GunAmmo[i];
            }
            given += GiveMissing(me, s.Passives);
            given += GiveMissing(me, s.Actives);
            if (s.MaxHealth > 0f)
            {
                me.healthHaver.SetHealthMaximum(s.MaxHealth, null, false);
                me.healthHaver.ForceSetCurrentHealth(Mathf.Max(0.5f, s.Health));
            }
            me.healthHaver.Armor = s.Armor;
            me.Blanks = s.Blanks;
            Plugin.Log.LogInfo("The host remembered our items: got " + given + " back.");
        }

        // Items we had that this character doesn't have yet (a passive picked up twice is given twice).
        private static int GiveMissing(PlayerController me, List<int> ids)
        {
            Dictionary<int, int> have = new Dictionary<int, int>();
            foreach (PassiveItem p in me.passiveItems)
                if (p != null) have[p.PickupObjectId] = (have.ContainsKey(p.PickupObjectId) ? have[p.PickupObjectId] : 0) + 1;
            foreach (PlayerItem a in me.activeItems)
                if (a != null) have[a.PickupObjectId] = (have.ContainsKey(a.PickupObjectId) ? have[a.PickupObjectId] : 0) + 1;
            int given = 0;
            foreach (int id in ids)
            {
                int n;
                if (have.TryGetValue(id, out n) && n > 0)
                {
                    have[id] = n - 1;
                    continue;
                }
                PickupObject prefab = PickupObjectDatabase.GetById(id);
                if (prefab == null)
                    continue;
                LootEngine.GivePrefabToPlayer(prefab.gameObject, me);
                given++;
            }
            return given;
        }
    }
}
