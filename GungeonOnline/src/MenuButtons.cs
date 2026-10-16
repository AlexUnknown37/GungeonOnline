using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // A real "MULTIPLAYER" button in the game's own menus: the title screen and the pause menu.
    // It's a copy of the menu's Options button (same look, font and sounds), put right below it; the
    // buttons under it move down to make room. The game wires its buttons' clicks in code when the menu
    // starts, so the copy has no click action of its own; we give it ours (open the Multiplayer menu).
    public static class MenuButtons
    {
        private const string Label = "MULTIPLAYER";
        private static readonly HashSet<dfButton> added = new HashSet<dfButton>();

        public static void Init(Harmony harmony)
        {
            harmony.PatchAll(typeof(MenuButtons));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PauseMenuController), "Start")]
        private static void AfterPauseMenuStart(PauseMenuController __instance)
        {
            AddBelow(__instance.OptionsButton, "pause menu");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuFoyerController), "InitializeMainMenu")]
        private static void AfterMainMenuInit(MainMenuFoyerController __instance)
        {
            dfButton ours = AddBelow(__instance.ControlsButton, "title screen");
            // The title screen keeps the selection on ITS buttons: when none of them is selected it selects PLAY
            // again, so ours could never stay selected. Its Xbox sign-in button isn't used on PC; ours takes that
            // slot so it counts as one of the title screen's buttons.
            if (ours != null && (__instance.XboxLiveButton == null || !__instance.XboxLiveButton.IsVisible))
                __instance.XboxLiveButton = ours;
        }

        private static dfButton AddBelow(dfButton anchor, string where)
        {
            if (anchor == null || anchor.Parent == null)
            {
                Plugin.Log.LogWarning("No anchor button for the Multiplayer button on the " + where + ".");
                return null;
            }
            // Only once per menu.
            foreach (dfButton b in added)
            {
                if (b != null && b.Parent == anchor.Parent)
                    return b;
            }

            GameObject copy = (GameObject)Object.Instantiate(anchor.gameObject, anchor.transform.parent);
            copy.name = "GungeonOnline Multiplayer Button";
            // Drop anything wired to the original in the inspector (event bindings), but keep the controller/keyboard
            // navigation (UIKeyControls: which button is above/below); it's re-linked below.
            foreach (MonoBehaviour m in copy.GetComponents<MonoBehaviour>())
            {
                if (!(m is dfControl) && !(m is UIKeyControls))
                    Object.Destroy(m);
            }

            dfButton button = copy.GetComponent<dfButton>();
            button.IsLocalized = false;
            button.Text = Label;
            button.IsVisible = true;
            button.IsEnabled = true;

            // Make room without pushing the bottom button (QUIT) off screen: the new button takes the anchor's
            // spot, and the anchor and every button above it move UP by one row. A row is the real spacing
            // between two neighbouring buttons (not just the button's height).
            Vector3 anchorPos = anchor.RelativePosition;
            float step = RowSpacing(anchor);
            foreach (dfControl sibling in anchor.Parent.Controls)
            {
                if (sibling is dfButton && sibling != button && sibling.RelativePosition.y <= anchorPos.y + 0.5f)
                    sibling.RelativePosition = sibling.RelativePosition - new Vector3(0f, step, 0f);
            }
            button.RelativePosition = anchorPos;
            button.ZOrder = anchor.ZOrder + 1;

            button.Click += OnClick;
            LinkNavigation(anchor, button);
            added.Add(button);
            Plugin.Log.LogInfo("Added the Multiplayer button to the " + where + ".");
            return button;
        }

        // Controller/keyboard menu navigation: put the new button between the anchor (now above it) and the
        // button that was below the anchor.
        private static void LinkNavigation(dfButton anchor, dfButton button)
        {
            UIKeyControls anchorKeys = anchor.GetComponent<UIKeyControls>();
            UIKeyControls ourKeys = button.GetComponent<UIKeyControls>();
            if (ourKeys == null)
                ourKeys = button.gameObject.AddComponent<UIKeyControls>();
            dfControl below = anchorKeys != null ? anchorKeys.down : null;
            originalDown[button] = below;
            ourKeys.up = anchor;
            ourKeys.down = below;
            ourKeys.left = null;
            ourKeys.right = null;
            if (anchorKeys != null)
                anchorKeys.down = button;
            UIKeyControls belowKeys = below != null ? below.GetComponent<UIKeyControls>() : null;
            if (belowKeys != null)
                belowKeys.up = button;
            Plugin.Log.LogInfo("Multiplayer button navigation: up " + (anchor != null ? anchor.name : "-") + ", down " + (below != null ? below.name : "-"));
        }

        // Which pause-menu buttons show depends on the situation (Quick Restart, Exit Coop...): link ours to the
        // buttons actually visible above and below it, checked twice a second while the menu shows.
        private static float nextRelinkAt;
        private static readonly Dictionary<dfButton, dfControl> originalDown = new Dictionary<dfButton, dfControl>();

        public static void Update()
        {
            if (Time.realtimeSinceStartup < nextRelinkAt)
                return;
            nextRelinkAt = Time.realtimeSinceStartup + 0.5f;
            foreach (dfButton b in added)
            {
                if (b != null && b.IsVisible && b.Parent != null && b.Parent.IsVisible)
                    RelinkByPosition(b);
            }
        }

        private static void RelinkByPosition(dfButton button)
        {
            UIKeyControls ours = button.GetComponent<UIKeyControls>();
            if (ours == null)
                return;
            dfControl above = null, below = null;
            float y = button.RelativePosition.y;
            float bestAbove = float.MaxValue, bestBelow = float.MaxValue;
            foreach (dfControl c in button.Parent.Controls)
            {
                if (!(c is dfButton) || c == button || !c.IsVisible || !c.IsEnabled)
                    continue;
                float d = c.RelativePosition.y - y; // dfGUI: y grows downward
                if (d < -0.5f && -d < bestAbove) { bestAbove = -d; above = c; }
                if (d > 0.5f && d < bestBelow) { bestBelow = d; below = c; }
            }
            // Neighbours in another group (the title's QUIT) aren't found by position: keep the original links.
            dfControl original;
            if (below == null && originalDown.TryGetValue(button, out original) && original != null && original.IsVisible)
                below = original;
            if (above == null)
                above = ours.up;
            if (ours.up == above && ours.down == below)
                return;
            ours.up = above;
            ours.down = below;
            UIKeyControls aboveKeys = above != null ? above.GetComponent<UIKeyControls>() : null;
            if (aboveKeys != null)
                aboveKeys.down = button;
            UIKeyControls belowKeys = below != null ? below.GetComponent<UIKeyControls>() : null;
            if (belowKeys != null)
                belowKeys.up = button;
        }

        // Distance to the closest other button above or below (dfGUI y grows downward); the button height if alone.
        private static float RowSpacing(dfButton anchor)
        {
            float best = float.MaxValue;
            foreach (dfControl sibling in anchor.Parent.Controls)
            {
                if (!(sibling is dfButton) || sibling == anchor || !sibling.IsVisible)
                    continue;
                float d = Mathf.Abs(sibling.RelativePosition.y - anchor.RelativePosition.y);
                if (d > 1f && d < best)
                    best = d;
            }
            return best == float.MaxValue ? anchor.Height : best;
        }

        private static void OnClick(dfControl control, dfMouseEventArgs args)
        {
            MultiplayerMenu.Open();
        }
    }
}
