using UnityEngine;

namespace GungeonOnline
{
    // Shows which save slot is selected ("SAVE SLOT A") in the top-right corner of the title screen, so players
    // know which save an online game will use. Clicking it opens a small list to switch to another slot.
    // Switching does exactly what the Options menu's save slot setting does: set SaveManager.TargetSaveSlot and
    // reload the main menu, which loads that slot's save.
    public static class SaveSlotLabel
    {
        private static readonly SaveManager.SaveSlot[] Slots =
            { SaveManager.SaveSlot.A, SaveManager.SaveSlot.B, SaveManager.SaveSlot.C, SaveManager.SaveSlot.D };

        private static NetSession session;
        private static MainMenuFoyerController title;
        private static float nextLookAt;
        private static bool listOpen;
        private static GUIStyle style, shadow, item, itemCurrent, small, smallShadow;

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        public static bool IsListOpen
        {
            get { return listOpen; }
        }

        public static void Draw()
        {
            // Looking the title screen up every frame is slow; once a second is plenty.
            if (title == null && Time.realtimeSinceStartup >= nextLookAt)
            {
                nextLookAt = Time.realtimeSinceStartup + 1f;
                title = Object.FindObjectOfType(typeof(MainMenuFoyerController)) as MainMenuFoyerController;
            }
            // Leaving the title screen (PLAY) switches its menu off (DisableMainMenu) but the button stays "visible".
            bool onTitle = title != null && title.NewGameButton != null && title.NewGameButton.IsVisible
                && title.NewGameButton.GUIManager != null && title.NewGameButton.GUIManager.enabled;
            if (!onTitle || MultiplayerMenu.IsOpen)
            {
                listOpen = false;
                return;
            }
            MakeStyles();

            // Our version, bottom-left, so players can check they match before playing together.
            string version = "Gungeon Online v" + Plugin.ModVersion;
            Vector2 vSize = small.CalcSize(new GUIContent(version));
            Rect vr = new Rect(16f, Screen.height - vSize.y - 16f, vSize.x + 8f, vSize.y + 4f);
            GUI.Label(new Rect(vr.x + 2f, vr.y + 2f, vr.width, vr.height), version, smallShadow);
            GUI.Label(vr, version, small);

            float size = style.fontSize;
            string text = "SAVE SLOT " + SaveManager.CurrentSaveSlot + (listOpen ? "  ▲" : "  ▼");
            Vector2 textSize = style.CalcSize(new GUIContent(text));
            Rect r = new Rect(Screen.width - textSize.x - 24f, 16f, textSize.x + 8f, textSize.y + 4f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, shadow);
            if (GUI.Button(r, text, style))
                listOpen = !listOpen;
            if (!listOpen)
                return;

            // Switching slots mid-session would load another save under the partner: only offline.
            bool online = session != null && session.Link != null;
            float rowH = size * 1.8f;
            float w = Mathf.Max(r.width, size * 9f);
            Rect box = new Rect(Screen.width - w - 24f, r.yMax + 6f, w, rowH * (online ? 5 : 4) + 12f);
            // Drawn twice: the default box is see-through enough that the title art shows through.
            GUI.Box(box, GUIContent.none);
            GUI.Box(box, GUIContent.none);
            float y = box.y + 6f;
            if (online)
            {
                GUI.Label(new Rect(box.x + 8f, y, w - 16f, rowH), "Leave multiplayer first", item);
                y += rowH;
            }
            GUI.enabled = !online;
            foreach (SaveManager.SaveSlot slot in Slots)
            {
                bool current = slot == SaveManager.CurrentSaveSlot;
                string label = "Slot " + slot + (current ? "  (current)" : "");
                if (GUI.Button(new Rect(box.x + 6f, y, w - 12f, rowH - 4f), label, current ? itemCurrent : item) && !current)
                    SwitchTo(slot);
                y += rowH;
            }
            GUI.enabled = true;
        }

        private static void SwitchTo(SaveManager.SaveSlot slot)
        {
            listOpen = false;
            Plugin.Log.LogInfo("Switching to save slot " + slot + " (reloading the main menu).");
            SaveManager.TargetSaveSlot = slot;
            title = null;
            GameManager.Instance.LoadMainMenu();
        }

        private static void MakeStyles()
        {
            if (style != null)
                return;
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = Mathf.Max(18, Screen.height / 40);
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.UpperRight;
            style.normal.textColor = new Color(0.95f, 0.78f, 0.35f);
            style.hover.textColor = Color.white;
            shadow = new GUIStyle(style);
            shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            small = new GUIStyle(GUI.skin.label);
            small.fontSize = Mathf.Max(14, Screen.height / 60);
            small.normal.textColor = new Color(0.95f, 0.78f, 0.35f, 0.9f);
            smallShadow = new GUIStyle(small);
            smallShadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);

            item = new GUIStyle(GUI.skin.button);
            item.fontSize = Mathf.Max(16, Screen.height / 48);
            item.alignment = TextAnchor.MiddleLeft;
            item.padding = new RectOffset(12, 12, 4, 4);
            itemCurrent = new GUIStyle(item);
            itemCurrent.normal.textColor = new Color(0.95f, 0.78f, 0.35f);
            itemCurrent.hover.textColor = itemCurrent.normal.textColor;
        }
    }
}
