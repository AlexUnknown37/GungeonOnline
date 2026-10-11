using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Co-op deaths, the way the game's own local co-op does them:
    // - If you die while your partner is still alive, you become a ghost instead of getting a game over.
    // - While one player is a ghost, the next normal chest the living player opens brings them back
    //   (no loot from that chest), right at the chest.
    // - If the second player dies too, both games show the death screen.
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

        // Is the partner alive, in a run, on our floor? Only then can they come and get us.
        private static bool PartnerAliveHere
        {
            get
            {
                RemotePlayerState r = session.Remote;
                return r.HasPlayer && !r.IsDead && r.Scene == LevelSync.CurrentLevelKey();
            }
        }

        public static bool PartnerIsGhost
        {
            get { return Connected && session.Remote.HasPlayer && session.Remote.IsDead; }
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

            // The partner is already a ghost (or gone): this is a normal game over, for both of us.
            if (session.Remote.HasPlayer && session.Remote.IsDead)
            {
                Plugin.Log.LogInfo("We died while our partner is a ghost: game over for both.");
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
            bool partnerDown = Connected ? (session.Remote.HasPlayer && session.Remote.IsDead) : true;
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
                Plugin.Log.LogInfo(Connected ? "Both players are down: game over." : "We're a ghost and our partner is gone: game over.");
                ForceGameOver();
            }
        }

        public static void OnTeamWiped()
        {
            Plugin.Log.LogInfo("Partner died too: game over.");
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
            if (__instance.spriteAnimator != null && !string.IsNullOrEmpty(__instance.openAnimName))
                __instance.spriteAnimator.Play(__instance.openAnimName);
            Vector2 at = __instance.sprite != null ? __instance.sprite.WorldBottomCenter : (Vector2)__instance.transform.position;
            Plugin.Log.LogInfo("Opened a chest while our partner is a ghost: bringing them back at " + at);
            session.SendReliable(MsgType.Revive, delegate (BinaryWriter w)
            {
                w.Write(at.x);
                w.Write(at.y);
            });
            return false;
        }

        public static void OnRevive(BinaryReader r)
        {
            Vector2 at = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (!GameManager.HasInstance)
                return;
            PlayerController p = GameManager.Instance.PrimaryPlayer;
            if (p == null || !p.IsGhost)
                return;
            Plugin.Log.LogInfo("Partner opened a chest: coming back to life.");
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
