using System.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GungeonOnline
{
    // A simple on-screen window: pick a name, host or join over Steam or LAN, and check the connection.
    // Also draws a name tag where the other player is standing.
    public class DebugWindow
    {
        public bool Visible = true;

        private readonly NetSession session;
        private readonly RemotePuppet puppet;
        private Rect windowRect = new Rect(20, 20, 320, 10);
        private string nameField;
        private string addressField;
        private string message = "";

        public DebugWindow(NetSession session, RemotePuppet puppet)
        {
            this.session = session;
            this.puppet = puppet;
            nameField = Plugin.DisplayName();
            addressField = Plugin.LastLanAddress.Value;
        }

        public void Draw()
        {
            DrawRemoteMarker();
            if (PauseSync.PartnerPaused)
            {
                GUIStyle big = new GUIStyle(GUI.skin.label);
                big.fontSize = 28;
                big.alignment = TextAnchor.MiddleCenter;
                big.normal.textColor = Color.white;
                GUI.Label(new Rect(0f, Screen.height * 0.35f - 30f, Screen.width, 60f), session.PeerName + " paused the game", big);
            }
            if (LevelSync.WaitingForHost)
            {
                GUIStyle big = new GUIStyle(GUI.skin.label);
                big.fontSize = 28;
                big.alignment = TextAnchor.MiddleCenter;
                big.normal.textColor = Color.white;
                GUI.Label(new Rect(0f, Screen.height * 0.5f - 30f, Screen.width, 60f), "Waiting for the host to restart...", big);
            }
            if (Visible)
                windowRect = GUILayout.Window(0x6E7601, windowRect, DrawContents, Plugin.Name + " " + Plugin.ModVersion);
        }

        private void DrawContents(int id)
        {
            DrawNameRow();
            GUILayout.Space(6);

            if (session.Link == null)
                DrawOfflineOptions();
            else
                DrawSessionInfo();

            if (message.Length > 0)
                GUILayout.Label(message);
            GUILayout.Label("F8 hides this window.");
            GUI.DragWindow();
        }

        private void DrawNameRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name:", GUILayout.Width(45));
            nameField = GUILayout.TextField(nameField, 24);
            if (GUILayout.Button("Save", GUILayout.Width(50)))
            {
                Plugin.PlayerName.Value = nameField.Trim();
                message = "Name saved. Others will see: " + Plugin.DisplayName();
            }
            GUILayout.EndHorizontal();
            if (nameField.Trim().Length == 0)
                GUILayout.Label("(empty = your Steam name)");
        }

        private void DrawOfflineOptions()
        {
            GUILayout.Label("-- Steam --");
            if (session.SteamAvailable)
            {
                if (GUILayout.Button("Host on Steam"))
                    session.HostSteam();
                GUILayout.Label("To join: accept a friend's Steam invite.");
            }
            else
            {
                GUILayout.Label("Steam isn't available (game not started through Steam).");
            }

            GUILayout.Space(6);
            GUILayout.Label("-- LAN (same network) --");
            if (GUILayout.Button("Host on LAN (port " + Plugin.LanPort.Value + ")"))
            {
                if (!session.HostLan(Plugin.LanPort.Value))
                    message = "Couldn't host. Is the port already in use? See the log.";
            }

            GUILayout.BeginHorizontal();
            addressField = GUILayout.TextField(addressField);
            if (GUILayout.Button("Join", GUILayout.Width(50)))
                JoinAddress(addressField);
            GUILayout.EndHorizontal();
            GUILayout.Label("Type the host's address, e.g. 192.168.1.20");

            if (GUILayout.Button("Find LAN games"))
                session.Discovery.Search(Plugin.LanPort.Value);
            foreach (LanDiscovery.FoundHost host in session.Discovery.Found)
            {
                string label = host.Name + "  (" + host.Address + ")" + (host.SameVersion ? "" : "  [different version]");
                if (GUILayout.Button(label) && host.SameVersion)
                {
                    session.Discovery.Stop();
                    Join(host.Address);
                }
            }
            if (session.Discovery.Searching && session.Discovery.Found.Count == 0)
                GUILayout.Label("Searching...");
        }

        private void DrawSessionInfo()
        {
            Transport link = session.Link;
            GUILayout.Label("Mode: " + link.Kind + (link.IsHost ? " (host)" : ""));
            GUILayout.Label(link.Info);
            GUILayout.Label("Status: " + session.Status);
            if (session.PeerName.Length > 0)
                GUILayout.Label("Partner: " + session.PeerName);
            if (session.PingMs >= 0f)
                GUILayout.Label("Ping: " + Mathf.RoundToInt(session.PingMs) + " ms");

            RemotePlayerState remote = session.Remote;
            if (session.Status == NetSession.State.Connected)
            {
                if (remote.HasPlayer)
                {
                    GUILayout.Label("Partner at " + remote.Position.ToString("F1") + " in " + remote.Scene);
                    GUILayout.Label("Partner is " + (PlayableCharacters)remote.Identity + (remote.AltCostume ? " (alt costume)" : "")
                        + ", anim " + remote.Clip + " #" + remote.Frame);
                    GUILayout.Label("Partner sprite: " + puppet.Status);
                    if (LevelSync.WaitingToJoin)
                        GUILayout.Label("Host started a new run: Quick Restart to join it.");
                    if (remote.IsDead)
                        GUILayout.Label("PARTNER IS A GHOST: open a chest to bring them back!");
                    PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                    if (me != null && me.IsGhost)
                        GUILayout.Label("You are a ghost: your partner can bring you back by opening a chest.");
                }
                else
                    GUILayout.Label("Partner is in menus (" + remote.Scene + ")");
            }

            if (session.Status == NetSession.State.Connected)
            {
                string why;
                bool canTeleport = PlayerWarp.CanTeleportToPartner(out why);
                GUI.enabled = canTeleport;
                if (GUILayout.Button(canTeleport ? "Teleport to partner" : "Teleport to partner (" + why + ")"))
                    PlayerWarp.TeleportToPartner();
                GUI.enabled = true;
            }

            if (link.IsHost)
            {
                bool pvp = GUILayout.Toggle(Plugin.Pvp.Value, " PvP: bullets hurt each other");
                if (pvp != Plugin.Pvp.Value)
                {
                    Plugin.Pvp.Value = pvp;
                    session.SendRules();
                }
            }
            else
            {
                GUILayout.Label("PvP: " + (session.PvpEnabled ? "ON (host's choice)" : "off (host's choice)"));
            }

            if (link == session.Steam && link.IsHost && GUILayout.Button("Invite friend (Steam overlay)"))
                session.Steam.Lobby.OpenInviteOverlay();
            if (GUILayout.Button("Leave"))
            {
                session.Leave();
                message = "";
            }
        }

        // Accepts "192.168.1.20" or "192.168.1.20:27460".
        private void JoinAddress(string text)
        {
            text = text.Trim();
            int port = Plugin.LanPort.Value;
            string host = text;
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                host = text.Substring(0, colon);
                if (!int.TryParse(text.Substring(colon + 1), out port))
                {
                    message = "That port isn't a number.";
                    return;
                }
            }

            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                message = "That isn't an IP address (like 192.168.1.20).";
                return;
            }
            Plugin.LastLanAddress.Value = text;
            Join(new IPEndPoint(address, port));
        }

        private void Join(IPEndPoint host)
        {
            message = session.JoinLan(host) ? "" : "Couldn't start joining. See the log.";
        }

        // Draws the partner's name over their position, if we're in the same level.
        private void DrawRemoteMarker()
        {
            RemotePlayerState remote = session.Remote;
            if (session.Status != NetSession.State.Connected || !remote.HasPlayer)
                return;
            if (remote.Scene != LevelSync.CurrentLevelKey())
                return;
            if (!GameManager.HasInstance || GameManager.Instance.MainCameraController == null)
                return;

            Camera cam = GameManager.Instance.MainCameraController.Camera;
            if (cam == null)
                return;

            Vector3 screen = cam.WorldToScreenPoint(new Vector3(remote.Position.x, remote.Position.y, 0f));
            if (screen.z < 0f)
                return;
            GUI.Label(new Rect(screen.x - 50f, Screen.height - screen.y - 40f, 100f, 22f), "[" + session.PeerName + "]");
        }
    }
}
