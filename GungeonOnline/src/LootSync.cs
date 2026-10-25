using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Loot and money between the two games.
    // - Chests: one player opening a chest opens it in both games. Each game rolls its OWN contents (the
    //   partner's copy is re-rolled), so players don't get the same item unless the dice say so. A partner
    //   who is in another room is brought to the chest, so their loot isn't left behind for the thief rat.
    // - Master Round: if either living player beat the boss without getting hit, both get it.
    // - Dropped guns appear in both games; whoever picks one up takes it away from the other game.
    // - Shop: an item bought (or stolen) by one player is sold out for the other.
    // - Money and keys are one shared purse: every change on one side is applied on the other.
    // - Thief rat: while you're a ghost it still takes items left behind, as if you were alive, so you can
    //   win them back from the rat later.
    public static class LootSync
    {
        private static NetSession session;
        private static readonly FieldInfo chestMimicField = AccessTools.Field(typeof(Chest), "m_isMimic");
        private static readonly FieldInfo currentHealthField = AccessTools.Field(typeof(HealthHaver), "currentHealth");

        // Set while we act on a message from the partner, so it isn't sent back.
        private static bool applyingRemote;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(LootSync));
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static PlayerController Me
        {
            get { return GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null; }
        }

        // Is another player on our floor?
        private static bool PartnerHere
        {
            get
            {
                string level = LevelSync.CurrentLevelKey();
                foreach (RemotePlayerState r in session.Remotes)
                {
                    if (r.HasPlayer && r.Scene == level)
                        return true;
                }
                return false;
            }
        }

        private static RoomHandler RoomAt(Vector2 pos)
        {
            if (!GameManager.HasInstance || GameManager.Instance.Dungeon == null)
                return null;
            return GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(new IntVector2(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y)));
        }

        // A new floor: forget everything floor-specific.
        private static Dungeon floor;

        private static void CheckFloor()
        {
            Dungeon d = GameManager.HasInstance ? GameManager.Instance.Dungeon : null;
            if (d == floor)
                return;
            floor = d;
            partnerHitRooms.Clear();
            sentHitRooms.Clear();
            guns.Clear();
        }

        // ---- Chests ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Chest), "Open")]
        private static void AfterChestOpen(Chest __instance, PlayerController player)
        {
            if (applyingRemote || player == null || player != Me || __instance == DeathSync.ReviveChest)
                return;
            StandInShare(__instance); // also when nobody else is connected any more
            if (!Connected)
                return;
            Vector2 pos = __instance.transform.position;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.ChestOpened, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnChestOpened(BinaryReader r)
        {
            string level = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            PlayerController me = Me;
            if (me == null || level != LevelSync.CurrentLevelKey())
                return;
            Chest chest = null;
            float best = 1.5f;
            foreach (Chest c in Object.FindObjectsOfType(typeof(Chest)))
            {
                if (c == null || c.IsOpen)
                    continue;
                float d = Vector2.Distance(c.transform.position, pos);
                if (d < best)
                {
                    best = d;
                    chest = c;
                }
            }
            if (chest == null)
                return;

            bool mimic = chestMimicField != null && (bool)chestMimicField.GetValue(chest);
            // Our own roll. The game decides every chest's contents when the floor is built, from the shared seed
            // (RewardManager's floor manifest), so just clearing them got the partner's item again. Roll new
            // contents with our own random numbers instead.
            if (!mimic)
            {
                RerollChest(chest, me);
                StandInShare(chest);
            }
            Plugin.Log.LogInfo("Partner opened a chest: opening ours too (own contents).");
            applyingRemote = true;
            try
            {
                if (chest.IsLocked)
                    chest.ForceUnlock();
                chest.ForceOpen(me);
            }
            finally
            {
                applyingRemote = false;
            }

            // Not in that room: go there, so our loot isn't left behind on the floor.
            if (!me.IsGhost && !me.healthHaver.IsDead && me.CurrentRoom != RoomAt(pos))
                PlayerWarp.TeleportToPartner();
        }

        // Host with a stand-in playing for someone who left: every chest opened also gives the stand-in its own roll,
        // straight into its inventory - it's their loot, handed back when they return.
        private static readonly HashSet<Chest> standInShared = new HashSet<Chest>();

        private static void StandInShare(Chest chest)
        {
            PlayerController bot = StandIn.Player;
            if (bot == null || chest == null || bot.IsGhost || bot.healthHaver.IsDead || !standInShared.Add(chest))
                return;
            if (chestMimicField != null && (bool)chestMimicField.GetValue(chest))
                return;
            try
            {
                List<PickupObject> rolled = generateContents != null
                    ? generateContents.Invoke(chest, new object[] { bot, 0, new System.Random(ownRandom.Next()) }) as List<PickupObject>
                    : null;
                if (rolled == null)
                    return;
                foreach (PickupObject item in rolled)
                {
                    if (item == null)
                        continue;
                    LootEngine.GivePrefabToPlayer(item.gameObject, bot);
                    Plugin.Log.LogInfo("Stand-in for " + StandIn.Name + " gets its own " + item.name + " from the chest.");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't give the stand-in its chest share: " + e.Message);
            }
        }

        private static readonly MethodInfo generateContents = AccessTools.Method(typeof(Chest), "GenerateContents");
        private static readonly System.Random ownRandom = new System.Random();

        private static void RerollChest(Chest chest, PlayerController me)
        {
            try
            {
                List<PickupObject> rolled = generateContents != null
                    ? generateContents.Invoke(chest, new object[] { me, 0, new System.Random(ownRandom.Next()) }) as List<PickupObject>
                    : null;
                if (rolled != null && rolled.Count > 0)
                {
                    chest.contents = rolled;
                    return;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't roll our own chest contents: " + e.Message);
            }
            chest.contents = null;
        }

        // ---- Master Round ----

        // Rooms other players got hit in, as room index * MaxPlayers + their slot.
        private static readonly HashSet<int> partnerHitRooms = new HashSet<int>();
        private static readonly HashSet<int> sentHitRooms = new HashSet<int>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "Damaged")]
        private static void AfterPlayerDamaged(PlayerController __instance)
        {
            if (!Connected || __instance != Me || __instance.CurrentRoom == null || GameManager.Instance.Dungeon == null)
                return;
            CheckFloor();
            int roomIndex = GameManager.Instance.Dungeon.data.rooms.IndexOf(__instance.CurrentRoom);
            if (roomIndex < 0 || !sentHitRooms.Add(roomIndex))
                return;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.HitInRoom, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(roomIndex);
            });
        }

        public static void OnHitInRoom(BinaryReader r)
        {
            string level = r.ReadString();
            int roomIndex = r.ReadInt32();
            if (level != LevelSync.CurrentLevelKey())
                return;
            CheckFloor();
            partnerHitRooms.Add(roomIndex * Protocol.MaxPlayers + Mathf.Clamp(session.From, 0, Protocol.MaxPlayers - 1));
        }

        // The boss drops the Master Round when its room's "player got hit" flag is off. If we were hit but the
        // another player fought the boss alive without being hit, switch the flag off for the drop only.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AIActor), "HandleLootPinata")]
        private static void BeforeLootPinata(AIActor __instance, out bool __state)
        {
            __state = false;
            if (!Connected || __instance.healthHaver == null || !__instance.healthHaver.IsBoss)
                return;
            RoomHandler room = __instance.ParentRoom;
            if (room == null || !room.PlayerHasTakenDamageInThisRoom || !PartnerHere)
                return;
            CheckFloor();
            int roomIndex = GameManager.Instance.Dungeon.data.rooms.IndexOf(room);
            if (roomIndex < 0)
                return;
            RemotePlayerState unhit = null;
            string level = LevelSync.CurrentLevelKey();
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (p.HasPlayer && !p.IsDead && p.Scene == level && RoomAt(p.FeetPosition) == room
                    && !partnerHitRooms.Contains(roomIndex * Protocol.MaxPlayers + p.Slot))
                {
                    unhit = p;
                    break;
                }
            }
            if (unhit == null)
                return;
            Plugin.Log.LogInfo(unhit.Name + " beat the boss without getting hit: we get the Master Round too.");
            room.PlayerHasTakenDamageInThisRoom = false;
            __state = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIActor), "HandleLootPinata")]
        private static void AfterLootPinata(AIActor __instance, bool __state)
        {
            if (__state && __instance.ParentRoom != null)
                __instance.ParentRoom.PlayerHasTakenDamageInThisRoom = true;
        }

        // ---- Dropped guns ----
        // Each dropped gun gets a number (ending in the dropper's slot) so every game knows which one was taken.

        private static readonly Dictionary<int, Gun> guns = new Dictionary<int, Gun>();

        // A gun some player dropped (ours, or the copy of another player's drop).
        public static bool IsPlayerDrop(Gun g)
        {
            return g != null && guns.ContainsValue(g);
        }
        private static int nextGunId;

        private static int NewGunId()
        {
            nextGunId++;
            return nextGunId * Protocol.MaxPlayers + Mathf.Clamp(session.LocalSlot, 0, Protocol.MaxPlayers - 1);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Gun), "DropGun")]
        private static void BeforeDropGun(Gun __instance, out bool __state)
        {
            __state = !applyingRemote && Connected && Me != null && __instance.CurrentOwner == Me;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Gun), "DropGun")]
        private static void AfterDropGun(Gun __instance, DebrisObject __result, bool __state)
        {
            if (!__state || __result == null)
                return;
            CheckFloor();
            Gun dropped = __result.GetComponentInChildren<Gun>();
            if (dropped == null)
                dropped = __instance;
            int id = NewGunId();
            guns[id] = dropped;
            int pickupId = dropped.PickupObjectId;
            int ammo = dropped.CurrentAmmo;
            Vector2 pos = __result.transform.position;
            string level = LevelSync.CurrentLevelKey();
            Plugin.Log.LogInfo("Dropped gun " + pickupId + ": partner gets a copy to grab.");
            session.SendReliable(MsgType.GunDropped, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(id); w.Write(pickupId); w.Write(ammo); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnGunDropped(BinaryReader r)
        {
            string level = r.ReadString();
            int id = r.ReadInt32();
            int pickupId = r.ReadInt32();
            int ammo = r.ReadInt32();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (level != LevelSync.CurrentLevelKey())
                return;
            CheckFloor();
            PickupObject prefab = PickupObjectDatabase.GetById(pickupId);
            if (prefab == null || !(prefab is Gun))
                return;
            DebrisObject debris;
            applyingRemote = true;
            try
            {
                debris = LootEngine.SpawnItem(prefab.gameObject, pos, Vector2.zero, 0f, true, false, false);
            }
            finally
            {
                applyingRemote = false;
            }
            Gun gun = debris != null ? debris.GetComponentInChildren<Gun>() : null;
            if (gun == null)
                return;
            gun.CurrentAmmo = ammo;
            guns[id] = gun;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Gun), "Pickup")]
        private static void BeforeGunPickup(Gun __instance, PlayerController player)
        {
            if (applyingRemote || !Connected || player == null || player != Me)
                return;
            int id = -1;
            foreach (KeyValuePair<int, Gun> pair in guns)
            {
                if (pair.Value == __instance)
                {
                    id = pair.Key;
                    break;
                }
            }
            if (id < 0)
                return;
            guns.Remove(id);
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.GunTaken, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(id);
            });
        }

        public static void OnGunTaken(BinaryReader r)
        {
            string level = r.ReadString();
            int id = r.ReadInt32();
            if (level != LevelSync.CurrentLevelKey())
                return;
            Gun gun;
            if (!guns.TryGetValue(id, out gun))
                return;
            guns.Remove(id);
            // Only if it's still lying on the floor here (we didn't grab it ourselves meanwhile).
            if (gun != null && gun.CurrentOwner == null)
            {
                Plugin.Log.LogInfo("Partner picked up a dropped gun: removing our copy.");
                DebrisObject debris = gun.GetComponentInParent<DebrisObject>();
                Object.Destroy(debris != null ? debris.gameObject : gun.gameObject);
            }
        }

        // ---- Shop ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "HandleItemPurchased")]
        private static void AfterItemPurchased(PlayerController __instance, ShopItemController __0)
        {
            SendShopItemGone(__instance, __0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "HandleItemStolen")]
        private static void AfterItemStolen(PlayerController __instance, ShopItemController __0)
        {
            SendShopItemGone(__instance, __0);
        }

        private static void SendShopItemGone(PlayerController buyer, ShopItemController item)
        {
            if (!Connected || buyer != Me || item == null)
                return;
            Vector2 pos = item.transform.position;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.ShopItemGone, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(pos.x); w.Write(pos.y);
            });
        }

        public static void OnShopItemGone(BinaryReader r)
        {
            string level = r.ReadString();
            Vector2 pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (level != LevelSync.CurrentLevelKey())
                return;
            foreach (ShopItemController s in Object.FindObjectsOfType(typeof(ShopItemController)))
            {
                if (s != null && !s.Acquired && Vector2.Distance(s.transform.position, pos) < 0.5f)
                {
                    Plugin.Log.LogInfo("Partner bought a shop item: it's sold out here too.");
                    s.ForceOutOfStock();
                    return;
                }
            }
        }

        // ---- Shared money and keys ----

        private static PlayerController walletOwner;
        private static int lastMoney, lastKeys;

        // We and at least one other player in a run (not the Breach), so the purse is shared.
        private static bool InSharedRun()
        {
            PlayerController me = Me;
            return Connected && me != null && !GameManager.Instance.IsFoyer && !GameManager.Instance.IsLoadingLevel
                && PlayersInRun() > 1;
        }

        // How many players (us included) are in the run right now.
        private static int PlayersInRun()
        {
            int n = 1;
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (p.HasPlayer && !p.Scene.StartsWith("tt_foyer"))
                    n++;
            }
            return n;
        }

        // ---- The shared purse ----
        // The host's money and keys are the real purse. Guests send the host every change in theirs (pickups, buying,
        // keys used); the host adds them to its own and sends everyone the exact amount whenever it changes (and every
        // 2 s), which guests then show. (Before, every game sent its changes to every other one: one change skipped
        // or arriving at a bad moment, and the purses stayed different for the rest of the run.)
        private static float nextWalletStateAt;
        private static int sentMoney = -1, sentKeys = -1;

        private static bool InRunNow()
        {
            PlayerController me = Me;
            return me != null && GameManager.HasInstance && !GameManager.Instance.IsFoyer && !GameManager.Instance.IsLoadingLevel;
        }

        public static void Update()
        {
            if (!Connected || !GameManager.HasInstance)
                return;
            CheckFloor();
            PlayerController me = Me;
            if (me == null)
                return;
            PlayerConsumables c = me.carriedConsumables;
            if (me != walletOwner)
            {
                // New character (new run): start counting from here.
                walletOwner = me;
                lastMoney = c.Currency;
                lastKeys = c.KeyBullets;
                sentMoney = sentKeys = -1;
                return;
            }
            int dm = c.Currency - lastMoney, dk = c.KeyBullets - lastKeys;
            lastMoney = c.Currency;
            lastKeys = c.KeyBullets;
            if (!InRunNow())
                return;
            if (session.Link.IsHost)
            {
                // The host's purse is the real one: tell everyone when it changes, and now and then anyway.
                float now = Time.realtimeSinceStartup;
                if (c.Currency != sentMoney || c.KeyBullets != sentKeys || now >= nextWalletStateAt)
                    SendWalletState(c);
                return;
            }
            if (dm == 0 && dk == 0)
                return;
            int money = dm, keys = dk;
            session.SendReliable(MsgType.Wallet, delegate (BinaryWriter w)
            {
                w.Write(money); w.Write(keys);
            });
        }

        private static void SendWalletState(PlayerConsumables c)
        {
            nextWalletStateAt = Time.realtimeSinceStartup + 2f;
            sentMoney = c.Currency;
            sentKeys = c.KeyBullets;
            int money = c.Currency, keys = c.KeyBullets;
            string level = LevelSync.CurrentLevelKey();
            session.SendReliable(MsgType.WalletState, delegate (BinaryWriter w)
            {
                w.Write(level); w.Write(money); w.Write(keys);
            });
        }

        // Host: a guest's purse changed (picked up money, bought something, used a key): the shared purse changes too.
        public static void OnWallet(BinaryReader r)
        {
            int dm = r.ReadInt32(), dk = r.ReadInt32();
            PlayerController me = Me;
            if (me == null || !session.Link.IsHost || !InRunNow())
                return;
            PlayerConsumables c = me.carriedConsumables;
            c.Currency = Mathf.Max(0, c.Currency + dm);
            c.KeyBullets = Mathf.Max(0, c.KeyBullets + dk);
            // Not a change of ours to count again.
            walletOwner = me;
            lastMoney = c.Currency;
            lastKeys = c.KeyBullets;
            SendWalletState(c);
        }

        // Guest: the shared purse as the host has it.
        public static void OnWalletState(BinaryReader r)
        {
            string level = r.ReadString();
            int money = r.ReadInt32(), keys = r.ReadInt32();
            PlayerController me = Me;
            if (me == null || !InRunNow() || level != LevelSync.CurrentLevelKey())
                return;
            PlayerConsumables c = me.carriedConsumables;
            // A change of ours this frame that Update hasn't sent yet (a pickup) stays on top of the host's total and
            // still gets sent: overwriting it lost that pickup for good.
            int unsentMoney = walletOwner == me ? c.Currency - lastMoney : 0;
            int unsentKeys = walletOwner == me ? c.KeyBullets - lastKeys : 0;
            c.Currency = Mathf.Max(0, money + unsentMoney);
            c.KeyBullets = Mathf.Max(0, keys + unsentKeys);
            walletOwner = me;
            lastMoney = money;
            lastKeys = keys;
        }

        // Split money (host rule, on by default): money that drops in every game at once counts 1/players, so the
        // shared purse doesn't get it once per player. That's only a boss's drop now: an ordinary enemy drops its
        // money in the killer's game alone (the copies other games kill drop nothing), so that counts in full.
        // (Before, every pickup was split - with 4 players a run earned a quarter of a solo run.)
        // Parts that don't come out even are saved up, so nothing is lost over a run.
        private static float moneyCarry;
        private static bool bossDropping;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AIActor), "HandleLootPinata")]
        private static void BeforeBossDrop(AIActor __instance)
        {
            bossDropping = __instance.healthHaver != null && __instance.healthHaver.IsBoss;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIActor), "HandleLootPinata")]
        private static void AfterBossDrop()
        {
            bossDropping = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LootEngine), "SpawnCurrency", new System.Type[] { typeof(Vector2), typeof(int), typeof(bool),
            typeof(Vector2?), typeof(float?), typeof(float), typeof(float) })]
        private static void BeforeSpawnCurrency(ref int __1, bool __2)
        {
            if (!bossDropping || __2 || !session.HalfMoney || !InSharedRun())
                return;
            float share = __1 / (float)PlayersInRun() + moneyCarry;
            int give = Mathf.FloorToInt(share);
            moneyCarry = share - give;
            __1 = give;
        }

        // ---- Thief rat ----
        // The rat leaves items alone while the (single-player) player is dead. Online, a ghost will come back,
        // so let the rat behave as if we're alive: briefly give the dead player a sliver of health for the check.

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PickupObject), "ShouldBeTakenByRat")]
        private static void BeforeRatCheck(out float __state)
        {
            __state = -1f;
            PlayerController me = Me;
            if (!Connected || me == null || currentHealthField == null || !me.healthHaver.IsDead)
                return;
            __state = (float)currentHealthField.GetValue(me.healthHaver);
            currentHealthField.SetValue(me.healthHaver, 0.5f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PickupObject), "ShouldBeTakenByRat")]
        private static void AfterRatCheck(float __state)
        {
            PlayerController me = Me;
            if (__state >= 0f && me != null)
                currentHealthField.SetValue(me.healthHaver, __state);
        }
    }
}
