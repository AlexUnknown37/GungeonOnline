using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // Petting the partner's dog.
    //
    // The game's petting (PlayerController.HandlePlayerInput) only looks at real companions in your own
    // game; the partner's dog is just a picture there (PetPuppet). So we do the same thing ourselves:
    // - In the petter's game: interact within 2.5 tiles of the partner's dog (not in combat, not rolling)
    //   plays our player's "pet" animation and hides the gun/hands, exactly like normal petting. Moving away
    //   (more than 3 tiles) or rolling stops it, like the game does.
    // - In the owner's game: PetStart makes their real dog play its "pet" animation facing us; PetStop ends it.
    //   That dog animation reaches the petter's screen through the normal pet sync.
    public static class PetSync
    {
        private const float StartRange = 2.5f;
        private const float StopRange = 3f;

        private static NetSession session;
        private static int petOwner = -1;   // the slot whose dog we're petting
        private static bool petting;
        private static float pettingSince;
        private static readonly FieldInfo queuedAnimationField = AccessTools.Field(typeof(PlayerController), "m_handlingQueuedAnimation");

        private static readonly MethodInfo toggleGuns = AccessTools.Method(typeof(PlayerController), "ToggleGunRenderers");
        private static readonly MethodInfo toggleHands = AccessTools.Method(typeof(PlayerController), "ToggleHandRenderers");
        private static readonly FieldInfo activeActionsField = AccessTools.Field(typeof(PlayerController), "m_activeActions");

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        public static void Update()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || session == null || session.Status != NetSession.State.Connected)
            {
                if (petting && me != null)
                    StopPetting(me);
                petting = false;
                return;
            }

            if (petting)
            {
                PetPuppet pet = petOwner >= 0 ? Plugin.Pets[petOwner] : null;
                bool moving = Time.realtimeSinceStartup - pettingSince > 0.25f && me.specRigidbody != null
                    && me.specRigidbody.Velocity.sqrMagnitude > 0.01f;
                bool stillPetting = pet != null && pet.IsShown && Vector2.Distance(me.CenterPosition, pet.Center) <= StopRange
                    && !me.IsDodgeRolling && !moving && !me.IsInCombat;
                if (!stillPetting)
                {
                    StopPetting(me);
                    return;
                }
                // Keep the pet animation going (it may not loop on its own).
                if (me.spriteAnimator != null && !me.spriteAnimator.IsPlaying("pet"))
                    me.spriteAnimator.Play("pet");
                return;
            }

            if (me.IsGhost || me.IsInCombat || me.IsDodgeRolling || me.IsPetting)
                return;
            // The closest dog in reach.
            int closest = -1;
            float closestDist = StartRange;
            for (int i = 0; i < Plugin.Pets.Length; i++)
            {
                PetPuppet candidate = Plugin.Pets[i];
                if (i == session.LocalSlot || !candidate.IsShown)
                    continue;
                float d = Vector2.Distance(me.CenterPosition, candidate.Center);
                if (d <= closestDist)
                {
                    closest = i;
                    closestDist = d;
                }
            }
            if (closest < 0 || !InteractPressed(me))
                return;
            petOwner = closest;
            StartPetting(me);
        }

        private static bool InteractPressed(PlayerController me)
        {
            GungeonActions actions = activeActionsField.GetValue(me) as GungeonActions;
            return actions != null && actions.InteractAction.WasPressed;
        }

        private static void StartPetting(PlayerController me)
        {
            petting = true;
            pettingSince = Time.realtimeSinceStartup;
            // Without a real companion the game doesn't know we're petting, and its animation code swapped
            // "pet" back to idle the next frame. This flag (used for one-off animations like the ghost sneeze)
            // makes it leave our animation alone until we clear it.
            queuedAnimationField.SetValue(me, true);
            me.spriteAnimator.Play("pet");
            SetGunAndHands(me, false);
            bool petterOnLeft = me.CenterPosition.x < Plugin.Pets[petOwner].Center.x;
            byte owner = (byte)petOwner;
            Plugin.Log.LogInfo("Petting " + session.Players[petOwner].Name + "'s dog.");
            session.SendReliable(MsgType.PetStart, delegate (BinaryWriter w) { w.Write(owner); w.Write(petterOnLeft); });
        }

        private static void StopPetting(PlayerController me)
        {
            petting = false;
            queuedAnimationField.SetValue(me, false);
            SetGunAndHands(me, true);
            if (petOwner < 0)
                return;
            byte owner = (byte)petOwner;
            petOwner = -1;
            session.SendReliable(MsgType.PetStop, delegate (BinaryWriter w) { w.Write(owner); });
        }

        private static void SetGunAndHands(PlayerController me, bool visible)
        {
            if (toggleGuns != null)
                toggleGuns.Invoke(me, new object[] { visible, "petting" });
            if (toggleHands != null)
                toggleHands.Invoke(me, new object[] { visible, "petting" });
        }

        // ---- The dog owner's game ----

        private static AIActor OurPettableDog()
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me == null || me.companions == null)
                return null;
            foreach (AIActor c in me.companions)
            {
                CompanionController cc = c != null ? c.GetComponent<CompanionController>() : null;
                if (cc != null && cc.CanBePet && c.aiAnimator != null)
                    return c;
            }
            return null;
        }

        public static void OnPetStart(BinaryReader r)
        {
            int owner = r.ReadByte();
            bool petterOnLeft = r.ReadBoolean();
            if (owner != session.LocalSlot)
                return; // someone else's dog
            AIActor dog = OurPettableDog();
            if (dog == null)
                return;
            Plugin.Log.LogInfo(session.PeerName + " is petting our dog.");
            dog.aiAnimator.LockFacingDirection = true;
            dog.aiAnimator.FacingDirection = petterOnLeft ? 180f : 0f;
            dog.aiAnimator.PlayUntilCancelled("pet", false, null, -1f, false);
        }

        public static void OnPetStop(BinaryReader r)
        {
            if (r.ReadByte() != session.LocalSlot)
                return;
            AIActor dog = OurPettableDog();
            if (dog == null)
                return;
            dog.aiAnimator.EndAnimationIf("pet");
            dog.aiAnimator.LockFacingDirection = false;
        }
    }
}
