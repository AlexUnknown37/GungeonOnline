using System.Collections.Generic;
using HarmonyLib;

namespace GungeonOnline
{
    // More players, tougher enemies: every enemy's health is multiplied by 1 + (bonus per extra player) x (players - 1)
    // (+50% each by default: x1.5 with two players, x2 with three, x2.5 with four). A stand-in bot counts as a player.
    //
    // The host decides the multiplier (its rule and its count of players) and sends it with the other rules, so every
    // game gives the same enemy the same health - the games compare enemy health, so they must agree.
    //
    // Loop The Gungeon sets enemy health in AIActor.Start from the enemy's base health ("non-stacking"). Ours runs
    // after it (lowest Harmony priority) and multiplies the health it set, so the two stack: loop 2 with four players is
    // Loop's bonus times ours.
    public static class DifficultySync
    {
        private static NetSession session;
        private static float joinerFactor = 1f;
        private static readonly HashSet<HealthHaver> scaled = new HashSet<HealthHaver>();

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(DifficultySync));
        }

        // Host: how many play right now (us, the guests, a stand-in).
        public static int PartySize()
        {
            if (session == null || session.Link == null)
                return 1;
            return 1 + session.Remotes.Count + (StandIn.Active && !session.Players[StandIn.Slot].Connected ? 1 : 0);
        }

        // The health multiplier in use: the host works it out, guests use what the host sent.
        public static float Factor
        {
            get
            {
                if (session == null || session.Link == null || session.Status != NetSession.State.Connected && !StandIn.Active)
                    return 1f;
                if (!session.Link.IsHost)
                    return joinerFactor;
                if (!Plugin.ScaleEnemyHealth.Value)
                    return 1f;
                return 1f + System.Math.Max(0f, Plugin.HealthPerExtraPlayer.Value) * (PartySize() - 1);
            }
        }

        public static void SetFromHost(float factor)
        {
            if (System.Math.Abs(factor - joinerFactor) > 0.001f)
                Plugin.Log.LogInfo("Host's enemy health multiplier: x" + factor.ToString("0.##"));
            joinerFactor = factor;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIActor), "Start")]
        [HarmonyPriority(Priority.Last)]
        private static void AfterEnemyStart(AIActor __instance)
        {
            float f = Factor;
            if (f <= 1.001f || __instance == null || __instance.CompanionOwner != null || !__instance.IsNormalEnemy)
                return;
            HealthHaver h = __instance.healthHaver;
            if (h == null || h.IsDead || !scaled.Add(h))
                return;
            float max = h.GetMaxHealth();
            if (max <= 0f)
                return;
            h.SetHealthMaximum(max * f, null, true);
            if (h.IsBoss)
                Plugin.Log.LogInfo("Boss " + __instance.name + ": health x" + f.ToString("0.##") + " for " + PartySize() + " players.");
        }

        // Forget enemies that are gone (keeps the set small).
        public static void Update()
        {
            if (scaled.Count > 500)
                scaled.RemoveWhere(delegate (HealthHaver h) { return h == null; });
        }
    }
}
