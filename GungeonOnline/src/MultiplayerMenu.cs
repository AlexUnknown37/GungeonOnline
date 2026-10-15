using System.Net;
using UnityEngine;

namespace GungeonOnline
{
    // The Multiplayer menu opened from the game's title screen / pause menu ("MULTIPLAYER" button).
    // A big centered panel in game-like colors with everything needed to play: your name, host on Steam
    // (+ invite), host on LAN, find/join LAN games, PvP, teleport to partner, leave. The F8 window stays
    // as a backup with the same actions plus debug info.
    public static class MultiplayerMenu
    {
        private static NetSession session;
        private static bool open;
        private static string nameField = "";
        private static string addressField = "";
        private static string message = "";
        private static Vector2 scroll;

        private static GUIStyle panel, title, label, small, button, field;

        public static bool IsOpen
        {
            get { return open; }
        }

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        public static void Open()
        {
            open = true;
            nameField = Plugin.DisplayName();
            addressField = Plugin.LastLanAddress.Value;
            message = "";
        }

        public static void Close()
        {
            open = false;
            session.Discovery.Stop();
        }

        public static void Update()
        {
            if (open && Input.GetKeyDown(KeyCode.Escape))
                Close();
        }

        private static void MakeStyles()
        {
            if (panel != null)
                return;
            Texture2D bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.09f, 0.07f, 0.06f, 0.96f));
            bg.Apply();
            panel = new GUIStyle(GUI.skin.box);
            panel.normal.background = bg;
            panel.padding = new RectOffset(28, 28, 22, 22);

            title = new GUIStyle(GUI.skin.label);
            title.fontSize = 34;
            title.fontStyle = FontStyle.Bold;
            title.alignment = TextAnchor.MiddleCenter;
            title.normal.textColor = new Color(0.95f, 0.78f, 0.35f);

            label = new GUIStyle(GUI.skin.label);
            label.fontSize = 20;
            label.wordWrap = true;
            label.normal.textColor = Color.white;

            small = new GUIStyle(label);
            small.fontSize = 16;
            small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            button = new GUIStyle(GUI.skin.button);
            button.fontSize = 22;
            button.fixedHeight = 44;

            field = new GUIStyle(GUI.skin.textField);
            field.fontSize = 20;
            field.fixedHeight = 34;
        }

        public static void Draw()
        {
            if (!open)
                return;
            MakeStyles();
            float w = Mathf.Min(620f, Screen.width - 40f);
            float h = Mathf.Min(720f, Screen.height - 40f);
            Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(r, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(r.x + 28f, r.y + 22f, r.width - 56f, r.height - 44f));
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("MULTIPLAYER", title);
            GUILayout.Label("Early alpha side project - expect bugs!", small);
            GUILayout.Space(10);

            GUILayout.Label("Your name", label);
            GUILayout.BeginHorizontal();
            nameField = GUILayout.TextField(nameField, 24, field);
            if (GUILayout.Button("Save", button, GUILayout.Width(100)))
            {
                Plugin.PlayerName.Value = nameField.Trim();
                message = "Others will see you as: " + Plugin.DisplayName();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(12);

            if (session.Link == null)
                DrawNotConnected();
            else
                DrawConnected();

            if (message.Length > 0)
            {
                GUILayout.Space(8);
                GUILayout.Label(message, small);
            }

            GUILayout.Space(14);
            if (GUILayout.Button("Back", button))
                Close();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawNotConnected()
        {
            GUILayout.Label("Play online with Steam friends", label);
            if (session.SteamAvailable)
            {
                if (GUILayout.Button("Host on Steam", button))
                {
                    session.HostSteam();
                    message = "Lobby created. Use \"Invite friend\" to bring someone in.";
                    EnterBreachFromTitle();
                }
                GUILayout.Label("To join a friend: accept their Steam invite, or pick them below.", small);
                DrawFriends();
            }
            else
            {
                GUILayout.Label("Steam isn't available (game not started through Steam).", small);
            }

            GUILayout.Space(12);
            GUILayout.Label("Play on your home network (LAN)", label);
            if (GUILayout.Button("Host on LAN", button))
            {
                bool ok = session.HostLan(Plugin.LanPort.Value);
                message = ok ? "" : "Couldn't host. Is the port already in use?";
                if (ok)
                    EnterBreachFromTitle();
            }
            if (GUILayout.Button("Find LAN games", button))
                session.Discovery.Search(Plugin.LanPort.Value);
            foreach (LanDiscovery.FoundHost host in session.Discovery.Found)
            {
                string text = "Join " + host.Name + (host.SameVersion ? "" : "  (different version)");
                if (GUILayout.Button(text, button) && host.SameVersion)
                {
                    session.Discovery.Stop();
                    bool ok = session.JoinLan(host.Address);
                    message = ok ? "" : "Couldn't join.";
                    if (ok)
                        EnterBreachFromTitle();
                }
            }
            if (session.Discovery.Searching && session.Discovery.Found.Count == 0)
                GUILayout.Label("Searching...", small);

            GUILayout.Label("Or join by address (e.g. 192.168.1.20):", small);
            GUILayout.BeginHorizontal();
            addressField = GUILayout.TextField(addressField, field);
            if (GUILayout.Button("Join", button, GUILayout.Width(100)))
                JoinAddress(addressField.Trim());
            GUILayout.EndHorizontal();
        }

        private static void DrawConnected()
        {
            Transport link = session.Link;
            string state;
            if (session.Status == NetSession.State.Connected)
                state = "Playing with " + session.PeerName + (session.PingMs >= 0f ? "  (" + Mathf.RoundToInt(session.PingMs) + " ms)" : "");
            else if (session.Status == NetSession.State.WaitingForPartner)
                state = "Waiting for a friend to join...";
            else
                state = "Connecting...";
            GUILayout.Label(state, label);
            GUILayout.Label(link.Kind + (link.IsHost ? " host" : "") + "  -  " + link.Info, small);
            GUILayout.Space(8);

            if (link == session.Steam && link.IsHost && GUILayout.Button("Invite friend", button))
                session.Steam.Lobby.OpenInviteOverlay();

            if (session.Status == NetSession.State.Connected)
            {
                string why;
                bool canTeleport = PlayerWarp.CanTeleportToPartner(out why);
                GUI.enabled = canTeleport;
                if (GUILayout.Button(canTeleport ? "Teleport to partner" : "Teleport to partner (" + why + ")", button))
                {
                    PlayerWarp.TeleportToPartner();
                    Close();
                }
                GUI.enabled = true;
            }

            if (link.IsHost)
            {
                bool pvp = GUILayout.Toggle(Plugin.Pvp.Value, "  PvP: players' bullets hurt each other", label);
                if (pvp != Plugin.Pvp.Value)
                {
                    Plugin.Pvp.Value = pvp;
                    session.SendRules();
                }
                bool half = GUILayout.Toggle(Plugin.HalfMoney.Value, "  Half money: shared purse, pickups count half", label);
                if (half != Plugin.HalfMoney.Value)
                {
                    Plugin.HalfMoney.Value = half;
                    session.SendRules();
                }
            }
            else
            {
                GUILayout.Label("PvP: " + (session.PvpEnabled ? "on" : "off") + " (the host decides)", small);
                GUILayout.Label("Half money: " + (session.HalfMoney ? "on" : "off") + " (the host decides)", small);
            }

            GUILayout.Space(8);
            if (GUILayout.Button(link.IsHost ? "Stop hosting" : "Leave", button))
            {
                session.Leave();
                message = "";
            }
        }

        // Steam friends playing Enter the Gungeon right now; hosts get a Join button.
        private static void DrawFriends()
        {
            SteamFriendsList.Refresh(false);
            GUILayout.Space(6);
            GUILayout.Label("Friends playing Enter the Gungeon", label);
            if (SteamFriendsList.Playing.Count == 0)
            {
                GUILayout.Label("None right now.", small);
                return;
            }
            foreach (SteamFriendsList.Friend f in SteamFriendsList.Playing)
            {
                GUILayout.BeginHorizontal();
                string status;
                if (f.Hosting)
                    status = "hosting" + (f.ModVersion.Length > 0 ? " (Gungeon Online " + f.ModVersion + ")" : "");
                else if (f.ModVersion.Length > 0)
                    status = "has Gungeon Online " + f.ModVersion;
                else
                    status = "playing";
                if (f.ModVersion.Length > 0 && f.ModVersion != Plugin.ModVersion)
                    status += "  - different version!";
                GUILayout.Label(f.Name + "  -  " + status, small);
                if (f.Hosting && GUILayout.Button("Join", button, GUILayout.Width(100)))
                {
                    message = "Joining " + f.Name + "...";
                    session.JoinSteamLobby(f.Lobby);
                }
                GUILayout.EndHorizontal();
            }
        }

        // Hosting or joining from the title screen: go into the Breach right away, by pressing the title
        // screen's own PLAY button (exactly what clicking it does). Does nothing outside the title screen.
        public static void EnterBreachFromTitle()
        {
            MainMenuFoyerController title = Object.FindObjectOfType(typeof(MainMenuFoyerController)) as MainMenuFoyerController;
            if (title == null || title.NewGameButton == null || !title.NewGameButton.IsVisible)
                return;
            Plugin.Log.LogInfo("Started multiplayer from the title screen: entering the Breach.");
            Close();
            title.NewGameButton.DoClick();
        }

        // Accepts "192.168.1.20" or "192.168.1.20:27460".
        private static void JoinAddress(string text)
        {
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
            bool joined = session.JoinLan(new IPEndPoint(address, port));
            message = joined ? "Joining..." : "Couldn't join.";
            if (joined)
                EnterBreachFromTitle();
        }
    }
}
