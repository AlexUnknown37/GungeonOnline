using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace GungeonOnline
{
    // In the Breach the Cultist normally starts local co-op (a second player on this PC) and only shows up when a
    // controller is plugged in. While playing online that makes no sense, so instead:
    // - the Cultist is always there, and
    // - talking to them makes YOU the Cultist, like picking any other character.
    // The Cultist's script runs a "change co-op mode" step (ChangeCoopMode) with the Cultist's character file;
    // online, that step runs the game's normal "change to this character" step (ChangeToNewCharacter) instead.
    public static class BreachCultist
    {
        private static NetSession session;
        private static readonly System.Reflection.MethodInfo toggleSelf = AccessTools.Method(typeof(FoyerCharacterSelectFlag), "ToggleSelf");
        private static readonly System.Reflection.FieldInfo activeField = AccessTools.Field(typeof(FoyerCharacterSelectFlag), "m_active");

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(BreachCultist));
        }

        private static bool Online
        {
            get { return session != null && session.Link != null; }
        }

        // Keep the Cultist standing in the Breach while online, controller or not.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FoyerCharacterSelectFlag), "Update")]
        private static bool BeforeFlagUpdate(FoyerCharacterSelectFlag __instance)
        {
            if (!Online || !__instance.IsCoopCharacter)
                return true;
            if (!(bool)activeField.GetValue(__instance))
                toggleSelf.Invoke(__instance, new object[] { true });
            return false;
        }

        // The character-select screen (picking a character when you arrive in the Breach) only lists normal
        // characters; the Cultist is marked as the co-op character. Online, make it a normal character before the
        // list is built, with its own character file, so picking it loads the Cultist like anyone else.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Foyer), "SetUpCharacterCallbacks")]
        private static void BeforeCharacterSetup()
        {
            if (!Online)
                return;
            foreach (FoyerCharacterSelectFlag flag in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
            {
                if (flag == null || !flag.IsCoopCharacter)
                    continue;
                flag.IsCoopCharacter = false;
                if (string.IsNullOrEmpty(flag.CharacterPrefabPath))
                    flag.CharacterPrefabPath = CharacterSelectController.GetCharacterPathFromIdentity(PlayableCharacters.CoopCultist);
                if (!(bool)activeField.GetValue(flag))
                    toggleSelf.Invoke(flag, new object[] { true });
                // The select screen's mouse hover reads each character's CharacterSelectIdleDoer (for its sprite);
                // the Cultist has none. Give it one, switched off so it doesn't play any idle animations.
                if (flag.GetComponent<CharacterSelectIdleDoer>() == null)
                {
                    CharacterSelectIdleDoer idle = flag.gameObject.AddComponent<CharacterSelectIdleDoer>();
                    idle.enabled = false;
                }
                // The co-op Cultist has no info card (name + items over their head) and the select screen crashes
                // without one. Borrow another character's card; it's relabelled when shown (AfterCreateCard).
                if (flag.OverheadElement == null)
                {
                    foreach (FoyerCharacterSelectFlag other in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                    {
                        if (other != null && other != flag && other.OverheadElement != null)
                        {
                            flag.OverheadElement = other.OverheadElement;
                            cultistFlag = flag;
                            break;
                        }
                    }
                }
                Plugin.Log.LogInfo("Online: the Cultist (" + flag.CharacterPrefabPath + ") can be picked as a character."
                    + " overhead " + (flag.OverheadElement != null) + ", sprite " + (flag.sprite != null)
                    + ", talkDoer " + (flag.talkDoer != null) + ", object " + flag.name);
            }
        }

        private static FoyerCharacterSelectFlag cultistFlag;
        private static readonly System.Reflection.FieldInfo extantCardField = AccessTools.Field(typeof(FoyerCharacterSelectFlag), "m_extantOverheadUIElement");

        // The borrowed card: show "The Cultist" and hide the other character's face and items.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FoyerCharacterSelectFlag), "CreateOverheadElement")]
        private static void AfterCreateCard(FoyerCharacterSelectFlag __instance)
        {
            if (__instance != cultistFlag || cultistFlag == null)
                return;
            dfControl card = extantCardField.GetValue(__instance) as dfControl;
            if (card == null)
                return;
            bool named = false;
            foreach (dfLabel label in card.GetComponentsInChildren<dfLabel>(true))
            {
                if (!named)
                {
                    label.Text = "The Cultist";
                    named = true;
                }
                else
                {
                    label.IsVisible = false;
                }
            }
            foreach (dfSprite s in card.GetComponentsInChildren<dfSprite>(true))
                s.IsVisible = false;
        }

        // Walking up to the Cultist in the Breach: skip his "join co-op?" conversation and become him right away.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TalkDoerLite), "Interact")]
        private static bool BeforeTalkToCultist(TalkDoerLite __instance)
        {
            if (!Online || __instance == null || !__instance.name.Contains("FoyerCharacter_Cultist") || !GameManager.HasInstance || !GameManager.Instance.IsFoyer)
                return true;
            FoyerCharacterSelectFlag flag = __instance.GetComponent<FoyerCharacterSelectFlag>();
            string path = flag != null && !string.IsNullOrEmpty(flag.CharacterPrefabPath)
                ? flag.CharacterPrefabPath
                : CharacterSelectController.GetCharacterPathFromIdentity(PlayableCharacters.CoopCultist);
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            if (me != null && me.characterIdentity == PlayableCharacters.CoopCultist)
                return false; // already him
            PlayMakerFSM fsm = __instance.GetComponent<PlayMakerFSM>();
            HutongGames.PlayMaker.FsmState state = fsm != null && fsm.Fsm != null ? (fsm.Fsm.ActiveState ?? (fsm.Fsm.States.Length > 0 ? fsm.Fsm.States[0] : null)) : null;
            if (state == null)
                return true;
            Plugin.Log.LogInfo("Online: talking to the Cultist picks him as our character.");
            ChangeToNewCharacter change = new ChangeToNewCharacter();
            change.PlayerPrefabPath = path;
            change.Init(state);
            change.OnEnter();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChangeCoopMode), "OnEnter")]
        private static bool BeforeChangeCoopMode(ChangeCoopMode __instance)
        {
            if (!Online)
                return true;
            // "Is co-op possible?" check: online, the answer is yes (we become the Cultist ourselves).
            if (__instance.IsTestCoopValid)
            {
                __instance.Fsm.Event(__instance.IfCoopValidEvent);
                __instance.Finish();
                return false;
            }
            // Joining co-op as the Cultist: become the Cultist instead.
            if (__instance.TargetCoopMode && !string.IsNullOrEmpty(__instance.PlayerPrefabPath))
            {
                Plugin.Log.LogInfo("Online: picking the Cultist as our character instead of starting local co-op.");
                ChangeToNewCharacter change = new ChangeToNewCharacter();
                change.PlayerPrefabPath = __instance.PlayerPrefabPath;
                change.Init(__instance.State);
                change.OnEnter();
                return false;
            }
            // Leaving co-op: nothing to leave online.
            __instance.Finish();
            return false;
        }
    }
}
