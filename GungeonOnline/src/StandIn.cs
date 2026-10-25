using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // A guest who leaves (disconnects or quits) mid-run doesn't vanish: the host's game spawns their character as a
    // bot, with the gear the host remembers for them (InventorySync), and everyone keeps seeing them play.
    //
    // How: the host's game goes into the game's own local co-op for a while - the stand-in is a real second player
    // (GameManager.SecondaryPlayer, player 2's controls), driven by an autopilot Brain. The host sends its position,
    // animation and shots to the other guests as the missing player's slot, so on their screens nothing changes.
    // Enemies, doors, deaths and chest revives work the game's own co-op way in the host's game.
    //
    // When the player comes back, the stand-in's current gear and health become their remembered gear, it disappears,
    // and they rejoin as that character with the bot still playing for them until they move (InventorySync).
    //
    // The game has room for one extra player, so there's one stand-in at a time.
    public static class StandIn
    {
        private static NetSession session;
        private static PlayerController bot;
        private static Brain brain;
        private static int botSlot = -1;
        private static string botName = "";
        private static string botKey = "";   // the player's lasting identity (Steam ID, or name): gear is remembered by it
        private static float nextStateAt, nextRememberAt, nextHudCheckAt;
        // The stand-in has the player's real gear on (until then it only has the character's starting kit, which
        // must never be remembered as theirs - that lost players their guns).
        private static bool geared;
        // Where the stand-in last was, per player name, to put the returning player there.
        private static readonly System.Collections.Generic.Dictionary<string, Vector2> handedBack =
            new System.Collections.Generic.Dictionary<string, Vector2>();

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(StandIn));
        }

        public static bool Active
        {
            get { return bot != null; }
        }

        public static int Slot
        {
            get { return bot != null ? botSlot : -1; }
        }

        public static string Name
        {
            get { return botName; }
        }

        public static PlayerController Player
        {
            get { return bot; }
        }

        // ---- Coming and going ----

        // Host: a guest left. If they were in our run and we remember their gear, their character plays on.
        public static void OnGuestLeft(int slot, string name, string key, RemotePlayerState last)
        {
            if (bot != null || !GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (gm.IsFoyer || gm.IsLoadingLevel || gm.PrimaryPlayer == null || gm.SecondaryPlayer != null || gm.CurrentRunSeed == 0)
                return;
            string character = InventorySync.RememberedCharacter(key, gm.CurrentRunSeed);
            if (character == null)
            {
                Plugin.Log.LogInfo(name + " left, but we don't know their gear in this run: no stand-in.");
                return;
            }
            GameObject prefab = CustomCharacterSupport.FindPrefab(character);
            if (prefab == null)
                prefab = BraveResources.Load(character, ".prefab") as GameObject;
            if (prefab == null || prefab.GetComponent<PlayerController>() == null)
            {
                Plugin.Log.LogWarning("Stand-in: can't find the character " + character + " for " + name + ".");
                return;
            }
            Vector3 at = gm.PrimaryPlayer.transform.position;
            if (last != null && last.HasPlayer && last.Scene == LevelSync.CurrentLevelKey() && !last.IsDead)
                at = last.FeetPosition + last.RootOffset;
            try
            {
                Spawn(prefab, at, slot, name, key);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Stand-in for " + name + " failed: " + e);
                Remove(false);
            }
        }

        private static void Spawn(GameObject prefab, Vector3 at, int slot, string name, string key)
        {
            GameManager gm = GameManager.Instance;
            gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            gm.PrimaryPlayer.ReinitializeMovementRestrictors();
            GameObject go = (GameObject)Object.Instantiate(prefab, at, Quaternion.identity);
            go.SetActive(true);
            bot = go.GetComponent<PlayerController>();
            bot.ActorName = "Player ID 1";
            bot.PlayerIDX = 1;
            gm.SecondaryPlayer = bot;
            gm.RefreshAllPlayers();
            botSlot = slot;
            botName = name;
            botKey = key;
            geared = false;
            brain = new Brain(1, name);
            brain.Start(false);
            Autopilot.Add(brain);
            gm.StartCoroutine(FinishSpawn(bot, key));
            Plugin.Log.LogInfo(name + " left: their character keeps playing as a bot (stand-in in player " + (slot + 1) + "'s place).");
            Toast.Show(name + " left - a bot plays their character until they're back.", 5f);
            session.SendRosterNow();
        }

        // The rest of the game's co-op set-up happens a frame later, then the remembered gear goes on.
        private static System.Collections.IEnumerator FinishSpawn(PlayerController who, string key)
        {
            yield return null;
            if (who == null || who != bot)
                yield break;
            GameManager gm = GameManager.Instance;
            // The game needs its co-op HUD set up for a second player (reload bar etc.), but the host keeps its
            // one-player layout (see AfterCoopHud) and never sees the bot's half.
            if (GameUIRoot.Instance != null)
                GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            HideSecondHud();
            if (gm.MainCameraController != null)
                gm.MainCameraController.ClearPlayerCache();
            if (PhysicsEngine.HasInstance)
                PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(who.specRigidbody, null, false);
            BraveInput.ReassignAllControllers(null);
            // Let the character hand out its starting kit first.
            float until = Time.realtimeSinceStartup + 0.6f;
            while (Time.realtimeSinceStartup < until)
                yield return null;
            if (who == null || who != bot)
                yield break;
            int given = InventorySync.ApplyRemembered(key, who);
            geared = true;
            Plugin.Log.LogInfo("Stand-in for " + botName + " got their gear back (" + given + " items).");
        }

        // Host: the player is back (said hello again).
        public static void OnHello(int slot, string key)
        {
            if (bot == null)
                return;
            if (key == botKey)
            {
                Plugin.Log.LogInfo(botName + " is back: their stand-in hands over.");
                handedBack[key] = bot.transform.position;
                Remove(true);
            }
            else if (slot == botSlot)
            {
                // Someone else took that player's place: the stand-in goes (their gear stays remembered).
                Plugin.Log.LogInfo("Someone else took " + botName + "'s place: the stand-in leaves.");
                Remove(true);
            }
        }

        // Where the returning player's stand-in was (once).
        public static bool TakeHandBack(string key, out Vector2 at)
        {
            at = Vector2.zero;
            Vector2 v;
            if (!handedBack.TryGetValue(key, out v))
                return false;
            handedBack.Remove(key);
            at = v;
            return true;
        }

        public static void Remove(bool remember)
        {
            PlayerController who = bot;
            Brain b = brain;
            bot = null;
            brain = null;
            if (b != null)
            {
                b.Stop("stand-in removed");
                Autopilot.Remove(b);
            }
            if (!GameManager.HasInstance)
                return;
            GameManager gm = GameManager.Instance;
            if (who != null)
            {
                if (remember && geared && !who.healthHaver.IsDead && !who.IsGhost)
                    InventorySync.Remember(botKey, who);
                who.SetInputOverride("stand-in removed");
                Object.Destroy(who.gameObject);
            }
            if (gm.SecondaryPlayer == who || gm.SecondaryPlayer == null)
            {
                gm.SecondaryPlayer = null;
                gm.RefreshAllPlayers();
                gm.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
                if (gm.PrimaryPlayer != null)
                    gm.PrimaryPlayer.ReinitializeMovementRestrictors();
                HideSecondHud();
                if (gm.MainCameraController != null)
                    gm.MainCameraController.ClearPlayerCache();
                BraveInput.ReassignAllControllers(null);
            }
            if (who != null && session != null)
                session.SendRosterNow();
        }

        // The game rearranges the HUD for two players whenever a second player is in (on spawn and on every new
        // floor). Online the second player is our bot: keep the host's one-player HUD.
        // It moves our item box (and its name label) over beside player 2's gun box; put them back afterwards.
        private struct HudSpot
        {
            public bool Saved;
            public Vector3 ItemBox;
            public Vector3 ItemLabel;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameUIRoot), "ConvertCoreUIToCoopMode")]
        private static void BeforeCoopHud(GameUIRoot __instance, out HudSpot __state)
        {
            __state = new HudSpot();
            if (bot == null || __instance.itemControllers == null || __instance.itemControllers.Count == 0
                || __instance.itemNameLabels == null || __instance.itemNameLabels.Count == 0)
                return;
            __state.Saved = true;
            __state.ItemBox = __instance.itemControllers[0].transform.position;
            __state.ItemLabel = __instance.itemNameLabels[0].RelativePosition;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameUIRoot), "ConvertCoreUIToCoopMode")]
        private static void AfterCoopHud(GameUIRoot __instance, HudSpot __state)
        {
            if (!__state.Saved)
                return;
            __instance.itemControllers[0].transform.position = __state.ItemBox;
            __instance.itemNameLabels[0].RelativePosition = __state.ItemLabel;
        }

        // Never fill in the bot's side of the HUD in the first place (hiding it afterwards didn't hold).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameUIRoot), "UpdatePlayerHealthUI")]
        private static bool BeforeHealthHud(PlayerController __0)
        {
            return bot == null || __0 != bot;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameUIRoot), "UpdatePlayerBlankUI")]
        private static bool BeforeBlankHud(PlayerController __0)
        {
            return bot == null || __0 != bot;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameUIRoot), "UpdateGunData")]
        private static bool BeforeGunHud(PlayerController __2)
        {
            return bot == null || __2 != bot;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameUIRoot), "UpdateItemData")]
        private static bool BeforeItemHud(PlayerController __0)
        {
            return bot == null || __0 != bot;
        }

        private static readonly System.Reflection.FieldInfo heartsField = AccessTools.Field(typeof(GameUIHeartController), "extantHearts");
        private static readonly System.Reflection.FieldInfo armorsField = AccessTools.Field(typeof(GameUIHeartController), "extantArmors");
        private static readonly System.Reflection.FieldInfo blanksField = AccessTools.Field(typeof(GameUIBlankController), "extantBlanks");

        // Whatever the bot's (right-hand) heart and blank rows already show: removed.
        private static void ClearSecondRows(GameUIRoot ui)
        {
            if (ui.heartControllers != null)
            {
                foreach (GameUIHeartController hc in ui.heartControllers)
                {
                    if (hc == null || !hc.IsRightAligned)
                        continue;
                    System.Collections.IList hearts = heartsField != null ? heartsField.GetValue(hc) as System.Collections.IList : null;
                    for (int guard = 0; hearts != null && hearts.Count > 0 && guard < 50; guard++)
                        hc.RemoveHeart();
                    System.Collections.IList armors = armorsField != null ? armorsField.GetValue(hc) as System.Collections.IList : null;
                    for (int guard = 0; armors != null && armors.Count > 0 && guard < 50; guard++)
                        hc.RemoveArmor();
                }
            }
            if (ui.blankControllers != null)
            {
                foreach (GameUIBlankController bc in ui.blankControllers)
                {
                    if (bc == null || !bc.IsRightAligned)
                        continue;
                    System.Collections.IList blanks = blanksField != null ? blanksField.GetValue(bc) as System.Collections.IList : null;
                    if (blanks != null && blanks.Count > 0)
                        bc.UpdateBlanks(0);
                }
            }
        }

        // Player 2's side of the HUD (hearts, blanks, gun and item boxes, their name labels): the bot's, not for the host.
        private static void HideSecondHud()
        {
            GameUIRoot ui = GameUIRoot.Instance;
            if (ui == null)
                return;
            // Hearts and blanks are new sprites each time they change: look the pieces up again now and then.
            if (Time.realtimeSinceStartup >= nextHudCheckAt)
            {
                nextHudCheckAt = Time.realtimeSinceStartup + 0.25f;
                hudParts.Clear();
            }
            if (bot != null)
            {
                try { ClearSecondRows(ui); }
                catch (System.Exception) { }
            }
            HideSecond(ui.heartControllers);
            HideSecond(ui.blankControllers);
            HideSecond(ui.ammoControllers);
            HideSecond(ui.itemControllers);
            HideSecond(ui.gunNameLabels);
            HideSecond(ui.itemNameLabels);
        }

        private static readonly System.Collections.Generic.Dictionary<Component, dfControl[]> hudParts =
            new System.Collections.Generic.Dictionary<Component, dfControl[]>();

        private static void HideSecond(System.Collections.IList list)
        {
            if (list == null || list.Count < 2)
                return;
            Component c = list[1] as Component;
            if (c == null)
                return;
            // The game shows these again whenever it updates them, so they're made see-through as well as hidden,
            // every frame (their own panel and anything under it).
            dfControl[] controls;
            if (!hudParts.TryGetValue(c, out controls))
            {
                controls = c.GetComponentsInChildren<dfControl>(true);
                hudParts[c] = controls;
            }
            foreach (dfControl control in controls)
            {
                if (control == null)
                    continue;
                if (control.IsVisible)
                    control.IsVisible = false;
                if (control.Opacity > 0f)
                    control.Opacity = 0f;
            }
        }

        // ---- While it plays ----

        // Some holes take whoever falls in to another level (the Black Powder Mine's elevator shaft, the Rat's lair).
        // The game lets one player's fall start that load; a bot following the host in fell at the same moment, both
        // started the load, and the host's game froze. The bot never takes the group anywhere: its fall just puts it
        // back on solid ground, and the host's fall still takes everyone (bot included) along.
        private static readonly System.Reflection.FieldInfo cachedPitLevelField =
            AccessTools.Field(typeof(PlayerController), "m_cachedLevelToLoadOnPitfall");

        private static void ClearPitLevel(PlayerController who)
        {
            if (who == null)
                return;
            if (!string.IsNullOrEmpty(who.LevelToLoadOnPitfall))
                who.LevelToLoadOnPitfall = string.Empty;
            if (cachedPitLevelField != null && !string.IsNullOrEmpty(cachedPitLevelField.GetValue(who) as string))
                cachedPitLevelField.SetValue(who, string.Empty);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerController), "PitRespawn")]
        private static void BeforePitRespawn(PlayerController __instance)
        {
            if (bot != null && __instance == bot)
            {
                bool hadLevel = !string.IsNullOrEmpty(__instance.LevelToLoadOnPitfall)
                    || (cachedPitLevelField != null && !string.IsNullOrEmpty(cachedPitLevelField.GetValue(__instance) as string));
                ClearPitLevel(__instance);
                if (hadLevel)
                    Plugin.Log.LogInfo("Stand-in fell into a hole that leads to another level: it just climbs back out (only the host's fall loads).");
            }
        }

        public static void Update()
        {
            if (bot == null)
                return;
            ClearPitLevel(bot);
            if (session == null || session.Link == null || !session.Link.IsHost)
            {
                Remove(false);
                return;
            }
            float now = Time.realtimeSinceStartup;
            // Its gear as it is now is what the player gets back.
            if (geared && now >= nextRememberAt && !bot.healthHaver.IsDead && !bot.IsGhost)
            {
                nextRememberAt = now + 2f;
                InventorySync.Remember(botKey, bot);
            }
            // The game may show player 2's HUD again (floor change, co-op events): keep it hidden.
            HideSecondHud();
            if (now >= nextStateAt)
            {
                nextStateAt = now + 1f / 30f;
                session.SendStateAs(botSlot, bot, true);
            }
        }

        // ---- The game's co-op rules, adjusted for a bot ----

        // The camera stays on the host (a bot wandering off shouldn't drag the view around) - while the host is alive.
        // The camera skips ghosts too, so with the host a ghost it had nobody to follow and showed only black.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerController), "get_IgnoredByCamera")]
        private static void AfterIgnoredByCamera(PlayerController __instance, ref bool __result)
        {
            if (bot == null || __instance != bot)
                return;
            PlayerController host = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (host != null && !host.IsGhost)
                __result = true;
        }

        // Co-op doors wait for both players to be near; the bot counts as always there.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DungeonDoorController), "GetDistanceToPlayer")]
        private static void AfterDoorDistance(SpeculativeRigidbody playerRigidbody, ref float __result)
        {
            if (bot != null && playerRigidbody == bot.specRigidbody)
                __result = 0f;
        }

        // A run ending (back to the Breach, a new run, the title) ends the stand-in too.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "ClearActiveGameData")]
        private static void BeforeClearGameData()
        {
            if (bot != null)
                Remove(false);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), "LoadCharacterSelect")]
        private static void BeforeLoadCharacterSelect()
        {
            if (bot != null)
                Remove(false);
        }
    }
}
