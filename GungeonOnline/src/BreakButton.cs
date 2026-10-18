using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Online, the pause menu's "Exit Coop" button (shown in the Breach, greyed out outside local co-op) becomes
    // "TAKE A BREAK": it switches the bot on (Autopilot) and closes the pause menu. Offline it's the game's button
    // again. The same button reads "Quit to Main Menu" in a run; that one is left alone.
    public static class BreakButton
    {
        private const string ExitCoopKey = "#EXIT_COOP";
        private const string Label = "TAKE A BREAK";

        private static NetSession session;
        private static bool relabeled;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(BreakButton));
        }

        private static bool Online
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        private static void Apply(PauseMenuController menu)
        {
            dfButton b = menu != null ? menu.ExitToMainMenuButton : null;
            if (b == null)
                return;
            bool isExitCoop = relabeled ? b.Text == Label : b.Text == ExitCoopKey;
            if (!isExitCoop)
                return;
            if (Online && !relabeled)
            {
                relabeled = true;
                b.IsLocalized = false;
                b.Text = Label;
                b.Enable();
                b.IsVisible = true;
            }
            else if (!Online && relabeled)
            {
                relabeled = false;
                b.Text = ExitCoopKey;
                b.IsLocalized = true;
                if (GameManager.Instance.CurrentGameType == GameManager.GameType.SINGLE_PLAYER)
                    b.Disable();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PauseMenuController), "Start")]
        private static void AfterStart(PauseMenuController __instance)
        {
            Apply(__instance);
        }

        // The game re-greys the button on co-op changes and each time the menu opens: put ours back.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PauseMenuController), "ToggleExitCoopButtonOnCoopChange")]
        private static void AfterCoopChange(PauseMenuController __instance)
        {
            if (relabeled && Online && __instance.ExitToMainMenuButton != null)
                __instance.ExitToMainMenuButton.Enable();
            Apply(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PauseMenuController), "OnVisibilityChanged")]
        private static void AfterVisibilityChanged(PauseMenuController __instance)
        {
            Apply(__instance);
            if (relabeled && Online && __instance.ExitToMainMenuButton != null)
                __instance.ExitToMainMenuButton.Enable();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PauseMenuController), "DoExitToMainMenu")]
        private static bool BeforeExitClicked(PauseMenuController __instance)
        {
            if (!relabeled || !Online || __instance.ExitToMainMenuButton == null || __instance.ExitToMainMenuButton.Text != Label)
                return true;
            Plugin.Log.LogInfo("Pause menu: Take a Break.");
            if (GameManager.HasInstance && GameManager.Instance.IsPaused)
                GameManager.Instance.Unpause();
            Autopilot.Start(false);
            return false;
        }
    }
}
