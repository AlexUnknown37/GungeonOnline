using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline
{
    // How the partner's character is tinted right now, read from their sprite's material:
    // - SpecialFlags + FlatColor: the ghost look (PlayerController.BecomeGhost sets these via
    //   ChangeSpecialShaderFlag / ChangeFlatColorOverride).
    // - OverrideColor: status tints (poison, freeze, charm, ...), GameActor.CurrentOverrideColor.
    // - OnFire: PlayerController.IsOnFire; its flames are separate sprites, recreated on the puppet.
    // The puppet's material uses the same shader, so the same values give the same look.
    public class PlayerLook
    {
        public Vector4 SpecialFlags;
        public Color FlatColor = new Color(0f, 0f, 0f, 0f);
        public Color OverrideColor = new Color(0f, 0f, 0f, 0f);
        public bool OnFire;

        // The shader property IDs the game uses (GameActor fields, set from Shader.PropertyToID).
        private static readonly FieldInfo specialFlagsIdField = AccessTools.Field(typeof(GameActor), "m_specialFlagsID");
        private static readonly FieldInfo flatColorIdField = AccessTools.Field(typeof(GameActor), "m_overrideFlatColorID");
        private static readonly FieldInfo overrideColorIdField = AccessTools.Field(typeof(GameActor), "m_overrideColorID");
        private static int specialFlagsId, flatColorId, overrideColorId;
        private static bool haveIds;

        private static bool GetIds(PlayerController p)
        {
            if (haveIds)
                return true;
            if (p == null)
                return false;
            specialFlagsId = (int)specialFlagsIdField.GetValue(p);
            flatColorId = (int)flatColorIdField.GetValue(p);
            overrideColorId = (int)overrideColorIdField.GetValue(p);
            haveIds = specialFlagsId != 0 || flatColorId != 0 || overrideColorId != 0;
            return haveIds;
        }

        public static PlayerLook Read(PlayerController p)
        {
            PlayerLook look = new PlayerLook();
            if (p == null || p.sprite == null || p.sprite.renderer == null || !GetIds(p))
                return look;
            Material m = p.sprite.renderer.sharedMaterial;
            if (m != null)
            {
                if (m.HasProperty(specialFlagsId)) look.SpecialFlags = m.GetVector(specialFlagsId);
                if (m.HasProperty(flatColorId)) look.FlatColor = m.GetColor(flatColorId);
                if (m.HasProperty(overrideColorId)) look.OverrideColor = m.GetColor(overrideColorId);
            }
            look.OnFire = p.IsOnFire;
            return look;
        }

        // Put this look on a puppet's material (its own copy, so our player isn't affected).
        public void ApplyTo(Material m)
        {
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (m == null || !GetIds(me))
                return;
            if (m.HasProperty(specialFlagsId)) m.SetVector(specialFlagsId, SpecialFlags);
            if (m.HasProperty(flatColorId)) m.SetColor(flatColorId, FlatColor);
            if (m.HasProperty(overrideColorId)) m.SetColor(overrideColorId, OverrideColor);
        }
    }

    // Flames on the partner's puppet while they're on fire: the same flame sprites the game puts on a
    // burning player (PlayerStats.OnFireSourceEffect.FlameVfx).
    public class PuppetFlames
    {
        private const int FlameCount = 3;
        private readonly List<GameObject> flames = new List<GameObject>();

        public void Update(bool onFire, tk2dBaseSprite body)
        {
            if (!onFire || body == null || !body.gameObject.activeInHierarchy)
            {
                Clear();
                return;
            }
            if (flames.Count > 0)
            {
                if (flames[0] == null) // destroyed with the puppet on a scene change
                    flames.Clear();
                else
                    return;
            }
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            GameActorFireEffect fire = me != null && me.stats != null ? me.stats.OnFireSourceEffect : null;
            if (fire == null || fire.FlameVfx == null || fire.FlameVfx.Count == 0)
                return;

            Bounds b = body.GetBounds();
            for (int i = 0; i < FlameCount; i++)
            {
                GameObject prefab = fire.FlameVfx[Random.Range(0, fire.FlameVfx.Count)];
                if (prefab == null)
                    continue;
                Vector3 local = new Vector3(Random.Range(b.min.x, b.max.x), Random.Range(b.min.y, b.center.y + b.extents.y * 0.5f), -0.1f);
                GameObject flame = (GameObject)Object.Instantiate(prefab);
                flame.transform.parent = body.transform;
                flame.transform.localPosition = local;
                flames.Add(flame);
            }
        }

        public void Clear()
        {
            foreach (GameObject f in flames)
            {
                if (f != null)
                    Object.Destroy(f);
            }
            flames.Clear();
        }
    }
}
