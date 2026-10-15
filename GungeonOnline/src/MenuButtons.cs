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
            AddBelow(__instance.ControlsButton, "title screen");
        }

        private static void AddBelow(dfButton anchor, string where)
        {
            if (anchor == null || anchor.Parent == null)
            {
                Plugin.Log.LogWarning("No anchor button for the Multiplayer button on the " + where + ".");
                return;
            }
            // Only once per menu.
            foreach (dfButton b in added)
            {
                if (b != null && b.Parent == anchor.Parent)
                    return;
            }

            GameObject copy = (GameObject)Object.Instantiate(anchor.gameObject, anchor.transform.parent);
            copy.name = "GungeonOnline Multiplayer Button";
            // Drop anything wired to the original in the inspector (event bindings, key navigation targets).
            foreach (MonoBehaviour m in copy.GetComponents<MonoBehaviour>())
            {
                if (!(m is dfControl))
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
            added.Add(button);
            Plugin.Log.LogInfo("Added the Multiplayer button to the " + where + ".");
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
