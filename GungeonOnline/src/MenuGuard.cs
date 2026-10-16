using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // While our Multiplayer menu is open (and a moment after it closes), the game's own menus must not react to the
    // controller too. Otherwise one press did two things: moving/pressing in the title or pause menu underneath, or
    // B closing the pause menu and unpausing the game along with closing ours.
    // - InControlInputAdapter turns controller input into menu movement and button presses for the game's menus.
    // - MainMenuFoyerController.Update (title screen) moves the selection back to PLAY and handles B by itself.
    // - GameManager.Unpause is what B does in the pause menu.
    public static class MenuGuard
    {
        private static float blockUntil;

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(MenuGuard));
        }

        private static bool Blocked
        {
            get { return MultiplayerMenu.IsOpen || Time.realtimeSinceStartup < blockUntil; }
        }

        // Called when our menu closes: keep the game's menus quiet a little longer, so the press that closed ours
        // (B or A on "Back") isn't also taken by them.
        public static void OnMenuClosed()
        {
            blockUntil = Time.realtimeSinceStartup + 0.3f;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InControlInputAdapter), "ProcessPrimaryPlayerInput")]
        private static bool BeforePrimaryMenuInput()
        {
            return !Blocked;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InControlInputAdapter), "ProcessSecondaryPlayerInput")]
        private static bool BeforeSecondaryMenuInput()
        {
            return !Blocked;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuFoyerController), "Update")]
        private static bool BeforeTitleUpdate()
        {
            return !Blocked;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "Unpause")]
        private static bool BeforeUnpause()
        {
            return !Blocked;
        }
    }
}
