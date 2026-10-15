using UnityEngine;

namespace GungeonOnline
{
    // A short message at the top of the screen ("Floor didn't match the host's: reloading", "You're banned from
    // this shop"...). One at a time; a new one replaces the old.
    public static class Toast
    {
        private static string text = "";
        private static float until;
        private static GUIStyle style, shadow;

        public static void Show(string message, float seconds)
        {
            text = message;
            until = Time.realtimeSinceStartup + seconds;
            Plugin.Log.LogInfo("[on screen] " + message);
        }

        public static void Draw()
        {
            if (text.Length == 0 || Time.realtimeSinceStartup > until || Event.current.type != EventType.Repaint)
                return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label);
                style.fontSize = Mathf.Max(20, Screen.height / 36);
                style.fontStyle = FontStyle.Bold;
                style.alignment = TextAnchor.UpperCenter;
                style.wordWrap = true;
                style.normal.textColor = new Color(0.95f, 0.78f, 0.35f);
                shadow = new GUIStyle(style);
                shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            }
            Rect r = new Rect(Screen.width * 0.15f, Screen.height * 0.12f, Screen.width * 0.7f, Screen.height * 0.2f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, shadow);
            GUI.Label(r, text, style);
        }
    }
}
