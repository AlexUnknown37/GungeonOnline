using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // Draws another player's companion (e.g. the Hunter's dog): a sprite-only copy showing exactly the
    // animation frame their game reports, gliding smoothly. Only a picture: the real companion fights in its
    // owner's game (its bullets are copied by BulletSync); petting goes through PetSync.
    public class PetPuppet
    {
        private tk2dBaseSprite sprite;
        private readonly List<tk2dSpriteAnimation> libraries = new List<tk2dSpriteAnimation>();
        private string builtGuid = "";
        private bool shownFrame;
        private Material material;
        private Texture texture;
        private readonly HashSet<string> missingClips = new HashSet<string>();

        // For petting: is the partner's pet drawn right now, and where is its middle?
        public bool IsShown
        {
            get { return sprite != null && sprite.gameObject.activeInHierarchy && shownFrame; }
        }

        public Vector2 Center
        {
            get { return sprite != null ? sprite.WorldCenter : Vector2.zero; }
        }

        public void Update(RemotePlayerState remote, bool partnerShown)
        {
            Vector2 feet = remote.PetMotion.HasData ? remote.PetMotion.Sample() : remote.PetFeet;
            Show(partnerShown && remote.HasPet, remote.PetGuid, remote.PetClip, remote.PetFrame, remote.PetFlipX, feet);
        }

        // Draw any companion: which one (enemy guid), its animation frame, facing and feet position.
        public void Show(bool visible, string guid, string clip, int frame, bool flipX, Vector2 feet)
        {
            if (!visible || string.IsNullOrEmpty(guid) || !GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null)
            {
                Hide();
                return;
            }

            // Scene loads destroy the copy (sprite == null), so it gets rebuilt as needed.
            if (sprite == null || guid != builtGuid)
            {
                if (!Build(guid))
                    return;
            }

            if (!sprite.gameObject.activeSelf)
                sprite.gameObject.SetActive(true);
            ShowFrame(clip, frame);
            sprite.FlipX = flipX;
            RemotePuppet.ApplySheet(sprite, ref material, ref texture);

            Vector2 offset = feet - sprite.WorldBottomCenter;
            sprite.transform.position += new Vector3(offset.x, offset.y, 0f);
            sprite.UpdateZDepth();
        }

        public void Hide()
        {
            if (sprite != null && sprite.gameObject.activeSelf)
                sprite.gameObject.SetActive(false);
        }

        private void ShowFrame(string clipName, int frameIndex)
        {
            tk2dSpriteAnimationClip clip = null;
            foreach (tk2dSpriteAnimation lib in libraries)
            {
                clip = lib.GetClipByName(clipName);
                if (clip != null && clip.frames != null && clip.frames.Length > 0)
                    break;
                clip = null;
            }
            if (clip == null)
            {
                if (clipName.Length > 0 && missingClips.Add(clipName))
                    Plugin.Log.LogWarning("Partner pet animation '" + clipName + "' not found.");
                return;
            }
            tk2dSpriteAnimationFrame frame = clip.frames[Mathf.Clamp(frameIndex, 0, clip.frames.Length - 1)];
            sprite.SetSprite(frame.spriteCollection, frame.spriteId);
            if (!shownFrame)
            {
                shownFrame = true;
                if (sprite.renderer != null)
                    sprite.renderer.enabled = true;
            }
        }

        private bool Build(string guid)
        {
            Destroy();
            builtGuid = guid;
            missingClips.Clear();
            libraries.Clear();

            AIActor prefab = EnemyDatabase.GetOrLoadByGuid(guid);
            if (prefab == null)
            {
                Plugin.Log.LogWarning("Couldn't find partner's pet (enemy guid " + guid + ").");
                return false;
            }
            foreach (tk2dSpriteAnimator animator in prefab.GetComponentsInChildren<tk2dSpriteAnimator>(true))
            {
                if (animator.Library != null && !libraries.Contains(animator.Library))
                    libraries.Add(animator.Library);
            }
            PlayerController local = GameManager.Instance.PrimaryPlayer;
            if (libraries.Count == 0 || local == null || local.sprite == null)
                return false;

            sprite = RemotePuppet.CloneStrippedSprite(local.sprite, "GungeonOnline Partner Pet");
            if (sprite == null)
                return false;
            shownFrame = false;
            Plugin.Log.LogInfo("Showing partner's pet " + prefab.name);
            return true;
        }

        private void Destroy()
        {
            if (sprite != null)
                Object.Destroy(sprite.gameObject);
            sprite = null;
            if (material != null)
                Object.Destroy(material);
            material = null;
            texture = null;
        }
    }
}
