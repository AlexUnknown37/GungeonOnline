using System.IO;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // The ending online: a duel, then the winner's past.
    //
    // In the base game, shooting a past with the Gun That Can Kill The Past (from the ark after the High Dragun) loads
    // that character's past; in local co-op, shooting the other player loads the Cultist's past (fs_coop), a duel
    // between the two players. Online every player has their own game, so the pasts broke (guests followed the host
    // into the host's past, or waited forever for the host).
    // Now, online: whoever fires the gun first sends everyone to the duel arena (fs_coop). The game's own duel script
    // (two players in one game) is switched off; ours spreads the players out, counts down and turns PvP on. The host
    // counts who's still standing. The last one goes to their own past. The others watch: they load the same past as
    // hidden spectators that can't be hurt (their copy of the past's own script stays off, so it doesn't play out for
    // their character), with the camera on the winner; when the winner is done (or dies), they go back to the Breach.
    public static class DuelSync
    {
        private const string Arena = "fs_coop";
        private const float CountdownTime = 3f;
        private const float ArrivalTimeout = 15f;

        private static NetSession session;
        private static bool inArena;
        private static float arrivedAt;
        private static bool started;        // the countdown is over: PvP on
        private static bool decided;        // a winner was announced
        private static float toBreachAt = -1f;

        // While true, our next special-level load is our own past: not held for the host, not sent to anyone.
        private static bool privateLoad;
        // We're in the winner's past (as the winner, or watching): nothing from the host may pull us out.
        public static bool InPrivatePast;
        // We lost the duel and watch the winner's past.
        public static bool Spectating;
        private static int watchedSlot = -1;
        private static bool sawWinnerHere, revived;
        private static float spectateStartedAt, spectateLeaveAt = -1f;

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(DuelSync));
            // Every character's past has its own script (intro, boss, ending) made for that character; while we only
            // watch someone else's past, ours stays off.
            HarmonyMethod skip = new HarmonyMethod(typeof(DuelSync), "SkipWhileSpectating");
            foreach (System.Type t in typeof(CoopPastController).Assembly.GetTypes())
            {
                if (t == typeof(CoopPastController) || !t.Name.EndsWith("PastController"))
                    continue;
                foreach (string m in new string[] { "Start", "Update" })
                {
                    System.Reflection.MethodInfo method = AccessTools.DeclaredMethod(t, m);
                    if (method != null)
                        harmony.Patch(method, skip);
                }
            }
        }

        private static bool SkipWhileSpectating()
        {
            return !Spectating;
        }

        private static bool Online
        {
            get
            {
                if (session == null || session.Status != NetSession.State.Connected)
                    return false;
                foreach (RemotePlayerState p in session.Remotes)
                {
                    if (p.HasPlayer)
                        return true;
                }
                return false;
            }
        }

        // The duel ending owns the run (in the arena, or the winner in their past): no game over from the host.
        public static bool HoldsRun
        {
            get { return inArena || InPrivatePast; }
        }

        // PvP is on for the duel (copied bullets hit players).
        public static bool Active
        {
            get { return inArena && started && !decided; }
        }

        // ---- The ark: from LevelSync's special-level hook ----

        // True: this load was a past and is replaced by the duel (don't load it).
        public static bool RedirectPast(GameManager gm, string custom)
        {
            if (privateLoad)
                return false;
            if (string.IsNullOrEmpty(custom) || !custom.StartsWith("fs_") || custom == Arena || !Online)
                return false;
            if (inArena || duelStarted)
                return true;
            if (session.Link.IsHost)
            {
                Plugin.Log.LogInfo("The Gun That Can Kill The Past was fired (" + custom + "): everyone goes to the duel.");
                StartDuel(gm);
            }
            else
            {
                Plugin.Log.LogInfo("The Gun That Can Kill The Past was fired (" + custom + "): asking the host to start the duel.");
                session.SendReliable(MsgType.DuelRequest, null);
            }
            return true;
        }

        // From LevelSync's special-level hook: our own past (the duel's winner) loads without the host.
        public static bool TakePrivateLoad()
        {
            if (!privateLoad)
                return false;
            privateLoad = false;
            InPrivatePast = true;
            return true;
        }

        public static void OnDuelRequest()
        {
            if (session.Link.IsHost && GameManager.HasInstance && !inArena && !GameManager.Instance.IsLoadingLevel)
            {
                Plugin.Log.LogInfo(session.PeerName + " fired the Gun That Can Kill The Past: everyone goes to the duel.");
                StartDuel(GameManager.Instance);
            }
        }

        private static bool duelStarted;   // host: the duel was started (once per ending, even if the gun fires twice)

        private static void StartDuel(GameManager gm)
        {
            if (duelStarted)
                return;
            duelStarted = true;
            if (StandIn.Active)
                StandIn.Remove(true); // the duel is between the players themselves
            Toast.Show("The past awaits... but only one of you. Duel!", 4f);
            gm.LoadCustomLevel(Arena); // LevelSync sends it: everyone follows
        }

        // ---- The game's own duel script: off online ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CoopPastController), "Start")]
        private static bool BeforeCoopPastStart()
        {
            return !Online;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CoopPastController), "Update")]
        private static bool BeforeCoopPastUpdate()
        {
            return !Online;
        }

        // ---- The duel ----

        public static void Update()
        {
            GameManager gm = GameManager.HasInstance ? GameManager.Instance : null;
            if (gm == null)
                return;
            bool arenaLoaded = Object.FindObjectOfType<CoopPastController>() != null;
            if (gm.IsFoyer && !arenaLoaded && LevelSync.CurrentLevelKey().StartsWith("tt_foyer"))
            {
                InPrivatePast = false;
                duelStarted = false;
                StopSpectating(gm.PrimaryPlayer);
                Leave();
                return;
            }
            if (Spectating && InPrivatePast && !gm.IsLoadingLevel && gm.PrimaryPlayer != null)
            {
                Spectate(gm, gm.PrimaryPlayer, Time.realtimeSinceStartup);
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (toBreachAt > 0f && now >= toBreachAt && !gm.IsLoadingLevel)
            {
                toBreachAt = -1f;
                Leave();
                gm.LoadCharacterSelect(true, false);
                return;
            }
            PlayerController me = gm.PrimaryPlayer;
            bool here = Online && !gm.IsLoadingLevel && me != null && arenaLoaded;
            if (!here)
            {
                if (inArena && !decided)
                    Leave();
                return;
            }
            if (!inArena)
                Arrive(me, now);
            if (gm.IsFoyer)
                gm.IsFoyer = false;
            if (!started)
            {
                if (now - arrivedAt >= CountdownTime && (EveryoneArrived() || now - arrivedAt >= ArrivalTimeout))
                {
                    started = true;
                    GameManager.PVP_ENABLED = true;
                    Plugin.Log.LogInfo("Duel: fight!");
                    Toast.Show("FIGHT! Last one standing faces their past.", 3f);
                }
                return;
            }
            if (!decided && session.Link.IsHost)
                CheckForWinner();
        }

        private static void Arrive(PlayerController me, float now)
        {
            inArena = true;
            started = decided = false;
            arrivedAt = now;
            Plugin.Log.LogInfo("Duel: we're in the arena.");
            // The arena is built from a copy of the Breach, so the game treats it as one (no guns, no shooting); its
            // own duel script switches that off, and ours does the same. Each player starts at their gun stand: the
            // two the arena has (players 1 and 2, lower floor) and two copies on the upper floor (players 3 and 4).
            me.CurrentInputState = PlayerInputState.AllInput;
            if (me.inventory != null)
                me.inventory.ForceNoGun = false;
            AddUpperStands();
            int slot = Mathf.Clamp(session.LocalSlot, 0, Starts.Length - 1);
            me.WarpToPoint(Starts[slot], false, false);
            Toast.Show("Duel! Get ready...", CountdownTime);
        }

        // Where each player number starts (the arena's layout never changes): in front of their gun stand.
        private static readonly Vector2[] Starts =
        {
            new Vector2(27f, 22.5f), new Vector2(47f, 22.5f),     // lower floor, by the arena's own stands
            new Vector2(27.1f, 57.5f), new Vector2(48.1f, 57.5f), // upper floor, by the copies
        };
        // The upper floor's two extra gun stands (players 3 and 4), picked by standing there in the arena.
        private static readonly Vector2[] UpperStands = { new Vector2(27.1f, 55.3f), new Vector2(48.1f, 55.3f) };

        private static void AddUpperStands()
        {
            RewardPedestal original = null;
            foreach (RewardPedestal p in Object.FindObjectsOfType<RewardPedestal>())
            {
                if (p != null && p.name.StartsWith("Coop_Gun_Pedestal"))
                    original = p;
            }
            if (original == null)
            {
                Plugin.Log.LogWarning("Duel: the arena's gun stands weren't found; no extra stands for players 3 and 4.");
                return;
            }
            foreach (Vector2 at in UpperStands)
            {
                GameObject copy = (GameObject)Object.Instantiate(original.gameObject, new Vector3(at.x, at.y, at.y), Quaternion.identity);
                RewardPedestal stand = copy.GetComponent<RewardPedestal>();
                RoomHandler room = GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(new IntVector2(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.y)));
                if (stand != null && room != null)
                    room.RegisterInteractable(stand);
            }
            Plugin.Log.LogInfo("Duel: added the upper floor's two gun stands.");
        }

        // Everyone still in the run is in the arena with us.
        private static bool EveryoneArrived()
        {
            string level = LevelSync.CurrentLevelKey();
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (p.HasPlayer && p.Scene != level)
                    return false;
            }
            return true;
        }

        // Host: one player left standing in the arena wins (nobody: everyone fell at once, no winner).
        private static void CheckForWinner()
        {
            string level = LevelSync.CurrentLevelKey();
            int alive = 0, last = -1, fighters = 0;
            PlayerController me = GameManager.Instance.PrimaryPlayer;
            fighters++;
            if (me != null && !me.IsGhost && !me.healthHaver.IsDead)
            {
                alive++;
                last = session.LocalSlot;
            }
            foreach (RemotePlayerState p in session.Remotes)
            {
                if (!p.HasPlayer || p.Scene != level)
                    continue;
                fighters++;
                if (!p.IsDead)
                {
                    alive++;
                    last = p.Slot;
                }
            }
            if (fighters < 2 || alive > 1)
                return;
            int winner = alive == 1 ? last : -1;
            session.SendReliable(MsgType.DuelWon, delegate (BinaryWriter w) { w.Write((sbyte)winner); });
            Decide(winner);
        }

        public static void OnDuelWon(BinaryReader r)
        {
            Decide(r.ReadSByte());
        }

        private static void Decide(int winner)
        {
            if (decided || !inArena)
                return;
            decided = true;
            GameManager.PVP_ENABLED = false;
            string name = winner == session.LocalSlot ? "You" : winner >= 0 ? session.Players[winner].Name : "";
            Plugin.Log.LogInfo(winner >= 0 ? "Duel won by " + name + "." : "Duel: nobody left standing.");
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (winner == session.LocalSlot && me != null)
            {
                string past = PastOf(me.characterIdentity);
                if (past != null)
                {
                    Toast.Show("You won the duel! Now face your past.", 4f);
                    privateLoad = true;
                    GameManager.Instance.DelayedLoadCustomLevel(2f, past);
                    return;
                }
                Toast.Show("You won the duel! (Your character has no past to face.)", 5f);
            }
            else
            {
                string theirPast = winner >= 0 ? PastOf((PlayableCharacters)session.Players[winner].Identity) : null;
                if (theirPast != null)
                {
                    Toast.Show(name + " won the duel! Watching them face their past...", 5f);
                    Spectating = true;
                    watchedSlot = winner;
                    sawWinnerHere = revived = false;
                    spectateStartedAt = Time.realtimeSinceStartup;
                    spectateLeaveAt = -1f;
                    privateLoad = true;
                    GameManager.Instance.DelayedLoadCustomLevel(2f, theirPast);
                    return;
                }
                Toast.Show(winner >= 0 ? name + " won the duel." : "Nobody left standing.", 5f);
            }
            toBreachAt = Time.realtimeSinceStartup + 5f;
        }

        // ---- Watching the winner's past ----

        private static void Spectate(GameManager gm, PlayerController me, float now)
        {
            // A ghost from the duel comes back first: the camera follows living players only.
            if (!revived && me.IsGhost)
            {
                revived = true;
                me.ResurrectFromChest(me.CenterPosition);
            }
            // Hidden, untouchable, hands off: just a camera on the winner.
            me.healthHaver.IsVulnerable = false;
            me.SetInputOverride("spectate");
            if (me.sprite != null && me.sprite.renderer != null)
                me.sprite.renderer.enabled = false; // also hides us in the others' games (they draw what's visible)
            me.ToggleGunRenderers(false, "spectate");
            me.ToggleHandRenderers(false, "spectate");
            me.ToggleShadowVisiblity(false);
            if (me.specRigidbody != null)
            {
                me.specRigidbody.CollideWithOthers = false;
                me.specRigidbody.CollideWithTileMap = false;
            }

            RemotePlayerState w = watchedSlot >= 0 && watchedSlot < session.Players.Length ? session.Players[watchedSlot] : null;
            bool winnerHere = w != null && w.Connected && w.HasPlayer && w.Scene == LevelSync.CurrentLevelKey();
            if (winnerHere)
                sawWinnerHere = true;
            // Done: the winner left the past (its ending, or back to the Breach), died, or never arrived.
            bool done = w == null || !w.Connected || (sawWinnerHere && (!winnerHere || w.IsDead)) || (!sawWinnerHere && now - spectateStartedAt > 30f);
            if (done)
            {
                if (spectateLeaveAt < 0f)
                {
                    spectateLeaveAt = now + 4f;
                    Toast.Show(w != null && w.IsDead ? w.Name + " fell to their past." : "The past is done.", 4f);
                }
                else if (now >= spectateLeaveAt)
                {
                    Plugin.Log.LogInfo("Duel: done watching the winner's past; back to the Breach.");
                    StopSpectating(me);
                    InPrivatePast = false;
                    gm.LoadCharacterSelect(true, false);
                }
                return;
            }
            if (!winnerHere)
                return;
            // Stay on the winner (where their character is drawn here), so the camera and the rooms follow them.
            Vector2 at = (w.Motion.HasData ? w.Motion.Sample() : w.FeetPosition) + w.RootOffset;
            if (Vector2.Distance(at, me.transform.position) > 0.1f)
            {
                me.transform.position = new Vector3(at.x, at.y, me.transform.position.z);
                if (me.specRigidbody != null)
                    me.specRigidbody.Reinitialize();
            }
        }

        private static void StopSpectating(PlayerController me)
        {
            if (!Spectating)
                return;
            Spectating = false;
            watchedSlot = -1;
            if (me != null)
            {
                me.ClearInputOverride("spectate");
                me.ToggleGunRenderers(true, "spectate");
                me.ToggleHandRenderers(true, "spectate");
            }
        }

        // The base game's past for each character (the ark's own choice); none for the Cultist or other characters.
        private static string PastOf(PlayableCharacters who)
        {
            switch (who.ToString())
            {
                case "Convict": return "fs_convict";
                case "Pilot": return "fs_pilot";
                case "Guide": return "fs_guide";
                case "Soldier": return "fs_soldier";
                case "Robot": return "fs_robot";
                case "Bullet": return "fs_bullet";
            }
            return null;
        }

        private static void Leave()
        {
            if (inArena && GameManager.PVP_ENABLED)
                GameManager.PVP_ENABLED = false;
            inArena = started = decided = false;
        }
    }
}
