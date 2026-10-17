using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Other mods' settings that must be the same for both players (they change how floors are built or the game
    // plays), synced from the host when you join. Each mod keeps its settings in a Gunfig menu (Mod Config).
    // - Gungeon Go Vroom: most of its fixes are set up when the game starts, and two of them (room shuffle fix,
    //   random cell pick) change floor generation. So the joiner's settings are CHANGED and SAVED to the host's,
    //   and if anything changed the joiner is told to restart the game once.
    // Host: sends the settings on connect and whenever they change. Joiner without the mod: told to install it.
    // (Loop The Gungeon has its own live sync in LoopSync.)
    public static class ModSettingsSync
    {
        private class SyncedMod
        {
            public string Name, TypeName, FieldName;
            public SyncedMod(string name, string type, string field) { Name = name; TypeName = type; FieldName = field; }
        }

        private static readonly SyncedMod[] Mods =
        {
            new SyncedMod("Gungeon Go Vroom", "GGV.ConfigMenu", "_Gunfig"),
        };

        private static NetSession session;
        private static Type gunfigType;
        private static FieldInfo optionsField, parentField, nameField, activeField;
        private static MethodInfo setMethod, saveAllMethod;
        private static bool setUp;
        private static float nextCheckAt;
        private static string lastSent = "";

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        private static void SetUp()
        {
            setUp = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                gunfigType = a.GetType("Gunfiguration.Gunfig", false);
                if (gunfigType != null)
                    break;
            }
            if (gunfigType == null)
                return;
            optionsField = AccessTools.Field(gunfigType, "_options");
            parentField = AccessTools.Field(gunfigType, "_parentGunfig");
            nameField = AccessTools.Field(gunfigType, "_cleanModName");
            activeField = AccessTools.Field(gunfigType, "_ActiveConfigs");
            setMethod = AccessTools.Method(gunfigType, "Set", new Type[] { typeof(string), typeof(string) });
            saveAllMethod = AccessTools.Method(gunfigType, "SaveActiveConfigsToDisk");
        }

        // The mod's main Gunfig menu, or null when the mod isn't installed.
        private static object MainMenu(SyncedMod mod)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(mod.TypeName, false);
                if (t == null)
                    continue;
                FieldInfo f = t.GetField(mod.FieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? f.GetValue(null) : null;
            }
            return null;
        }

        private static bool BelongsTo(object gunfig, object main)
        {
            object g = gunfig;
            for (int i = 0; g != null && i < 8; i++)
            {
                if (ReferenceEquals(g, main))
                    return true;
                g = parentField.GetValue(g);
            }
            return false;
        }

        // Every Gunfig (main menu and submenus) of the mod.
        private static List<object> Menus(object main)
        {
            List<object> menus = new List<object>();
            IList all = activeField != null ? activeField.GetValue(null) as IList : null;
            if (all == null || main == null)
                return menus;
            foreach (object g in all)
            {
                if (BelongsTo(g, main))
                    menus.Add(g);
            }
            return menus;
        }

        // "menu<1>key" -> value for all of the mod's options.
        private static Dictionary<string, string> Settings(object main)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            foreach (object g in Menus(main))
            {
                Dictionary<string, string> options = optionsField.GetValue(g) as Dictionary<string, string>;
                if (options == null)
                    continue;
                string menu = (string)nameField.GetValue(g);
                foreach (KeyValuePair<string, string> o in options)
                    result[menu + "\u0001" + o.Key] = o.Value ?? "";
            }
            return result;
        }

        // ---- Host ----

        public static void OnConnected()
        {
            lastSent = "";
            Send();
        }

        public static void Update()
        {
            if (!setUp)
                SetUp();
            if (session == null || session.Link == null || !session.Link.IsHost || session.Status != NetSession.State.Connected)
                return;
            if (Time.realtimeSinceStartup < nextCheckAt)
                return;
            nextCheckAt = Time.realtimeSinceStartup + 5f;
            Send();
        }

        private static void Send()
        {
            if (!setUp)
                SetUp();
            if (gunfigType == null || session == null || session.Link == null || !session.Link.IsHost)
                return;
            List<string> names = new List<string>();
            List<Dictionary<string, string>> all = new List<Dictionary<string, string>>();
            System.Text.StringBuilder keyBuilder = new System.Text.StringBuilder(2048);
            foreach (SyncedMod mod in Mods)
            {
                object main = MainMenu(mod);
                if (main == null)
                    continue;
                Dictionary<string, string> settings = Settings(main);
                names.Add(mod.Name);
                all.Add(settings);
                foreach (KeyValuePair<string, string> s in settings)
                    keyBuilder.Append(s.Key).Append('=').Append(s.Value).Append(';');
            }
            string key = keyBuilder.ToString();
            if (key == lastSent)
                return;
            lastSent = key;
            session.SendReliable(MsgType.ModSettings, delegate (BinaryWriter w)
            {
                w.Write(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    w.Write(names[i]);
                    w.Write(all[i].Count);
                    foreach (KeyValuePair<string, string> s in all[i])
                    {
                        w.Write(s.Key);
                        w.Write(s.Value);
                    }
                }
            });
        }

        // ---- Joiner ----

        public static void OnModSettings(BinaryReader r)
        {
            if (!setUp)
                SetUp();
            int modCount = r.ReadInt32();
            List<string> missing = new List<string>();
            List<string> changedMods = new List<string>();
            for (int m = 0; m < modCount; m++)
            {
                string name = r.ReadString();
                int n = r.ReadInt32();
                Dictionary<string, string> host = new Dictionary<string, string>();
                for (int i = 0; i < n; i++)
                {
                    string k = r.ReadString();
                    host[k] = r.ReadString();
                }
                SyncedMod mod = null;
                foreach (SyncedMod s in Mods)
                {
                    if (s.Name == name)
                        mod = s;
                }
                object main = mod != null && gunfigType != null ? MainMenu(mod) : null;
                if (main == null)
                {
                    missing.Add(name);
                    continue;
                }
                int changed = Apply(main, host);
                if (changed > 0)
                {
                    changedMods.Add(name);
                    Plugin.Log.LogInfo(name + ": changed " + changed + " settings to match the host's (saved).");
                }
                else
                {
                    Plugin.Log.LogInfo(name + ": settings already match the host's.");
                }
            }
            if (changedMods.Count > 0)
                Toast.Show(string.Join(", ", changedMods.ToArray()) + " settings were changed to match the host's. Restart the game once so they take effect (floors may not match until then).", 15f);
            else if (missing.Count > 0)
                Toast.Show("The host uses " + string.Join(", ", missing.ToArray()) + ". Install it (same settings) or floors may not match.", 12f);
        }

        // Puts the host's values into our menus and saves them. Returns how many changed.
        private static int Apply(object main, Dictionary<string, string> host)
        {
            int changed = 0;
            foreach (object g in Menus(main))
            {
                Dictionary<string, string> options = optionsField.GetValue(g) as Dictionary<string, string>;
                if (options == null)
                    continue;
                string menu = (string)nameField.GetValue(g);
                foreach (string k in new List<string>(options.Keys))
                {
                    string want;
                    if (!host.TryGetValue(menu + "\u0001" + k, out want) || options[k] == want)
                        continue;
                    try
                    {
                        setMethod.Invoke(g, new object[] { k, want });
                        changed++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Couldn't set " + k + ": " + e.Message);
                    }
                }
            }
            if (changed > 0 && saveAllMethod != null)
            {
                try
                {
                    saveAllMethod.Invoke(null, null);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't save the changed settings: " + e.Message);
                }
            }
            return changed;
        }
    }
}
