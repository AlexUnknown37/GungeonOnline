using UnityEngine;

namespace GungeonOnline
{
    // Hides an object's floor reflection.
    // The game draws reflections with a separate camera (Pixelator.IsRenderingReflectionMap is true while it
    // runs), and each object sets "_ReflectionYOffset" on its material just before being drawn to tell the
    // shader where its reflection goes. The puppet's copied material held our own player's value, which
    // drew a misplaced black silhouette. During the reflection pass we push the reflection far off-screen.
    public class NoReflection : MonoBehaviour
    {
        private const float OffScreen = 100000f;
        private Renderer cachedRenderer;
        private float normalValue;
        private bool hasNormalValue;

        private void OnWillRenderObject()
        {
            if (cachedRenderer == null)
                cachedRenderer = GetComponent<Renderer>();
            Material m = cachedRenderer != null ? cachedRenderer.sharedMaterial : null;
            if (m == null || !m.HasProperty("_ReflectionYOffset"))
                return;

            if (Pixelator.IsRenderingReflectionMap)
            {
                if (!hasNormalValue)
                {
                    normalValue = m.GetFloat("_ReflectionYOffset");
                    hasNormalValue = true;
                }
                m.SetFloat("_ReflectionYOffset", OffScreen);
            }
            else if (hasNormalValue)
            {
                m.SetFloat("_ReflectionYOffset", normalValue);
            }
        }
    }
}
