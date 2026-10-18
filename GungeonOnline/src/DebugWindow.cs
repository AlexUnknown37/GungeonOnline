using System.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GungeonOnline
{
    // A simple on-screen window: pick a name, host or join over Steam or LAN, and check the connection.
    // Also draws a name tag where each other player is standing.
    public class DebugWindow
    {
        public bool Visible = false; // backup/debug window; the real menu is MULTIPLAYER in the game menus

        private readonly NetSession session;
        private Rect windowRect = new Rect(20, 20, 320, 10);
        private string nameField;
        private string addressField;
        private string message = "";

        public DebugWindow(NetSession session)
        {
            this.session = session;
            nameField = Plugin.DisplayName();
            addressField = Plugin.LastLanAddress.Value;
        }

        public void Draw()
        {
            foreach (RemotePlayerState p in session.Remotes)
                DrawRemoteMarker(p);
            if (PauseSync.PartnerPaused)
            {
                GUIStyle big = new GUIStyle(GUI.skin.label);
                big.fontSize = 28;
                big.alignment = TextAnchor.MiddleCenter;
                big.normal.textColor = Color.white;
                GUI.Label(new Rect(0f, Screen.height * 0.35f - 30f, Screen.width, 60f), PauseSync.PausedBy + " paused the game", big);
            }
            if (LevelSync.WaitingForHost)
            {
                GUIStyle big = new GUIStyle(GUI.skin.label);
                big.fontSize = 28;
                big.alignment = TextAnchor.MiddleCenter;
                big.normal.textColor = Color.white;
                GUI.Label(new Rect(0f, Screen.height * 0.5f - 40f, Screen.width, 80f), LevelSync.WaitingText, big);
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
            bool takeBreak = GUILayout.Toggle(Autopilot.Active, " Take a Break (the bot plays for you until you move, " + Plugin.TakeABreakKey.Value + ")");
            if (takeBreak != Autopilot.Active)
                Autopilot.Toggle();
            bool names = GUILayout.Toggle(Plugin.ShowPartnerName.Value, " Show other players' names (only on your screen)");
            if (names != Plugin.ShowPartnerName.Value)
                Plugin.ShowPartnerName.Value = names;
            // A host rule: guests see the host's choice.
            if (session.Link != null && !session.Link.IsHost)
            {
                GUILayout.Label("Teleport to fights: " + (session.FightPull ? "ON" : "off") + " (host's choice)");
                GUILayout.Label("Split money: " + (session.HalfMoney ? "ON" : "off") + " (host's choice)");
                GUILayout.Label("Enemy health: x" + DifficultySync.Factor.ToString("0.##") + " (host's choice)");
            }
            else
            {
                bool fights = GUILayout.Toggle(Plugin.TeleportToFights.Value, " Teleport everyone into fights (when you host)");
                if (fights != Plugin.TeleportToFights.Value)
                {
                    Plugin.TeleportToFights.Value = fights;
                    session.SendRules();
                }
                bool scale = GUILayout.Toggle(Plugin.ScaleEnemyHealth.Value, " Tougher enemies with more players: +" + Mathf.RoundToInt(Plugin.HealthPerExtraPlayer.Value * 100f) + "% health each (when you host)");
                if (scale != Plugin.ScaleEnemyHealth.Value)
                {
                    Plugin.ScaleEnemyHealth.Value = scale;
                    session.SendRules();
                }
                bool split = GUILayout.Toggle(Plugin.HalfMoney.Value, " Split money: pickups count 1/players (when you host)");
                if (split != Plugin.HalfMoney.Value)
                {
                    Plugin.HalfMoney.Value = split;
                    session.SendRules();
                }
            }
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
            GUILayout.Label("Status: " + session.Status + "  (you are player " + (session.LocalSlot + 1) + ")");

            if (session.Status == NetSession.State.Connected)
            {
                foreach (RemotePlayerState remote in session.Remotes)
                {
                    GUILayout.Label("-- Player " + (remote.Slot + 1) + ": " + remote.Name
                        + (remote.PingMs >= 0f ? "  (" + Mathf.RoundToInt(remote.PingMs) + " ms)" : ""));
                    if (remote.HasPlayer)
                    {
                        GUILayout.Label((PlayableCharacters)remote.Identity + (remote.AltCostume ? " (alt costume)" : "")
                            + " at " + remote.Position.ToString("F1") + " in " + remote.Scene);
                        GUILayout.Label("Sprite: " + Plugin.Puppets[remote.Slot].Details());
                        if (remote.IsDead)
                            GUILayout.Label("A GHOST: open a chest to bring them back!");
                    }
                    else
                        GUILayout.Label("In menus (" + remote.Scene + ")");
                }
                if (LevelSync.WaitingToJoin)
                    GUILayout.Label("Host started a new run: Quick Restart to join it.");
                PlayerController me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                if (me != null && me.IsGhost)
                    GUILayout.Label("You are a ghost: a living player can bring you back by opening a chest.");
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

        // Draws a player's name over their position, if we're in the same level.
        private GUIStyle nameStyle, nameShadow;

        private void DrawRemoteMarker(RemotePlayerState remote)
        {
            if (session.Status != NetSession.State.Connected || !remote.HasPlayer)
                return;
            if (remote.Scene != LevelSync.CurrentLevelKey())
                return;
            if (!GameManager.HasInstance || GameManager.Instance.MainCameraController == null)
                return;
            // Not over the Ammonomicon (death page, item book) or the loop's time tube.
            if (LevelSync.BookOpen() || LoopSync.TubePlaying)
                return;

            Camera cam = GameManager.Instance.MainCameraController.Camera;
            if (cam == null)
                return;

            if (!Plugin.ShowPartnerName.Value)
                return;
            // Above the drawn (smoothed) character; the network position runs ahead of it.
            Vector3 head;
            if (!Plugin.Puppets[remote.Slot].TryGetHeadTop(out head))
                return;
            Vector3 screen = cam.WorldToScreenPoint(head);
            if (screen.z < 0f)
                return;
            if (nameStyle == null)
            {
                nameStyle = new GUIStyle(GUI.skin.label);
                nameStyle.alignment = TextAnchor.LowerCenter;
                nameStyle.fontStyle = FontStyle.Bold;
                nameShadow = new GUIStyle(nameStyle);
                nameShadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            }
            string nameText = remote.OnBreak ? remote.Name + " (bot)" : remote.Name;
            Rect r = new Rect(screen.x - 100f, Screen.height - screen.y - 26f, 200f, 24f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), nameText, nameShadow);
            GUI.Label(r, nameText, nameStyle);
        }
    }
}
