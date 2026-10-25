using System.IO;
using HarmonyLib;

namespace GungeonOnline
{
    // The four parts of the Bullet That Can Kill The Past (Prime Primer, Arcane Gunpowder, Planar Lead, Obsidian
    // Shell Casing) and the Bullet itself are shared: when one player gets one, every player gets a copy. So any
    // player can craft the Bullet at the Blacksmith, and whoever reaches the ark first can use it.
    public static class KeyItemSync
    {
        private static readonly int[] Shared = { 348, 349, 350, 351, 303 };

        private static NetSession session;
        private static bool giving;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(KeyItemSync));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PassiveItem), "Pickup")]
        private static void AfterPickup(PassiveItem __instance, PlayerController player)
        {
            if (giving || session == null || session.Status != NetSession.State.Connected || __instance == null || player == null
                || !GameManager.HasInstance || player != GameManager.Instance.PrimaryPlayer)
                return;
            int id = __instance.PickupObjectId;
            if (System.Array.IndexOf(Shared, id) < 0)
                return;
            Plugin.Log.LogInfo("We got " + __instance.EncounterNameOrDisplayName + ": every player gets one.");
            session.SendReliable(MsgType.KeyItem, delegate (BinaryWriter w) { w.Write(id); });
        }

        public static void OnKeyItem(BinaryReader r)
        {
            int id = r.ReadInt32();
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || System.Array.IndexOf(Shared, id) < 0 || GameManager.Instance.IsFoyer || me.HasPassiveItem(id))
                return;
            PickupObject prefab = PickupObjectDatabase.GetById(id);
            if (prefab == null)
                return;
            Plugin.Log.LogInfo(session.PeerName + " got " + prefab.EncounterNameOrDisplayName + ": we get one too.");
            Toast.Show(session.PeerName + " found " + prefab.EncounterNameOrDisplayName + " - you got one too.", 4f);
            giving = true;
            try
            {
                LootEngine.GivePrefabToPlayer(prefab.gameObject, me);
            }
            finally
            {
                giving = false;
            }
        }
    }
}
