using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Test copies only (the I key): give each player a different set of items, companions, orbitals and guns, then
    // let the bot tour its guns, so the other players' screens show many items, synergies and weapons to compare.
    // Each player's set is fixed (by player number) plus a few random extras.
    public static class TestLoadout
    {
        private static readonly string[][] Items =
        {
            new string[] { "Glass Guon Stone", "Orbital Bullets", "Ser Junkan", "Hot Lead", "Drill" },
            new string[] { "Baby Good Mimic", "Blue Guon Stone", "Homing Bullets", "Irradiated Lead", "Bouncy Bullets" },
            new string[] { "Pig", "Green Guon Stone", "Ghost Bullets", "Frost Bullets", "Scattershot" },
            new string[] { "Super Space Turtle", "Orange Guon Stone", "Shock Rounds", "Charming Rounds", "Heavy Bullets" },
        };
        private static readonly string[][] Guns =
        {
            new string[] { "Casey", "Raiden Coil", "Shotgun Full of Hate", "Mailbox" },
            new string[] { "Crossbow", "Laser Rifle", "Hexagun", "Fightsabre" },
            new string[] { "Gunbow", "Bee Hive", "Big Iron", "Snakemaker" },
            new string[] { "Sniper Rifle", "Thunderclap", "Unicorn", "Mega Douser" },
        };

        // Once per character: pressing the key again (it got pressed 26 times once) stacked every item that many
        // times, which buried the screen in effects.
        private static PlayerController givenTo;

        public static void Give(PlayerController me, int slot)
        {
            if (me == null || PickupObjectDatabase.Instance == null)
                return;
            if (me == givenTo)
            {
                Plugin.Log.LogInfo("Test loadout: already given to this character.");
                return;
            }
            givenTo = me;
            int s = Mathf.Clamp(slot, 0, Items.Length - 1);
            List<string> given = new List<string>();
            List<string> missing = new List<string>();
            foreach (string name in Items[s])
                GiveNamed(me, name, given, missing);
            foreach (string name in Guns[s])
                GiveNamed(me, name, given, missing);
            // A few random extras (the same per player number, so a rerun gives the same set).
            System.Random rng = new System.Random(1000 + s);
            List<PickupObject> all = PickupObjectDatabase.Instance.Objects;
            int extras = 0;
            for (int tries = 0; tries < 300 && extras < 3; tries++)
            {
                PickupObject p = all[rng.Next(all.Count)];
                if (p == null || p.quality == PickupObject.ItemQuality.EXCLUDED || p.quality == PickupObject.ItemQuality.SPECIAL
                    || !(p is PassiveItem || p is Gun))
                    continue;
                if (Give(me, p))
                {
                    given.Add(p.EncounterNameOrDisplayName + " (random)");
                    extras++;
                }
            }
            Plugin.Log.LogInfo("Test loadout for player " + (slot + 1) + ": " + string.Join(", ", given.ToArray())
                + (missing.Count > 0 ? ". Not found: " + string.Join(", ", missing.ToArray()) : ""));
            Autopilot.GunTour = true;
            logSynergiesAt = Time.realtimeSinceStartup + 3f;
        }

        private static void GiveNamed(PlayerController me, string name, List<string> given, List<string> missing)
        {
            PickupObject found = Find(name);
            if (found != null && Give(me, found))
                given.Add(found.EncounterNameOrDisplayName);
            else
                missing.Add(name);
        }

        private static PickupObject Find(string name)
        {
            string want = name.ToLowerInvariant();
            PickupObject partial = null;
            foreach (PickupObject p in PickupObjectDatabase.Instance.Objects)
            {
                if (p == null)
                    continue;
                string n;
                try { n = (p.EncounterNameOrDisplayName ?? "").ToLowerInvariant(); }
                catch (System.Exception) { continue; }
                if (n == want)
                    return p;
                if (partial == null && n.Contains(want))
                    partial = p;
            }
            return partial;
        }

        private static bool Give(PlayerController me, PickupObject p)
        {
            try
            {
                Gun gun = p as Gun;
                if (gun != null)
                {
                    me.inventory.AddGunToInventory(gun, false);
                    return true;
                }
                return LootEngine.TryGivePrefabToPlayer(p.gameObject, me, true);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Test loadout: couldn't give " + p.name + ": " + e.Message);
                return false;
            }
        }

        // Which synergies came on (logged once after the loadout, then whenever they change).
        private static float logSynergiesAt = -1f;
        private static string lastSynergies = "";

        public static void Update()
        {
            if (logSynergiesAt < 0f || Time.realtimeSinceStartup < logSynergiesAt || !GameManager.HasInstance)
                return;
            logSynergiesAt = Time.realtimeSinceStartup + 10f;
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            AdvancedSynergyDatabase db = GameManager.Instance.SynergyManager;
            if (me == null || db == null || db.synergies == null)
                return;
            List<string> names = new List<string>();
            foreach (int i in me.ActiveExtraSynergies)
            {
                if (i >= 0 && i < db.synergies.Length && db.synergies[i] != null)
                    names.Add(db.synergies[i].NameKey);
            }
            string now = string.Join(", ", names.ToArray());
            if (now == lastSynergies)
                return;
            lastSynergies = now;
            Plugin.Log.LogInfo("Test: active synergies (" + names.Count + "): " + (names.Count > 0 ? now : "none")
                + "; gun " + (me.CurrentGun != null ? me.CurrentGun.EncounterNameOrDisplayName : "-"));
        }
    }
}
