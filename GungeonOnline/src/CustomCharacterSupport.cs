using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace GungeonOnline
{
    // Characters added by the Custom Characters mod (KyleTheScientist), e.g. The Lamb.
    // Such a character reports a base character's identity (The Lamb says "Hunter"), so the partner drew the base
    // character. Now the custom character's name is sent too, and the other game looks up that character's own
    // prefab in Custom Characters' list (CharacterBuilder.storedCharacters: name -> (data, prefab)). That needs
    // the same character mod on both PCs; without it the base character is shown as before.
    // Everything goes through reflection, so Gungeon Online doesn't need Custom Characters installed.
    public static class CustomCharacterSupport
    {
        private static bool looked;
        private static IDictionary stored;          // CharacterBuilder.storedCharacters
        private static Type customCharacterType;    // CustomCharacters.CustomCharacter (component on the player)
        private static FieldInfo dataField, nameInternalField, nameField;

        private static void Find()
        {
            if (looked)
                return;
            looked = true;
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type builder = a.GetType("CustomCharacters.CharacterBuilder", false);
                    if (builder == null)
                        continue;
                    FieldInfo f = builder.GetField("storedCharacters", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    stored = f != null ? f.GetValue(null) as IDictionary : null;
                    customCharacterType = a.GetType("CustomCharacters.CustomCharacter", false);
                    Type dataType = a.GetType("CustomCharacters.CustomCharacterData", false);
                    if (customCharacterType != null)
                        dataField = customCharacterType.GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (dataType != null)
                    {
                        nameInternalField = dataType.GetField("nameInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        nameField = dataType.GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    Plugin.Log.LogInfo("Custom Characters mod found: " + (stored != null ? stored.Count : 0) + " custom characters.");
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Custom Characters support: " + e.Message);
                stored = null;
            }
        }

        // Our character's custom name, or "" for a normal character.
        public static string LocalName(PlayerController player)
        {
            Find();
            if (player == null || customCharacterType == null || dataField == null)
                return "";
            Component c = player.GetComponent(customCharacterType);
            if (c == null)
                return "";
            object data = dataField.GetValue(c);
            string name = NameOf(data);
            if (name != lastLocalName)
            {
                lastLocalName = name;
                Plugin.Log.LogInfo("Playing custom character '" + name + "': the partner will see it if they have it too"
                    + (FindPrefab(name) != null ? "." : " (couldn't find it in our own list!)."));
            }
            return name;
        }

        private static string lastLocalName = "";

        private static string NameOf(object data)
        {
            if (data == null)
                return "";
            string n = nameInternalField != null ? nameInternalField.GetValue(data) as string : null;
            if (string.IsNullOrEmpty(n) && nameField != null)
                n = nameField.GetValue(data) as string;
            return n ?? "";
        }

        // The custom character's player prefab here, or null (not installed here, or not a custom character).
        public static GameObject FindPrefab(string name)
        {
            Find();
            if (string.IsNullOrEmpty(name) || stored == null)
                return null;
            foreach (DictionaryEntry entry in stored)
            {
                object pair = entry.Value;
                if (pair == null)
                    continue;
                object data = Member(pair, "First");
                string key = entry.Key as string;
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(NameOf(data), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Member(pair, "Second") as GameObject;
            }
            return null;
        }

        // The game's Tuple has public fields First/Second.
        private static object Member(object o, string name)
        {
            Type t = o.GetType();
            FieldInfo f = t.GetField(name);
            if (f != null)
                return f.GetValue(o);
            PropertyInfo p = t.GetProperty(name);
            return p != null ? p.GetValue(o, null) : null;
        }

        // The prefab to draw the partner from: their custom character if we have it, else the base character.
        public static GameObject PartnerPrefab(RemotePlayerState remote, out string description)
        {
            GameObject custom = FindPrefab(remote.CustomCharacter);
            if (custom != null)
            {
                description = "custom character " + remote.CustomCharacter;
                return custom;
            }
            string path = CharacterSelectController.GetCharacterPathFromIdentity((PlayableCharacters)remote.Identity);
            description = path + (remote.CustomCharacter.Length > 0 ? " (their custom character " + remote.CustomCharacter + " isn't installed here)" : "");
            return BraveResources.Load(path, ".prefab") as GameObject;
        }
    }
}
