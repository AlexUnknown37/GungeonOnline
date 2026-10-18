using UnityEngine;

namespace GungeonOnline
{
    // Draws the partner's character: a sprite-only copy with no game logic, showing exactly the
    // animation frame the partner's game reports. Enemies and bullets ignore it (that comes later).
    //
    // A sprite built from scratch stayed invisible even with the same layer, shader and depth as a real
    // player: the game sets more on a live player's sprite (UV2 generation, tilt/perpendicular state,
    // its player material). So the puppet is a clone of our OWN player's sprite object, stripped of
    // logic, with the partner's sprite sheet and frames swapped in.
    public class RemotePuppet
    {
        private GameObject go;
        private tk2dBaseSprite sprite;
        // Every animation library on the partner's character (body, hands, effects...), searched in order.
        private readonly System.Collections.Generic.List<tk2dSpriteAnimation> libraries = new System.Collections.Generic.List<tk2dSpriteAnimation>();
        private readonly System.Collections.Generic.HashSet<string> missingClips = new System.Collections.Generic.HashSet<string>();
        private bool hasShownPartnerFrame;
        private int builtIdentity = -1;
        private bool builtAltCostume;
        private string builtCustom = "";
        private bool failedToBuild;
        private Material puppetMaterial;
        private Texture puppetTexture;

        // The gun in the partner's hand: a second clone, showing the gun's sprite.
        private readonly PuppetFlames flames = new PuppetFlames();
        private GameObject shadowGo;
        private Renderer shadowRenderer;
        private GameObject gunGo;
        private tk2dBaseSprite gunSprite;
        private Material gunMaterial;
        private Texture gunTexture;
        private readonly System.Collections.Generic.Dictionary<int, tk2dSpriteCollectionData> gunCollections =
            new System.Collections.Generic.Dictionary<int, tk2dSpriteCollectionData>();

        private static readonly System.Reflection.FieldInfo attachedRenderersField =
            HarmonyLib.AccessTools.Field(typeof(tk2dBaseSprite), "attachedRenderers");

        // For the window: what the puppet is doing right now.
        public string Status = "hidden";

        // Where to put the partner's name: just above the head of the character as it's drawn right now (the
        // smoothed position), not the newest network position, which runs a little ahead.
        public bool TryGetHeadTop(out Vector3 world)
        {
            world = Vector3.zero;
            if (go == null || !go.activeInHierarchy || sprite == null || sprite.renderer == null || !sprite.renderer.enabled)
                return false;
            Bounds b = sprite.renderer.bounds;
            world = new Vector3(b.center.x, b.max.y, 0f);
            return true;
        }

        public void Update(RemotePlayerState remote, bool connected)
        {
            string hiddenReason = null;
            if (!connected)
                hiddenReason = "not connected";
            else if (!remote.HasPlayer)
                hiddenReason = "partner has no character (menus or loading)";
            else if (!remote.Visible)
                hiddenReason = "partner's character is invisible in their game";
            else if (!GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null)
                hiddenReason = "you have no character yet";
            else if (remote.Scene != LevelSync.CurrentLevelKey())
                hiddenReason = "partner is on another floor (" + remote.Scene + ")";
            if (hiddenReason != null)
            {
                Hide();
                Status = "hidden: " + hiddenReason;
                return;
            }

            // Scene loads destroy the puppet (go == null), so it gets rebuilt as needed.
            if (go == null || remote.Identity != builtIdentity || remote.AltCostume != builtAltCostume || remote.CustomCharacter != builtCustom)
            {
                if (!Build(remote))
                {
                    Status = "couldn't load character";
                    return;
                }
            }

            if (!go.activeSelf)
                go.SetActive(true);
            ShowFrame(remote.Clip, remote.Frame);
            sprite.FlipX = remote.FlipX;
            ApplySheet(sprite, ref puppetMaterial, ref puppetTexture);
            // Ghost glow, status tints (poison, freeze, ...) and fire, as the partner's game shows them.
            if (sprite.renderer != null && sprite.renderer.sharedMaterial == puppetMaterial)
                remote.Look.ApplyTo(puppetMaterial);
            flames.Update(remote.Look.OnFire && hasShownPartnerFrame, sprite);

            // Smoothed position (see MotionBuffer): glides between updates instead of jumping.
            Vector2 feet = remote.Motion.HasData ? remote.Motion.Sample() : remote.FeetPosition;

            // Move the puppet so its feet land on the partner's feet. Lining up by the feet (instead of
            // copying the transform) keeps it in place when the sprite is mirrored to face left.
            Vector2 offset = feet - sprite.WorldBottomCenter;
            go.transform.position += new Vector3(offset.x, offset.y, 0f);

            // Place the gun first: the body's depth update also sorts the gun attached to it.
            UpdateGun(remote, feet);
            sprite.UpdateZDepth();
            UpdateShadow(remote, feet, go.transform.position.z);

            Status = "shown"; // details only when asked (Details), not rebuilt every frame
        }

        public void Hide()
        {
            if (go != null && go.activeSelf)
                go.SetActive(false);
            if (gunGo != null && gunGo.activeSelf)
                gunGo.SetActive(false);
            if (shadowGo != null && shadowGo.activeSelf)
                shadowGo.SetActive(false);
            flames.Clear();
            Status = "hidden";
        }

        // The blob shadow under the partner's feet: a stripped copy of our own player's shadow.
        private void UpdateShadow(RemotePlayerState remote, Vector2 feet, float bodyZ)
        {
            PlayerController local = GameManager.Instance.PrimaryPlayer;
            if (!remote.HasShadow || !hasShownPartnerFrame || local.ShadowObject == null)
            {
                if (shadowGo != null && shadowGo.activeSelf)
                    shadowGo.SetActive(false);
                return;
            }
            if (shadowGo == null)
            {
                shadowGo = (GameObject)Object.Instantiate(local.ShadowObject);
                shadowGo.name = "GungeonOnline Partner Shadow";
                shadowGo.transform.parent = null;
                shadowGo.transform.localScale = local.ShadowObject.transform.lossyScale;
                // Keep only what draws it: no collision bodies or game scripts.
                foreach (MonoBehaviour behaviour in shadowGo.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!(behaviour is tk2dBaseSprite))
                        Object.Destroy(behaviour);
                    else
                        ((tk2dBaseSprite)behaviour).attachParent = null; // don't follow OUR player's depth
                }
                shadowRenderer = shadowGo.GetComponentInChildren<Renderer>();
            }
            if (!shadowGo.activeSelf)
                shadowGo.SetActive(true);
            if (shadowRenderer == null)
                return;
            // The copy can come out with drawing switched off (F9 showed "enabled False"); keep it on.
            if (!shadowRenderer.enabled)
                shadowRenderer.enabled = true;

            // Move the shadow so the middle of what's drawn lands where the partner's game draws theirs.
            Vector2 target = feet + (Vector2)remote.ShadowOffset;
            Vector2 drawn = shadowRenderer.bounds.center;
            Vector3 pos = shadowGo.transform.position + (Vector3)(target - drawn);
            float z = bodyZ + remote.ShadowOffset.z;
            pos.z += z - shadowRenderer.transform.position.z;
            shadowGo.transform.position = pos;
        }

        // For the F8 window: what the partner's sprite and ours look like right now.
        public string Details()
        {
            if (Status != "shown" || sprite == null || !GameManager.HasInstance || GameManager.Instance.PrimaryPlayer == null)
                return Status;
            return "shown: " + Describe(sprite) + "\nYou: " + Describe(GameManager.Instance.PrimaryPlayer.sprite);
        }

        private static string Describe(tk2dBaseSprite s)
        {
            if (s == null)
                return "none";
            Renderer r = s.renderer;
            return "layer " + LayerMask.LayerToName(s.gameObject.layer) + ", z " + s.transform.position.z.ToString("F1")
                + (r == null ? ", no renderer" : (r.isVisible ? ", on screen" : ", NOT on screen")
                    + ", " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "no material"));
        }

        // A still picture of the character for the party list: the first frame of its standing animation.
        public tk2dSpriteDefinition Icon()
        {
            foreach (string name in new string[] { "idle_front", "idle", "idle_forward" })
            {
                foreach (tk2dSpriteAnimation lib in libraries)
                {
                    tk2dSpriteAnimationClip clip = lib != null ? lib.GetClipByName(name) : null;
                    if (clip != null && clip.frames != null && clip.frames.Length > 0 && clip.frames[0].spriteCollection != null)
                        return clip.frames[0].spriteCollection.spriteDefinitions[clip.frames[0].spriteId];
                }
            }
            return sprite != null && hasShownPartnerFrame ? sprite.GetCurrentSpriteDef() : null;
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
                // Unknown animation: keep showing the last frame, and say so once.
                if (clipName.Length > 0 && missingClips.Add(clipName))
                    Plugin.Log.LogWarning("Partner animation '" + clipName + "' not found in " + libraries.Count + " libraries.");
                return;
            }

            tk2dSpriteAnimationFrame frame = clip.frames[Mathf.Clamp(frameIndex, 0, clip.frames.Length - 1)];
            sprite.SetSprite(frame.spriteCollection, frame.spriteId);
            if (!hasShownPartnerFrame)
            {
                // Until now the clone still looked like OUR character; only show it once it's the partner.
                hasShownPartnerFrame = true;
                if (sprite.renderer != null)
                    sprite.renderer.enabled = true;
                // The black outline every player has (ours was stripped from the copy). Added now, from the
                // partner's frame, so it outlines their character and follows its animation from here on.
                if (!SpriteOutlineManager.HasOutline(sprite))
                    SpriteOutlineManager.AddOutlineToSprite(sprite, Color.black);
            }
        }

        // The clones use a player material (usesOverrideMaterial), so changing sprite collection doesn't
        // change their texture by itself. Point a private copy of the material at the current frame's sheet.
        public static void ApplySheet(tk2dBaseSprite s, ref Material material, ref Texture texture)
        {
            Renderer r = s.renderer;
            tk2dSpriteDefinition def = s.CurrentSprite;
            Material defMaterial = def != null ? (def.materialInst != null ? def.materialInst : def.material) : null;
            if (r == null || r.sharedMaterial == null || defMaterial == null)
                return;
            Texture sheet = defMaterial.mainTexture;
            if (material != null && texture == sheet)
                return;

            // One material per sheet, made once and reused. (Making a new one on every sheet change and
            // destroying the old one happened many times a second while walking, when the animation's frames
            // span several sheets, and the garbage made the game stutter.)
            SheetMaterials cache = s.GetComponent<SheetMaterials>();
            if (cache == null)
                cache = s.gameObject.AddComponent<SheetMaterials>();
            Material forSheet;
            if (!cache.BySheet.TryGetValue(sheet, out forSheet) || forSheet == null)
            {
                forSheet = new Material(material != null ? material : r.sharedMaterial);
                forSheet.mainTexture = sheet;
                cache.BySheet[sheet] = forSheet;
            }
            material = forSheet;
            texture = sheet;
            s.usesOverrideMaterial = true;
            r.sharedMaterial = forSheet;
            // The outline draws from the same sheet; point it at the new one too.
            tk2dSprite[] outlines = SpriteOutlineManager.GetOutlineSprites(s);
            if (outlines != null)
            {
                foreach (tk2dSprite o in outlines)
                {
                    if (o != null)
                        SpriteOutlineManager.ForceUpdateOutlineMaterial(o, s);
                }
            }
        }

        // Shows the partner's gun at the position and angle their game reports, relative to the puppet's feet.
        // Like a real player, the gun is attached to the body sprite; its HeightOffGround (+/-0.075 in the
        // game) decides whether the body's depth update puts it in front of or behind the body.
        private void UpdateGun(RemotePlayerState remote, Vector2 feet)
        {
            tk2dSpriteCollectionData collection = remote.HasGun ? GunCollection(remote.GunId) : null;
            if (collection == null || !hasShownPartnerFrame)
            {
                if (gunGo != null && gunGo.activeSelf)
                    gunGo.SetActive(false);
                return;
            }

            if (gunGo == null)
            {
                // Clone the (already stripped) body puppet so the gun gets the same working render setup.
                gunGo = (GameObject)Object.Instantiate(go);
                gunGo.name = "GungeonOnline Partner Gun";
                for (int i = gunGo.transform.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(gunGo.transform.GetChild(i).gameObject);
                gunSprite = gunGo.GetComponent<tk2dBaseSprite>();
                gunSprite.FlipX = false;
                if (gunSprite.renderer != null && gunSprite.renderer.sharedMaterial != null)
                    gunSprite.renderer.sharedMaterial = new Material(gunSprite.renderer.sharedMaterial);
                System.Collections.IList attached = attachedRenderersField.GetValue(gunSprite) as System.Collections.IList;
                if (attached != null)
                    attached.Clear();
                sprite.AttachRenderer(gunSprite);
                gunMaterial = null;
                gunTexture = null;
                if (gunSprite.renderer != null)
                    gunSprite.renderer.enabled = true;
            }
            if (!gunGo.activeSelf)
                gunGo.SetActive(true);

            gunSprite.SetSprite(collection, remote.GunSpriteId);
            gunSprite.FlipY = remote.GunFlipY;
            gunSprite.HeightOffGround = remote.GunHeight;
            ApplySheet(gunSprite, ref gunMaterial, ref gunTexture);

            Vector2 p = feet + remote.GunOffset;
            gunGo.transform.rotation = Quaternion.Euler(0f, 0f, remote.GunAngle);
            gunGo.transform.position = new Vector3(p.x, p.y, gunGo.transform.position.z);
        }

        // Each gun's sprites live in its own collection; find it from the gun's item id.
        private tk2dSpriteCollectionData GunCollection(int gunId)
        {
            tk2dSpriteCollectionData collection;
            if (gunCollections.TryGetValue(gunId, out collection))
                return collection;

            Gun gun = PickupObjectDatabase.GetById(gunId) as Gun;
            tk2dBaseSprite gunPrefabSprite = gun != null ? gun.GetComponent<tk2dBaseSprite>() : null;
            collection = gunPrefabSprite != null ? gunPrefabSprite.Collection : null;
            if (collection == null)
                Plugin.Log.LogWarning("Couldn't find sprites for partner's gun (item id " + gunId + ").");
            gunCollections[gunId] = collection;
            return collection;
        }

        private bool Build(RemotePlayerState remote)
        {
            Destroy();
            builtIdentity = remote.Identity;
            builtAltCostume = remote.AltCostume;
            builtCustom = remote.CustomCharacter;

            // The partner's animations come from their character's prefab (a Custom Characters character's own
            // prefab when we have that mod too)...
            string path;
            GameObject prefab = CustomCharacterSupport.PartnerPrefab(remote, out path);
            PlayerController template = prefab != null ? prefab.GetComponent<PlayerController>() : null;
            // ...and the drawing setup comes from our own live player.
            PlayerController local = GameManager.Instance.PrimaryPlayer;
            if (template == null || local == null || local.sprite == null)
            {
                if (!failedToBuild)
                    Plugin.Log.LogError("Couldn't load partner character '" + path + "'.");
                failedToBuild = true;
                return false;
            }
            failedToBuild = false;

            // A character has several animated parts, so collect every library; the alt costume goes first.
            libraries.Clear();
            missingClips.Clear();
            if (remote.AltCostume && template.AlternateCostumeLibrary != null)
                libraries.Add(template.AlternateCostumeLibrary);
            foreach (tk2dSpriteAnimator animator in prefab.GetComponentsInChildren<tk2dSpriteAnimator>(true))
            {
                if (animator.Library != null && !libraries.Contains(animator.Library))
                    libraries.Add(animator.Library);
            }
            if (libraries.Count == 0)
            {
                Plugin.Log.LogError("Partner character '" + path + "' has no animations.");
                return false;
            }

            GameObject original = local.sprite.gameObject;
            sprite = CloneStrippedSprite(local.sprite, "GungeonOnline Partner");
            if (sprite == null)
            {
                Plugin.Log.LogError("Our own player's sprite object has no tk2d sprite; can't build the partner.");
                Destroy();
                return false;
            }
            go = sprite.gameObject;
            // Hidden until the first partner frame is applied (see ShowFrame).
            hasShownPartnerFrame = false;

            string names = "";
            foreach (tk2dSpriteAnimation lib in libraries)
                names += (names.Length > 0 ? ", " : "") + lib.name + " (" + (lib.clips != null ? lib.clips.Length : 0) + " clips)";
            Plugin.Log.LogInfo("Showing partner as " + path + (builtAltCostume ? " (alt costume)" : "")
                + " (cloned from " + original.name + "), animation libraries: " + names);
            return true;
        }

        // A sprite built from scratch never showed up; a copy of our OWN live player sprite does (it carries
        // all the render setup the game gives players). This makes that copy and strips everything else off:
        // children (outline, hands), scripts, links to our player. Returned hidden; the caller shows it once
        // it has set the right frame. Used for the partner's body and their pet.
        public static tk2dBaseSprite CloneStrippedSprite(tk2dBaseSprite source, string name)
        {
            GameObject original = source.gameObject;
            GameObject copy = (GameObject)Object.Instantiate(original, original.transform.position, original.transform.rotation);
            copy.name = name;
            copy.transform.parent = null;
            copy.transform.localScale = original.transform.lossyScale;

            // Immediately: a normal Destroy waits for the end of the frame, and a clone made in the same frame
            // would copy our player's outline child (that was the black silhouette of OUR character).
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(copy.transform.GetChild(i).gameObject);
            foreach (MonoBehaviour behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (!(behaviour is tk2dBaseSprite))
                    Object.Destroy(behaviour);
            }
            // Added after the clean-up above; clones of this object inherit it too.
            copy.AddComponent<NoReflection>();

            tk2dBaseSprite s = copy.GetComponent<tk2dBaseSprite>();
            if (s == null)
            {
                Object.Destroy(copy);
                return null;
            }
            System.Collections.IList attached = attachedRenderersField.GetValue(s) as System.Collections.IList;
            if (attached != null)
                attached.Clear();
            s.attachParent = null;
            s.color = Color.white;
            if (s.renderer != null)
            {
                s.renderer.enabled = false;
                // Our own material right away: the copy starts out SHARING our player's material, so when we got
                // hit the game's damage flash (and tints) showed on the copy too (the partner's dog flashed).
                if (s.renderer.sharedMaterial != null)
                {
                    s.renderer.sharedMaterial = new Material(s.renderer.sharedMaterial);
                    s.usesOverrideMaterial = true;
                }
            }
            return s;
        }

        private void Destroy()
        {
            if (go != null)
                Object.Destroy(go);
            go = null;
            sprite = null;
            if (puppetMaterial != null)
                Object.Destroy(puppetMaterial);
            puppetMaterial = null;
            puppetTexture = null;

            if (shadowGo != null)
                Object.Destroy(shadowGo);
            shadowGo = null;
            if (gunGo != null)
                Object.Destroy(gunGo);
            gunGo = null;
            gunSprite = null;
            if (gunMaterial != null)
                Object.Destroy(gunMaterial);
            gunMaterial = null;
            gunTexture = null;
        }

        // F9: write everything about the puppet and our own sprite to the log, to compare them.
        public void DebugDump()
        {
            Plugin.Log.LogInfo("=== Puppet debug (" + Status.Replace("\n", " | ") + ")");
            Plugin.Log.LogInfo("Partner: " + DescribeFully(sprite));
            PlayerController local = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            Plugin.Log.LogInfo("You:     " + DescribeFully(local != null ? local.sprite : null));
            Plugin.Log.LogInfo("Partner shadow: " + DescribeObject(shadowGo) + " | partner feet "
                + (sprite != null ? sprite.WorldBottomCenter.ToString("F2") : "?"));
            Plugin.Log.LogInfo("Your shadow:    " + DescribeObject(local != null ? local.ShadowObject : null) + " | your feet "
                + (local != null && local.sprite != null ? local.sprite.WorldBottomCenter.ToString("F2") : "?"));

            // Everything drawn within 3 units of the partner, to identify stray sprites (e.g. the black silhouette).
            if (sprite == null)
                return;
            Vector2 center = sprite.WorldBottomCenter;
            Bounds area = new Bounds(new Vector3(center.x, center.y + 1f, 0f), new Vector3(6f, 6f, 100000f));
            int count = 0;
            foreach (Renderer r in Object.FindObjectsOfType(typeof(Renderer)))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                Bounds b = r.bounds;
                if (b.size.x > 20f || b.size.y > 20f)
                    continue; // floor/wall chunks
                Bounds flat = new Bounds(new Vector3(b.center.x, b.center.y, 0f), new Vector3(b.size.x, b.size.y, 1f));
                if (!area.Intersects(flat))
                    continue;
                count++;
                tk2dBaseSprite s = r.GetComponent<tk2dBaseSprite>();
                string materials = "";
                foreach (Material m in r.sharedMaterials)
                    materials += (materials.Length > 0 ? ", " : "") + (m == null ? "null" : m.shader.name + "/" + (m.mainTexture != null ? m.mainTexture.name : "notex"));
                Plugin.Log.LogInfo("Near partner: '" + Path(r.transform) + "' layer " + LayerMask.LayerToName(r.gameObject.layer)
                    + " " + r.GetType().Name + " bounds " + b.center.ToString("F2") + " size " + b.size.ToString("F2")
                    + (s != null ? " sprite " + (s.CurrentSprite != null ? s.CurrentSprite.name : "?") + " coll " + (s.Collection != null ? s.Collection.name : "?") : "")
                    + " materials [" + materials + "]");
            }
            Plugin.Log.LogInfo("Near partner: " + count + " renderers listed.");
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private static string DescribeObject(GameObject o)
        {
            if (o == null)
                return "none";
            string text = "'" + o.name + "' active " + o.activeInHierarchy + " pos " + o.transform.position.ToString("F2")
                + " scale " + o.transform.lossyScale.ToString("F2") + " parent " + (o.transform.parent != null ? o.transform.parent.name : "none");
            foreach (Renderer r in o.GetComponentsInChildren<Renderer>(true))
                text += " | renderer on '" + r.gameObject.name + "' enabled " + r.enabled + " pos " + r.transform.position.ToString("F2")
                    + " bounds center " + r.bounds.center.ToString("F2") + " size " + r.bounds.size.ToString("F2");
            return text;
        }

        private static string DescribeFully(tk2dBaseSprite s)
        {
            if (s == null)
                return "none";
            Renderer r = s.renderer;
            MeshFilter mf = s.GetComponent<MeshFilter>();
            tk2dSprite ts = s as tk2dSprite;
            tk2dSpriteDefinition def = s.CurrentSprite;
            return "obj '" + s.gameObject.name + "' active " + s.gameObject.activeInHierarchy
                + " pos " + s.transform.position.ToString("F2") + " scale " + s.transform.lossyScale.ToString("F2")
                + " rot " + s.transform.eulerAngles.ToString("F0")
                + " layer " + LayerMask.LayerToName(s.gameObject.layer)
                + " | sprite " + (def != null ? def.name : "?") + " id " + s.spriteId
                + " coll " + (s.Collection != null ? s.Collection.name : "?")
                + " color " + s.color + " flip " + s.FlipX
                + " perp " + s.IsPerpendicular + " height " + s.HeightOffGround
                + " overrideMat " + s.usesOverrideMaterial
                + (ts != null ? " uv2 " + ts.GenerateUV2 : "")
                + " | renderer " + (r == null ? "none" : "enabled " + r.enabled + " visible " + r.isVisible
                    + " bounds " + r.bounds.center.ToString("F2") + " size " + r.bounds.size.ToString("F2")
                    + " sortLayer " + r.sortingLayerName + "/" + r.sortingOrder
                    + " shader " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none")
                    + " tex " + (r.sharedMaterial != null && r.sharedMaterial.mainTexture != null ? r.sharedMaterial.mainTexture.name : "none"))
                + " | mesh verts " + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount.ToString() : "none");
        }
    }
}
