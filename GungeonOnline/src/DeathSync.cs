using System.Collections;
using System.IO;
using System.Reflection;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Co-op deaths, the way the game's own local co-op does them:
    // - If you die while your partner is still alive, you become a ghost instead of getting a game over.
    // - While one player is a ghost, the next normal chest the living player opens brings them back
    //   (no loot from that chest), right at the chest.
    // - If the last living player dies too, every game shows the death screen.
    // With more than two players: a ghost comes back when any living player opens a chest (every ghost at once).
    //
    // The game only does this when it is in local co-op mode with two players in one game. Online each
    // game has one player, so we call the same functions ourselves: HandleCoopDeath (become a ghost) and
    // ResurrectFromChest (come back), and send PlayerDied / Revive / TeamWiped between the games.
    public static class DeathSync
    {
        private static NetSession session;
        private static readonly FieldInfo isFallingField = AccessTools.Field(typeof(GameActor), "m_isFalling");
        private static readonly MethodInfo handleDeathMethod = AccessTools.Method(typeof(PlayerController), "HandleDeath_CR");

        public static void Init(NetSession netSession, Harmony harmony)
        {
            session = netSession;
            harmony.PatchAll(typeof(DeathSync));
        }

        private static bool Connected
        {
            get { return session != null && session.Status == NetSession.State.Connected; }
        }

        // Is another player alive, in a run, on our floor? Only then can they come and get us.
        private static bool PartnerAliveHere
        {
            get
            {
                string level = LevelSync.CurrentLevelKey();
                foreach (RemotePlayerState r in session.Remotes)
                {
                    if (r.HasPlayer && !r.IsDead && r.Scene == level)
                        return true;
                }
                return false;
            }
        }

        // Is any other player a ghost?
        public static bool PartnerIsGhost
        {
            get
            {
                if (!Connected)
                    return false;
                foreach (RemotePlayerState r in session.Remotes)
                {
                    if (r.HasPlayer && r.IsDead)
                        return true;
                }
                return false;
            }
        }

        // Is every other player down (a ghost, dead) or without a character?
        private static bool EveryoneElseDown
        {
            get
            {
                foreach (RemotePlayerState r in session.Remotes)
                {
                    if (r.HasPlayer && !r.IsDead)
                        return false;
                }
                return true;
            }
        }

        // ---- Dying ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerController), "Die")]
        private static bool BeforeDie(PlayerController __instance)
        {
            if (!Connected || !GameManager.HasInstance || __instance != GameManager.Instance.PrimaryPlayer || __instance.IsGhost)
                return true;
            if (GameManager.Instance.IsFoyer)
                return true;

            if (PartnerAliveHere)
            {
                Plugin.Log.LogInfo("We died; partner is alive, so we become a ghost until they open a chest.");
                bool falling = (bool)isFallingField.GetValue(__instance);
                __instance.StartCoroutine(__instance.HandleCoopDeath(falling));
                session.SendReliable(MsgType.PlayerDied, null);
                return false;
            }

            // Everyone else is already a ghost (or gone): this is a normal game over, for all of us.
            if (PartnerIsGhost && EveryoneElseDown)
            {
                Plugin.Log.LogInfo("We died while everyone else is a ghost: game over for all.");
                session.SendReliable(MsgType.TeamWiped, null);
            }
            return true;
        }

        public static void OnPartnerDied()
        {
            Plugin.Log.LogInfo(session.PeerName + " died and is a ghost. Open a chest to bring them back.");
        }

        // The living partner died too: run the normal death (death screen) for our ghost.
        // ---- Everyone dead? ----
        // Checked every frame instead of relying only on the TeamWiped message: when both players die at the
        // same moment, each game still saw the other as alive, both became ghosts and nobody got a death screen.
        private const float AllDeadGrace = 1f;     // seconds both must be down before ending the run
        private const float PartnerGoneGrace = 3f; // a ghost whose partner disconnected
        private static float allDeadSince = -1f;

        public static void Update()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || !me.IsGhost || gameOverStarted || GameManager.Instance.IsLoadingLevel)
            {
                allDeadSince = -1f;
                return;
            }
            bool partnerDown = Connected ? EveryoneElseDown : true;
            if (!partnerDown)
            {
                allDeadSince = -1f;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (allDeadSince < 0f)
            {
                allDeadSince = now;
                return;
            }
            if (now - allDeadSince >= (Connected ? AllDeadGrace : PartnerGoneGrace))
            {
                Plugin.Log.LogInfo(Connected ? "Every player is down: game over." : "We're a ghost and everyone else is gone: game over.");
                ForceGameOver();
            }
        }

        public static void OnTeamWiped()
        {
            Plugin.Log.LogInfo(session.PeerName + " died last: game over.");
            ForceGameOver();
        }

        private static bool gameOverStarted;

        // Show our death screen (we're a ghost, or still turning into one, and the run is over).
        public static void ForceGameOver()
        {
            if (!GameManager.HasInstance)
                return;
            PlayerController p = GameManager.Instance.PrimaryPlayer;
            if (p == null || !(p.IsGhost || p.healthHaver.IsDead))
            {
                Plugin.Log.LogWarning("Game over requested but we're alive; ignoring.");
                return;
            }
            if (gameOverStarted)
                return;
            gameOverStarted = true;
            try
            {
                p.StartCoroutine((IEnumerator)handleDeathMethod.Invoke(p, null));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Couldn't show the death screen: " + e);
                gameOverStarted = false;
            }
        }

        // A new life (new run or revived) can game-over again later.
        public static void ResetGameOver()
        {
            gameOverStarted = false;
        }

        // ---- Coming back from a chest ----

        // The last chest used to revive the partner (it gives no loot, so it isn't "opened" for them).
        public static Chest ReviveChest;

        // Opening a normal chest while the partner is a ghost: it brings them back instead of giving loot,
        // like the game's co-op chests.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Chest), "Open")]
        private static bool BeforeChestOpen(Chest __instance, PlayerController player)
        {
            if (!PartnerIsGhost || player == null || player.IsGhost || player.healthHaver.IsDead)
                return true;
            if (__instance.ChestIdentifier != Chest.SpecialChestIdentifier.NORMAL)
                return true;

            __instance.IsOpen = true;
            ReviveChest = __instance;
            if (__instance.spriteAnimator != null && !string.IsNullOrEmpty(__instance.openAnimName))
                __instance.spriteAnimator.Play(__instance.openAnimName);
            Vector2 at = __instance.sprite != null ? __instance.sprite.WorldBottomCenter : (Vector2)__instance.transform.position;
            Plugin.Log.LogInfo("Opened a chest while our partner is a ghost: bringing them back at " + at);
            SendRevive(at, false);
            return false;
        }

        // Beating a floor boss while the partner is a ghost brings them back, like the game's local co-op does.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomHandler), "HandleBossClearReward")]
        private static void AfterBossClearReward()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (!PartnerIsGhost || me == null || me.IsGhost || me.healthHaver.IsDead)
                return;
            Plugin.Log.LogInfo("Boss beaten while our partner is a ghost: bringing them back.");
            SendRevive(me.CenterPosition, true);
        }

        // Every ghost comes back, or only the one in slot onlySlot.
        private static void SendRevive(Vector2 at, bool fromBoss, int onlySlot = -1)
        {
            byte target = onlySlot < 0 ? Protocol.NoSlot : (byte)onlySlot;
            session.SendReliable(MsgType.Revive, delegate (BinaryWriter w)
            {
                w.Write(at.x);
                w.Write(at.y);
                w.Write(fromBoss);
                w.Write(target);
            });
        }

        // ---- The Cultist's Friendship Cookie (and any other "revives the partner" item) ----
        // In local co-op it can only be used while the partner is dead, and brings them back. Online the game counts
        // as single player, so it never revived anyone: here it can be used while another player on our floor is a
        // ghost, and brings back the closest one.

        private static RemotePlayerState ClosestGhost(PlayerController user)
        {
            if (!Connected || user == null)
                return null;
            string level = LevelSync.CurrentLevelKey();
            RemotePlayerState best = null;
            float bestDist = float.MaxValue;
            foreach (RemotePlayerState r in session.Remotes)
            {
                if (!r.HasPlayer || !r.IsDead || r.Scene != level)
                    continue;
                float d = Vector2.Distance(user.CenterPosition, r.Position);
                if (d < bestDist)
                {
                    best = r;
                    bestDist = d;
                }
            }
            return best;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HealPlayerItem), "CanBeUsed")]
        private static void AfterCookieCanBeUsed(HealPlayerItem __instance, PlayerController __0, ref bool __result)
        {
            if (__instance.DoesRevive && Connected && __result && ClosestGhost(__0) == null)
                __result = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealPlayerItem), "DoEffect")]
        private static void BeforeCookieEffect(HealPlayerItem __instance, PlayerController __0)
        {
            PlayerController user = __0;
            if (!__instance.DoesRevive || !Connected)
                return;
            RemotePlayerState ghost = ClosestGhost(__0);
            if (ghost == null)
                return;
            Plugin.Log.LogInfo("Used " + __instance.name + ": bringing back " + ghost.Name + ".");
            Toast.Show("Bringing back " + ghost.Name + "!", 3f);
            SendRevive(__0.CenterPosition, true, ghost.Slot);
        }

        public static void OnRevive(BinaryReader r)
        {
            Vector2 at = new Vector2(r.ReadSingle(), r.ReadSingle());
            bool fromBoss = r.ReadBoolean();
            int target = r.ReadByte();
            if (!GameManager.HasInstance)
                return;
            if (target != Protocol.NoSlot && target != session.LocalSlot)
                return; // someone else is being brought back
            PlayerController p = GameManager.Instance.PrimaryPlayer;
            if (p == null || !p.IsGhost)
                return;
            Plugin.Log.LogInfo(fromBoss ? session.PeerName + " beat the boss or used a revive: coming back to life." : session.PeerName + " opened a chest: coming back to life.");
            if (!fromBoss)
                OpenChestNear(at);
            p.ResurrectFromChest(at);
            p.StartCoroutine(FinishRevive(p));
        }

        // The game's revive (CoopResurrectInternal) ends by recalculating the OTHER player's stats; online there
        // is no other player in our game, so it crashed right there and skipped its last steps: giving control
        // back and making the player hittable again (the revived player was stuck frozen). Do those steps once
        // the revive animation has had time to finish.
        private static IEnumerator FinishRevive(PlayerController p)
        {
            float waited = 0f;
            while (waited < 4f && p != null && (p.IsCurrentlyCoopReviving || p.spriteAnimator.IsPlaying("ghost_revive") || waited < 1.5f))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (p == null || p.IsGhost)
                yield break;
            p.CurrentInputState = PlayerInputState.AllInput;
            p.healthHaver.IsVulnerable = true;
            p.ClearInputOverride("revivepause");
            p.IsCurrentlyCoopReviving = false;
            Plugin.Log.LogInfo("Revive finished: control restored.");
        }

        // "The best living player" is nobody in a game whose only player is a ghost (local co-op always has someone
        // alive while the run goes on). The boss's loot drop asks for it and crashed there: that game got no boss
        // chest. Answer with our own player instead.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "BestActivePlayer", MethodType.Getter)]
        private static void AfterBestActivePlayer(GameManager __instance, ref PlayerController __result)
        {
            if (__result == null && Connected)
                __result = __instance.PrimaryPlayer;
        }

        // A ghost that drifts too far is "reunited" with the other player; online there is none in our game
        // (it crashed). Teleport to the partner instead.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerController), "ReuniteWithOtherPlayer")]
        private static bool BeforeReunite(PlayerController other)
        {
            if (other != null)
                return true;
            if (Connected)
                PlayerWarp.TeleportToPartner();
            return false;
        }

        // Our copy of the chest the partner used: show it opened (and empty) here too.
        private static void OpenChestNear(Vector2 at)
        {
            Chest best = null;
            float bestDist = 3f;
            foreach (Chest c in Object.FindObjectsOfType(typeof(Chest)))
            {
                if (c == null || c.IsOpen || c.sprite == null)
                    continue;
                float d = Vector2.Distance(c.sprite.WorldBottomCenter, at);
                if (d < bestDist)
                {
                    best = c;
                    bestDist = d;
                }
            }
            if (best == null)
                return;
            best.IsOpen = true;
            if (best.spriteAnimator != null && !string.IsNullOrEmpty(best.openAnimName))
                best.spriteAnimator.Play(best.openAnimName);
        }
    }
}
