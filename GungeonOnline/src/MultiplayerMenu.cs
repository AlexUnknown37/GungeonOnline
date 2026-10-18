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
            focus = 0;
            pendingActivate = -1;
            // The press that opened us (A on the MULTIPLAYER button) must not also press our first button.
            ignoreInputUntil = Time.realtimeSinceStartup + 0.25f;
            nameField = Plugin.DisplayName();
            addressField = Plugin.LastLanAddress.Value;
            message = "";
        }

        public static void Close()
        {
            open = false;
            MenuGuard.OnMenuClosed();
            session.Discovery.Stop();
        }

        public static void Update()
        {
            if (open)
                ReadController();
            UpdateJoinerTitleStart();
            if (open && (Input.GetKeyDown(KeyCode.Escape) || backPressed))
                Close();
            backPressed = false;
        }

        // ---- Controller (and arrow keys / Enter) ----
        // Every button and switch gets a number as it's drawn; one of them is selected (highlighted). Up/down
        // (D-pad, left stick, arrow keys) moves the selection, A / Enter presses it, B / Esc goes back.
        // Text boxes can't be typed into with a controller, so they're skipped.
        private static int focus, navIndex, navCount, pendingActivate = -1;
        private static bool backPressed, usingController;
        private static float nextStickMoveAt;
        private static Rect focusRect;
        private static bool hasFocusRect;
        private static float viewHeight;
        private static GUIStyle highlighted, highlightedToggle;

        private static float ignoreInputUntil;

        private static void ReadController()
        {
            if (Time.realtimeSinceStartup < ignoreInputUntil)
                return;
            InControl.InputDevice d = InControl.InputManager.ActiveDevice;
            int move = 0;
            bool confirm = false, back = false;
            if (d != null)
            {
                if (d.DPadUp.WasPressed) move = -1;
                if (d.DPadDown.WasPressed) move = 1;
                float y = d.LeftStickY.Value;
                if (Mathf.Abs(y) > 0.6f && Time.realtimeSinceStartup >= nextStickMoveAt)
                {
                    move = y > 0f ? -1 : 1;
                    nextStickMoveAt = Time.realtimeSinceStartup + 0.25f;
                }
                else if (Mathf.Abs(y) < 0.3f)
                {
                    nextStickMoveAt = 0f;
                }
                confirm = d.Action1.WasPressed;
                back = d.Action2.WasPressed;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow)) move = -1;
            if (Input.GetKeyDown(KeyCode.DownArrow)) move = 1;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) confirm = true;
            if (move != 0 || confirm || back)
                usingController = true;
            if (Input.GetAxis("Mouse X") != 0f || Input.GetAxis("Mouse Y") != 0f)
                usingController = false;
            if (move != 0 && navCount > 0)
                focus = (focus + move + navCount) % navCount;
            if (confirm)
                pendingActivate = focus;
            if (back)
                backPressed = true;
        }

        private static GUIStyle Highlight(GUIStyle style, bool toggle)
        {
            if (toggle)
            {
                if (highlightedToggle == null)
                {
                    highlightedToggle = new GUIStyle(style);
                    highlightedToggle.normal.textColor = new Color(0.95f, 0.78f, 0.35f);
                    highlightedToggle.onNormal.textColor = highlightedToggle.normal.textColor;
                }
                return highlightedToggle;
            }
            if (highlighted == null)
            {
                highlighted = new GUIStyle(style);
                highlighted.normal = style.hover;
                highlighted.normal.textColor = new Color(0.95f, 0.78f, 0.35f);
                highlighted.fontStyle = FontStyle.Bold;
            }
            return highlighted;
        }

        // A button that the controller can select and press.
        private static bool Btn(string text, GUIStyle style, params GUILayoutOption[] options)
        {
            int index = navIndex++;
            bool selected = usingController && index == focus;
            bool clicked = GUILayout.Button(selected ? "> " + text + " <" : text, selected ? Highlight(style, false) : style, options);
            Track(index);
            if (pendingActivate == index && Event.current.type == EventType.Layout)
            {
                pendingActivate = -1;
                if (GUI.enabled)
                    clicked = true;
            }
            return clicked;
        }

        private static bool Tog(bool value, string text, GUIStyle style, params GUILayoutOption[] options)
        {
            int index = navIndex++;
            bool selected = usingController && index == focus;
            // The label style draws no checkbox, so the state is written in front of the text.
            text = (value ? "[ON]   " : "[OFF]  ") + text.Trim();
            bool result = GUILayout.Toggle(value, selected ? "> " + text : text, selected ? Highlight(style, true) : style, options);
            Track(index);
            if (pendingActivate == index && Event.current.type == EventType.Layout)
            {
                pendingActivate = -1;
                if (GUI.enabled)
                    result = !value;
            }
            return result;
        }

        // Where the selected control is, to scroll it into view.
        private static void Track(int index)
        {
            if (index == focus && Event.current.type == EventType.Repaint)
            {
                focusRect = GUILayoutUtility.GetLastRect();
                hasFocusRect = true;
            }
        }

        private static void ScrollToFocus()
        {
            if (!usingController || !hasFocusRect || viewHeight <= 0f)
                return;
            if (focusRect.yMax > scroll.y + viewHeight)
                scroll.y = focusRect.yMax - viewHeight + 8f;
            else if (focusRect.y < scroll.y)
                scroll.y = Mathf.Max(0f, focusRect.y - 8f);
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
            if (Event.current.type == EventType.Layout)
            {
                navCount = navIndex;
                navIndex = 0;
                if (navCount > 0 && focus >= navCount)
                    focus = navCount - 1;
            }
            else if (Event.current.type == EventType.Repaint)
            {
                navIndex = 0;
            }
            else
            {
                navIndex = 0;
            }
            viewHeight = r.height - 44f;
            ScrollToFocus();
            GUILayout.BeginArea(new Rect(r.x + 28f, r.y + 22f, r.width - 56f, r.height - 44f));
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("MULTIPLAYER", title);
            if (usingController)
                GUILayout.Label("Controller: up/down to choose, A to press, B to go back.", small);
            GUILayout.Label("Version " + Plugin.ModVersion + "  -  early alpha side project, expect bugs! Every player needs the same version.", small);
            GUILayout.Space(10);

            GUILayout.Label("Your name", label);
            GUILayout.BeginHorizontal();
            nameField = GUILayout.TextField(nameField, 24, field);
            if (Btn("Save", button, GUILayout.Width(100)))
            {
                Plugin.PlayerName.Value = nameField.Trim();
                message = "Others will see you as: " + Plugin.DisplayName();
            }
            GUILayout.EndHorizontal();
            // Shown whether or not you're connected, so it can be set before joining too.
            bool takeBreak = Tog(Autopilot.Active, "  Take a Break: the bot plays until you move (" + Plugin.TakeABreakKey.Value + ")", label);
            if (takeBreak != Autopilot.Active)
                Autopilot.Toggle();
            bool names = Tog(Plugin.ShowPartnerName.Value, "  Show other players' names (only on your screen)", label);
            if (names != Plugin.ShowPartnerName.Value)
                Plugin.ShowPartnerName.Value = names;
            // A host rule: guests see the host's choice.
            if (session.Link != null && !session.Link.IsHost)
            {
                GUILayout.Label("Teleport to fights: " + (session.FightPull ? "on" : "off") + " (the host decides)", small);
                GUILayout.Label("Split money: " + (session.HalfMoney ? "on" : "off") + " (the host decides)", small);
                GUILayout.Label("Enemy health: x" + DifficultySync.Factor.ToString("0.##") + " (the host decides)", small);
            }
            else
            {
                bool fights = Tog(Plugin.TeleportToFights.Value, "  Teleport everyone into fights (when you host)", label);
                if (fights != Plugin.TeleportToFights.Value)
                {
                    Plugin.TeleportToFights.Value = fights;
                    session.SendRules();
                }
                bool scale = Tog(Plugin.ScaleEnemyHealth.Value, "  Tougher enemies with more players: +" + Mathf.RoundToInt(Plugin.HealthPerExtraPlayer.Value * 100f) + "% health each (when you host)", label);
                if (scale != Plugin.ScaleEnemyHealth.Value)
                {
                    Plugin.ScaleEnemyHealth.Value = scale;
                    session.SendRules();
                }
                bool split = Tog(Plugin.HalfMoney.Value, "  Split boss money between players (when you host)", label);
                if (split != Plugin.HalfMoney.Value)
                {
                    Plugin.HalfMoney.Value = split;
                    session.SendRules();
                }
            }
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
            if (Btn("Back", button))
                Close();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawNotConnected()
        {
            GUILayout.Label("Play online with Steam friends", label);
            if (session.SteamAvailable)
            {
                if (Btn("Host on Steam", button))
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
            if (Btn("Host on LAN", button))
            {
                bool ok = session.HostLan(Plugin.LanPort.Value);
                message = ok ? "" : "Couldn't host. Is the port already in use?";
                if (ok)
                    EnterBreachFromTitle();
            }
            if (Btn("Find LAN games", button))
                session.Discovery.Search(Plugin.LanPort.Value);
            foreach (LanDiscovery.FoundHost host in session.Discovery.Found)
            {
                string text = "Join " + host.Name + (host.SameVersion ? "" : "  (different version)");
                if (Btn(text, button) && host.SameVersion)
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
            if (Btn("Join", button, GUILayout.Width(100)))
                JoinAddress(addressField.Trim());
            GUILayout.EndHorizontal();
        }

        private static void DrawConnected()
        {
            Transport link = session.Link;
            string state;
            if (session.Status == NetSession.State.Connected)
                state = "Playing with " + session.RemoteNames() + (session.PingMs >= 0f ? "  (" + Mathf.RoundToInt(session.PingMs) + " ms)" : "")
                    + (link.IsHost && session.Remotes.Count < Protocol.MaxPlayers - 1 ? "  -  room for " + (Protocol.MaxPlayers - 1 - session.Remotes.Count) + " more" : "");
            else if (session.Status == NetSession.State.WaitingForPartner)
                state = "Waiting for a friend to join...";
            else
                state = "Connecting...";
            GUILayout.Label(state, label);
            GUILayout.Label(link.Kind + (link.IsHost ? " host" : "") + "  -  " + link.Info, small);
            GUILayout.Space(8);

            if (link == session.Steam && link.IsHost && Btn("Invite friend", button))
                session.Steam.Lobby.OpenInviteOverlay();

            if (session.Status == NetSession.State.Connected)
            {
                string why;
                bool canTeleport = PlayerWarp.CanTeleportToPartner(out why);
                GUI.enabled = canTeleport;
                if (Btn(canTeleport ? "Teleport to partner" : "Teleport to partner (" + why + ")", button))
                {
                    PlayerWarp.TeleportToPartner();
                    Close();
                }
                GUI.enabled = true;
            }

            if (link.IsHost)
            {
                bool pvp = Tog(Plugin.Pvp.Value, "  PvP: players' bullets hurt each other", label);
                if (pvp != Plugin.Pvp.Value)
                {
                    Plugin.Pvp.Value = pvp;
                    session.SendRules();
                }
            }
            else
            {
                GUILayout.Label("PvP: " + (session.PvpEnabled ? "on" : "off") + " (the host decides)", small);
            }

            GUILayout.Space(8);
            if (Btn(link.IsHost ? "Stop hosting" : "Leave", button))
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
                if (f.Hosting && Btn("Join", button, GUILayout.Width(100)))
                {
                    message = "Joining " + f.Name + "...";
                    session.JoinSteamLobby(f.Lobby);
                }
                GUILayout.EndHorizontal();
            }
        }

        // Hosting or joining from the title screen: leave the title screen right away by pressing one of its own
        // buttons (exactly what clicking it does). Does nothing outside the title screen.
        // - Host: PLAY (into the Breach).
        // - Joiner: first waits until connected and told where the host is. If the host is in a run and we have a
        //   saved run, CONTINUE: we get our saved character and items back, on the host's floor (LevelSync).
        //   Otherwise PLAY (the Breach; a run started there joins the host's run).
        private const float JoinDecisionTimeout = 8f;
        private static float joinerTitleStartAt = -1f;
        private static float connectedSince = -1f;

        public static void EnterBreachFromTitle()
        {
            MainMenuFoyerController title = TitleScreen();
            if (title == null)
                return;
            if (session.Link != null && !session.Link.IsHost)
            {
                joinerTitleStartAt = Time.realtimeSinceStartup;
                connectedSince = -1f;
                message = "Connecting...";
                return;
            }
            Plugin.Log.LogInfo("Started multiplayer from the title screen: entering the Breach.");
            Close();
            title.NewGameButton.DoClick();
        }

        private static MainMenuFoyerController TitleScreen()
        {
            MainMenuFoyerController title = Object.FindObjectOfType(typeof(MainMenuFoyerController)) as MainMenuFoyerController;
            if (title == null || title.NewGameButton == null || !title.NewGameButton.IsVisible)
                return null;
            return title;
        }

        private static void UpdateJoinerTitleStart()
        {
            if (joinerTitleStartAt < 0f)
                return;
            float now = Time.realtimeSinceStartup;
            if (session.Link == null)
            {
                joinerTitleStartAt = -1f; // gave up joining
                return;
            }
            if (session.Status == NetSession.State.Connected && connectedSince < 0f)
                connectedSince = now;
            // The host's "where I am" arrives right after connecting; give it a moment.
            bool ready = connectedSince >= 0f && now - connectedSince > 1f;
            if (!ready && now - joinerTitleStartAt < JoinDecisionTimeout)
                return;
            joinerTitleStartAt = -1f;
            MainMenuFoyerController title = TitleScreen();
            if (title == null)
                return;
            Close();
            bool canContinue = title.ContinueGameButton != null && title.ContinueGameButton.IsVisible && GameManager.HasValidMidgameSave();
            // The host remembering us in its run beats our own saved run (that rejoins with our items as they were).
            if (LevelSync.HostInRun && canContinue && !InventorySync.HasRejoinOffer)
            {
                Plugin.Log.LogInfo("Joined a host who is in a run: continuing our saved run on their floor.");
                title.ContinueGameButton.DoClick();
            }
            else
            {
                Plugin.Log.LogInfo("Joined from the title screen: entering the Breach" + (LevelSync.HostInRun ? " (the host is in a run; start one to join it)." : "."));
                title.NewGameButton.DoClick();
            }
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
