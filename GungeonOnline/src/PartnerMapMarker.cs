using UnityEngine;

namespace GungeonOnline
{
    // Shows every other online player on the minimap and the full map with their character's own map icon (from their
    // character's prefab), or a tinted copy of our marker if that can't be found. It's moved
    // to the partner's position the same way the game places player markers (Minimap.UpdatePlayerPositionExact:
    // minimap position + world position / 8, one unit in front).
    public static class PartnerMapMarker
    {
        private static readonly Color Tint = new Color(0.45f, 0.85f, 1f);

        private static NetSession session;
        private static readonly GameObject[] markers = new GameObject[Protocol.MaxPlayers];
        private static readonly Minimap[] markerMaps = new Minimap[Protocol.MaxPlayers];
        private static readonly int[] markerIdentities = new int[Protocol.MaxPlayers];
        private static readonly string[] markerCustoms = new string[Protocol.MaxPlayers];

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        public static void Update()
        {
            if (session == null)
                return;
            PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            Minimap map = Minimap.Instance;
            bool canShow = session.Status == NetSession.State.Connected && me != null && map != null && !GameManager.Instance.IsLoadingLevel;
            string level = canShow ? LevelSync.CurrentLevelKey() : "";
            for (int i = 0; i < markers.Length; i++)
            {
                RemotePlayerState r = session.Players[i];
                bool show = canShow && i != session.LocalSlot && r.Connected && r.HasPlayer && r.Scene == level;
                if (!show)
                {
                    if (markers[i] != null)
                        markers[i].SetActive(false);
                    continue;
                }
                if (markers[i] == null || markerMaps[i] != map || markerIdentities[i] != r.Identity || markerCustoms[i] != r.CustomCharacter)
                {
                    if (markers[i] != null)
                        Object.Destroy(markers[i]);
                    markers[i] = MakeMarker(me, map, r);
                    if (markers[i] == null)
                        continue;
                    markerMaps[i] = map;
                    markerIdentities[i] = r.Identity;
                    markerCustoms[i] = r.CustomCharacter;
                }
                if (!markers[i].activeSelf)
                    markers[i].SetActive(true);
                Vector2 p = r.Position;
                markers[i].transform.position = map.transform.position + new Vector3(p.x * 0.125f, p.y * 0.125f, -1f);
            }
        }

        // The player's character icon; our own icon, tinted, if their character's prefab has none.
        private static GameObject MakeMarker(PlayerController me, Minimap map, RemotePlayerState remote)
        {
            GameObject icon = null;
            bool own = false;
            try
            {
                string description;
                GameObject prefab = CustomCharacterSupport.PartnerPrefab(remote, out description);
                PlayerController template = prefab != null ? prefab.GetComponent<PlayerController>() : null;
                if (template != null)
                    icon = template.minimapIconPrefab;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Partner map icon: " + e.Message);
            }
            if (icon == null)
            {
                icon = me.minimapIconPrefab;
                own = true;
            }
            if (icon == null)
                return null;
            GameObject m = (GameObject)Object.Instantiate(icon, map.transform.position, Quaternion.identity);
            m.name = "GungeonOnline Partner Map Marker";
            m.transform.parent = map.transform;
            tk2dBaseSprite sprite = m.GetComponent<tk2dBaseSprite>();
            if (sprite != null)
            {
                if (own)
                    sprite.color = Tint;
                if (sprite.renderer != null)
                    sprite.renderer.sortingLayerName = "Foreground";
            }
            Plugin.Log.LogInfo("Partner map marker: " + (own ? "tinted copy of ours (no character icon found)" : "their character's icon") + ".");
            return m;
        }
    }
}
