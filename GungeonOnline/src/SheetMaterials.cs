using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline
{
    // The materials a puppet sprite uses, one per sprite sheet (see RemotePuppet.ApplySheet).
    // Destroyed together with the puppet.
    public class SheetMaterials : MonoBehaviour
    {
        public readonly Dictionary<Texture, Material> BySheet = new Dictionary<Texture, Material>();

        private void OnDestroy()
        {
            foreach (Material m in BySheet.Values)
            {
                if (m != null)
                    Destroy(m);
            }
            BySheet.Clear();
        }
    }
}
