using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Shopkeepers online: only the player who angers a shopkeeper is punished.
    // Normally shooting in a shop, hitting the shopkeeper or being caught stealing makes him hostile, refuse service
    // or leave, which shut the shop for both players. Online, those moods are turned into a ban for the offender:
    // - the shopkeeper stays, so the partner can still buy;
    // - the banned player can't buy, and talking to the shopkeeper (or trying to buy) gets "you're banned" lines.
    // Also, the partner's copied bullets no longer anger our shopkeeper (they're just pictures here).
    public static class ShopBan
    {
        private static NetSession session;
        private static readonly HashSet<BaseShopController> banned = new HashSet<BaseShopController>();
        private static readonly System.Reflection.FieldInfo shopkeepField = AccessTools.Field(typeof(BaseShopController), "m_shopkeep");
        private static readonly System.Reflection.FieldInfo parentShopField = AccessTools.Field(typeof(ShopItemController), "m_baseParentShop");
        private static int lineIndex;

        private static readonly string[] BanLines =
        {
            "SHOPKEEPER: You again? Get out of my shop!",
            "SHOPKEEPER: I don't do business with your kind anymore.",
            "SHOPKEEPER: Your friend can shop here. YOU are banned.",
            "SHOPKEEPER: Not one casing from you. Out!",
            "SHOPKEEPER: Banned. Forever. Well... for this floor.",
        };

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(ShopBan));
        }

        private static bool Online
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static bool IsBanned(BaseShopController shop)
        {
            return shop != null && banned.Contains(shop);
        }

        private static void ShowBanLine()
        {
            Toast.Show(BanLines[lineIndex % BanLines.Length], 4f);
            lineIndex++;
        }

        // The mood change that would close the shop becomes a ban for us instead.
        // (The mood type is hidden inside the game's shop code, so it's read and replaced by name.)
        private static readonly string[] ClosingMoods = { "Hostile", "RefuseService", "PreTeleportAway", "TeleportAway", "Gone" };
        private static object defaultMood;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseShopController), "set_State")]
        private static void BeforeSetState(BaseShopController __instance, object[] __args)
        {
            if (!Online || __args == null || __args.Length == 0 || __args[0] == null)
                return;
            string mood = __args[0].ToString();
            if (System.Array.IndexOf(ClosingMoods, mood) < 0)
                return;
            if (defaultMood == null)
                defaultMood = System.Enum.Parse(__args[0].GetType(), "Default");
            if (banned.Add(__instance))
            {
                Plugin.Log.LogInfo("We angered the shopkeeper (" + mood + "): banned from this shop (the partner can still buy).");
                Toast.Show("You angered the shopkeeper! You're banned from this shop. Your partner can still buy.", 5f);
            }
            __args[0] = defaultMood;
        }

        // Banned: no buying.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ShopItemController), "Interact")]
        private static bool BeforeBuy(ShopItemController __instance)
        {
            if (!Online || parentShopField == null)
                return true;
            if (!IsBanned(parentShopField.GetValue(__instance) as BaseShopController))
                return true;
            ShowBanLine();
            return false;
        }

        // Banned: the shopkeeper only tells us off.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TalkDoerLite), "Interact")]
        private static bool BeforeTalk(TalkDoerLite __instance)
        {
            if (!Online || banned.Count == 0 || shopkeepField == null)
                return true;
            foreach (BaseShopController shop in banned)
            {
                if (shop != null && ReferenceEquals(shopkeepField.GetValue(shop), __instance))
                {
                    ShowBanLine();
                    return false;
                }
            }
            return true;
        }

        // The partner's copied bullets are pictures: they don't count as us shooting in the shop.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseShopController), "OnProjectileCreated")]
        private static bool BeforeShopSeesProjectile(Projectile __0)
        {
            return !(Online && BulletSync.IsRemote(__0));
        }

        public static void Update()
        {
            // Shops belong to a floor; forget bans for shops that are gone.
            if (banned.Count > 0)
                banned.RemoveWhere(delegate (BaseShopController s) { return s == null; });
        }
    }
}
