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
    // - Rejoining: when a joiner the host remembers connects while the host is in that run, the host offers their
    //   old character. The joiner picks it by itself on the character-select screen and goes straight into the run
    //   (no Breach), where the items come back. New joiners still start in the Breach.
    // - While the host's answer is pending, the joiner sends no lists (its fresh character must not overwrite the
    //   host's memory of the old one).
    public static class InventorySync
    {
        private const float CheckInterval = 2f;

        private class Snapshot
        {
            public int Seed;
            public string Character = "";   // player prefab name, e.g. "PlayerMarine" or "PlayerSlayer"
            public List<int> GunIds = new List<int>();
            public List<int> GunAmmo = new List<int>();
            public List<int> Passives = new List<int>();
            public List<int> Actives = new List<int>();
            public float Health, MaxHealth, Armor;
            public int Blanks;

            public void Write(BinaryWriter w)
            {
                w.Write(Seed);
                w.Write(Character ?? "");
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
                s.Character = r.ReadString();
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
        private static float answerPendingUntil = -1f;
        // Joiner: the host remembers us in its current run, with this character.
        private static string rejoinCharacter;
        private static float rejoinOfferedAt;
        private static bool rejoinPicked;
        private static float rejoinPickedAt = -100f;

        // Joiner: the character we play in the host's current run (to come back as it after visiting the Breach).
        private static int myRunSeed;
        private static string myRunCharacter;
        private static bool breachVisitHandled;

        public static bool HasRejoinOffer
        {
            get { return rejoinCharacter != null; }
        }

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
            s.Character = me.gameObject.name.Replace("(Clone)", "").Trim();
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
            FixStuckNoGun();
            GameManager gm = GameManager.Instance;
            PlayerController me = gm.PrimaryPlayer;
            bool inRun = me != null && !gm.IsFoyer && !gm.IsLoadingLevel && gm.Dungeon != null && gm.CurrentRunSeed != 0;
            if (session.Link.IsHost || !inRun)
                return;
            if (Time.realtimeSinceStartup >= answerPendingUntil && !me.IsGhost)
            {
                myRunSeed = gm.CurrentRunSeed;
                myRunCharacter = me.gameObject.name.Replace("(Clone)", "").Trim();
            }

            if (askWhenReady)
            {
                askWhenReady = false;
                answerPendingUntil = Time.realtimeSinceStartup + 6f;
                int seed = gm.CurrentRunSeed;
                session.SendReliable(MsgType.InventoryRequest, delegate (BinaryWriter w) { w.Write(seed); });
            }
            if (Time.realtimeSinceStartup < answerPendingUntil)
                return;

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

        // Host: a joiner connected. If we remember them in our current run, offer their old character.
        public static void OnConnected()
        {
            if (session == null || session.Link == null || !session.Link.IsHost || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            Snapshot s;
            if (gm.IsFoyer || gm.CurrentRunSeed == 0 || !remembered.TryGetValue(session.PeerName ?? "", out s) || s.Seed != gm.CurrentRunSeed)
                return;
            string character = s.Character;
            Plugin.Log.LogInfo(session.PeerName + " is back: offering their place in the run as " + character + ".");
            session.SendReliable(MsgType.RejoinOffer, delegate (BinaryWriter w) { w.Write(character); });
        }

        public static void OnRejoinOffer(BinaryReader r)
        {
            rejoinCharacter = r.ReadString();
            rejoinOfferedAt = Time.realtimeSinceStartup;
            rejoinPicked = false;
            Plugin.Log.LogInfo("The host remembers us in its run (" + rejoinCharacter + "): rejoining automatically.");
        }

        // Joiner: pick our old character on the character-select screen, then start the run (which joins the host's).
        public static void UpdateRejoin()
        {
            if (session == null || session.Link == null || session.Link.IsHost)
                return;
            // Back in the Breach while the host still plays the run we were in (we went back, or quit to the Breach):
            // we come back as the same character, like after a reconnect.
            GameManager g0 = GameManager.HasInstance ? GameManager.Instance : null;
            if (g0 != null && !g0.IsFoyer)
                breachVisitHandled = false;
            else if (g0 != null && !breachVisitHandled && rejoinCharacter == null && myRunCharacter != null
                && LevelSync.HostInRun && LevelSync.HostRunSeed == myRunSeed)
            {
                breachVisitHandled = true;
                rejoinCharacter = myRunCharacter;
                rejoinOfferedAt = Time.realtimeSinceStartup;
                rejoinPicked = false;
                Plugin.Log.LogInfo("Back in the Breach during the host's run: we'll rejoin as " + myRunCharacter + ".");
                Toast.Show("Rejoining the run as your character...", 4f);
            }
            if (rejoinCharacter == null)
                return;
            if (Time.realtimeSinceStartup - rejoinOfferedAt > 120f || !LevelSync.HostInRun)
            {
                rejoinCharacter = null;
                return;
            }
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null || !gm.IsFoyer || gm.IsLoadingLevel || Foyer.Instance == null)
                return;
            if (!rejoinPicked && gm.IsSelectingCharacter && Foyer.IsCurrentlyPlayingCharacterSelect == false)
            {
                foreach (FoyerCharacterSelectFlag flag in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                {
                    if (flag == null || string.IsNullOrEmpty(flag.CharacterPrefabPath))
                        continue;
                    string path = flag.CharacterPrefabPath;
                    int slash = path.LastIndexOf('/');
                    if (slash >= 0)
                        path = path.Substring(slash + 1);
                    if (!string.Equals(path, rejoinCharacter, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    rejoinPicked = true;
                    rejoinPickedAt = Time.realtimeSinceStartup;
                    Plugin.Log.LogInfo("Picking our old character " + rejoinCharacter + ".");
                    Foyer.Instance.StartCoroutine(Foyer.Instance.OnSelectedCharacter(0.25f, flag));
                    return;
                }
                rejoinPicked = true; // not found: keep whatever gets picked
                Plugin.Log.LogWarning("Couldn't find our old character " + rejoinCharacter + " to pick; pick any character.");
                return;
            }
            // Character chosen and arrived (the game finishes setting it up a moment after the pick): start the run
            // (it joins the host's floor).
            if (!gm.IsSelectingCharacter && gm.PrimaryPlayer != null && !Foyer.IsCurrentlyPlayingCharacterSelect
                && Time.realtimeSinceStartup - rejoinPickedAt > 3f)
            {
                string now = gm.PrimaryPlayer.gameObject.name.Replace("(Clone)", "").Trim();
                if (!string.Equals(now, rejoinCharacter, System.StringComparison.OrdinalIgnoreCase))
                    Plugin.Log.LogWarning("Rejoining as " + now + " instead of our old " + rejoinCharacter + " (it was picked before we could).");
                rejoinCharacter = null;
                Plugin.Log.LogInfo("Rejoining the host's run.");
                Toast.Show("Rejoining the run...", 3f);
                if (Pixelator.Instance != null)
                    Pixelator.Instance.FadeToBlack(0.5f, false, 0f);
                Foyer.Instance.OnDepartedFoyer();
                gm.DelayedLoadNextLevel(0.5f);
            }
        }

        // ---- Guns stuck away after a rejoin ----
        // In the Breach the game puts every character's gun away (ForceNoGun) and takes it out again when they leave.
        // Rejoining picked the character and left at once, before the new character had finished arriving: it put
        // its gun away after we'd left, and nothing took it out again. The player carried guns but held none (and
        // picked-up guns never showed either). If a gun stays put away for a few seconds in a run, take it out.
        private static float noGunSince = -1f;

        private static void FixStuckNoGun()
        {
            GameManager gm = GameManager.Instance;
            PlayerController me = gm.PrimaryPlayer;
            if (me == null || me.inventory == null || gm.IsFoyer || gm.IsLoadingLevel || !me.inventory.ForceNoGun
                || me.inventory.AllGuns.Count == 0)
            {
                noGunSince = -1f;
                return;
            }
            if (noGunSince < 0f)
                noGunSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - noGunSince < 3f)
                return;
            noGunSince = -1f;
            me.inventory.ForceNoGun = false;
            if (me.CurrentGun != null)
                me.CurrentGun.gameObject.SetActive(true);
            me.ProcessHandAttachment();
            Plugin.Log.LogWarning("Our gun was stuck put away (left over from the Breach): took it out. " + DescribeGuns(me));
        }

        // ---- For the stand-in (host) ----

        // The character we remember for this player in this run, or null.
        public static string RememberedCharacter(string name, int seed)
        {
            Snapshot s;
            if (name == null || !remembered.TryGetValue(name, out s) || s.Seed != seed || string.IsNullOrEmpty(s.Character))
                return null;
            return s.Character;
        }

        // Give a character the gear we remember for this player. Returns how many things were given.
        public static int ApplyRemembered(string name, PlayerController who)
        {
            Snapshot s;
            if (name == null || who == null || !remembered.TryGetValue(name, out s))
                return 0;
            return Apply(who, s);
        }

        // Remember this character's gear as the player's (their stand-in's latest, handed back when they return).
        public static void Remember(string name, PlayerController who)
        {
            if (name == null || who == null || !GameManager.HasInstance)
                return;
            remembered[name] = Take(who);
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
                // Nothing to give back: say so, so the joiner stops waiting.
                session.SendReliableTo(session.From, MsgType.InventoryRestore, delegate (BinaryWriter w) { w.Write(false); });
                return;
            }
            Vector2 at;
            bool hadStandIn = StandIn.TakeHandBack(session.PeerName ?? "", out at);
            Plugin.Log.LogInfo(session.PeerName + " rejoined our run: sending back their items" + (hadStandIn ? " (their stand-in's)." : "."));
            session.SendReliableTo(session.From, MsgType.InventoryRestore, delegate (BinaryWriter w)
            {
                w.Write(true);
                s.Write(w);
                w.Write(hadStandIn);
                w.Write(at.x); w.Write(at.y);
            });
        }

        // Joiner: get our items back.
        public static void OnInventoryRestore(BinaryReader r)
        {
            answerPendingUntil = -1f;
            rejoinCharacter = null;
            if (!r.ReadBoolean())
            {
                Plugin.Log.LogInfo("The host has no items saved for us in this run.");
                return;
            }
            Snapshot s = Snapshot.Read(r);
            bool hadStandIn = r.ReadBoolean();
            Vector2 standInAt = new Vector2(r.ReadSingle(), r.ReadSingle());
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || GameManager.Instance.CurrentRunSeed != s.Seed)
                return;
            int got = Apply(me, s);
            Plugin.Log.LogInfo("The host remembered our items: got " + got + " back. " + DescribeGuns(me));
            if (hadStandIn)
            {
                // A bot played our character while we were gone: pick up where it is, still playing, until we move.
                if (Vector2.Distance(me.transform.position, standInAt) > 1f)
                    me.WarpToPoint(standInAt, false, false);
                Autopilot.Start(false);
                Toast.Show("Welcome back! Your bot is still playing - move to take over.", 6f);
            }
            else
                Toast.Show("Welcome back! Your items and health were restored.", 5f);
        }

        // For the log: the guns a player has and can use right now.
        public static string DescribeGuns(PlayerController me)
        {
            if (me == null || me.inventory == null)
                return "No inventory.";
            string list = "";
            int broken = 0;
            foreach (Gun g in me.inventory.AllGuns)
            {
                if (g == null) { broken++; continue; }
                list += (list.Length > 0 ? ", " : "") + g.EncounterNameOrDisplayName + " " + g.CurrentAmmo + "/" + g.AdjustedMaxAmmo;
            }
            return "Guns: " + (list.Length > 0 ? list : "none") + (broken > 0 ? " (+" + broken + " missing)" : "")
                + "; holding " + (me.CurrentGun != null ? me.CurrentGun.EncounterNameOrDisplayName : "nothing")
                + (me.inventory.GunLocked.Value ? "; guns locked" : "") + (me.IsGhost ? "; ghost" : "")
                + "; input " + me.CurrentInputState + ".";
        }

        // Give a character a remembered list of gear (guns with ammo, items, health, armor, blanks).
        private static int Apply(PlayerController me, Snapshot s)
        {
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
            return given;
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
