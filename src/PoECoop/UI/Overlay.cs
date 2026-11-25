using System;
using System.Collections.Generic;
using PoECoop.Net;
using PoECoop.Sync;
using Steamworks;
using UnityEngine;

namespace PoECoop.UI
{
    /// <summary>
    /// The co-op window: Steam lobbies or the direct-IP connect form, player list
    /// and chat. Steamworks is touched only from Update, never from OnGUI.
    /// </summary>
    public sealed class Overlay
    {
        private const int WindowId = 0x504F4543; // "POEC"
        private const float RecentLineSeconds = 8f;
        private static readonly string[] TextFields = { "coop.name", "coop.address", "coop.port", "coop.chat" };

        private enum SteamActionKind
        {
            Host,
            RefreshFriends,
            OpenInviteDialog,
            InviteFriend,
            JoinPendingInvite,
            DismissPendingInvite,
            LeaveSession,
            SendChat,
            AssignParty,
        }

        private struct SteamAction
        {
            public SteamActionKind Kind;
            public CSteamID Friend;
            public string Text;
            public PartyMemberAI Member;
            public int Owner;
            public long Generation;
        }

        private struct FriendRow
        {
            public CSteamID Id;
            public string Name;
        }

        private struct MemberRow
        {
            public CSteamID Id;
            public string Name;
            public bool IsHost;
            public bool IsLocal;
        }

        private readonly Session _session;
        private readonly Settings _settings;
        private readonly Ownership _ownership;
        private readonly SteamLobbyService _steam;
        private readonly Queue<SteamAction> _steamActions = new Queue<SteamAction>();
        private readonly List<FriendRow> _friends = new List<FriendRow>();
        private readonly List<MemberRow> _members = new List<MemberRow>();
        private readonly Dictionary<ulong, string> _friendInviteStatus = new Dictionary<ulong, string>();

        private Rect _window = new Rect(20, 20, 560, 0);
        private Vector2 _chatScroll;
        private Vector2 _friendsScroll;
        private GUIStyle _wrappedLabel;
        private GUIStyle _boldLabel;
        private string _chatInput = "";
        private string _name, _address, _port;
        private int _seenLines;
        private float _lastLineTime = -100f;
        private bool _blockingKeys;
        private float _scale = 1f;
        private bool _steamMode;
        private bool _steamAvailable;
        private bool _inSteamLobby;
        private bool _steamBusy;
        private SteamLobbyVisibility _steamVisibility;
        private string _steamStatus = "";
        private string _nativeInviteStatus = "";
        private string _steamUiError = "";
        private bool _hasPendingInvite;
        private CSteamID _pendingInviteLobby;
        private CSteamID _pendingInviterId;
        private string _pendingInviter = "";
        private string _pendingLobby = "";
        private string _windowTitle;
        private KeyCode _windowTitleKey;
        private string _collapsedText;
        private KeyCode _collapsedKey;
        private int _collapsedKind = int.MinValue;

        public bool Visible { get; set; }

        /// <summary>True while the mouse is over the window, so clicks should not reach the game world.</summary>
        public static bool MouseOver { get; private set; }

        public Overlay(Session session, Settings settings, Ownership ownership)
            : this(session, settings, ownership, null)
        {
        }

        public Overlay(Session session, Settings settings, Ownership ownership, SteamLobbyService steam)
        {
            _ownership = ownership;
            _session = session;
            _settings = settings;
            _steam = steam;
            _name = settings.Name.Value;
            _address = settings.Address.Value;
            _port = settings.Port.Value.ToString();
            _steamAvailable = steam != null && steam.IsAvailable;
            _steamMode = settings.PreferredTransport.Value == TransportPreference.Steam && _steamAvailable;
            if (_steam != null) _steam.LobbyInviteReceived += OnLobbyInviteReceived;
            if (_steamAvailable) QueueSteamAction(SteamActionKind.RefreshFriends);
        }

        public void Update()
        {
            long perf = PerfStats.Begin();
            RunSteamActions();
            RefreshSteamSnapshot();
            PerfStats.End("overlay.update", perf);

            if (_session.LinesVersion != _seenLines)
            {
                _seenLines = _session.LinesVersion;
                _lastLineTime = Time.unscaledTime;
                _chatScroll.y = float.MaxValue;
            }

            if (Visible)
            {
                var mouse = Input.mousePosition;
                var point = new Vector2(mouse.x, Screen.height - mouse.y) / _scale;
                MouseOver = _window.Contains(point);
            }
            else
            {
                MouseOver = false;
                SetKeyBlock(false);
            }
        }

        public void OnGUI()
        {
            long perf = PerfStats.Begin();
            DrawGui();
            PerfStats.End("overlay.gui", perf);
        }

        private void DrawGui()
        {
            var e = Event.current;
            if (e.type == EventType.KeyUp && e.keyCode == _settings.ToggleKey.Value)
            {
                Visible = !Visible;
                if (!Visible)
                {
                    MouseOver = false;
                    SetKeyBlock(false);
                }
                e.Use();
            }

            _scale = Mathf.Max(1f, Screen.height / 1080f);

            if (!Visible)
            {
                // The collapsed line is plain labels, which only do anything while repainting.
                // OnGUI also runs for the layout pass and for every input event.
                if (e.type != EventType.Repaint) return;
                if (_session.State == SessionState.Offline && !_hasPendingInvite) return;
                GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
                DrawCollapsed();
                return;
            }
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));

            // Enter in the chat box sends. Checked before the text field sees the key.
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "coop.chat")
            {
                SendChat();
                e.Use();
            }

            _window.height = 0; // let the layout pick the height
            KeyCode toggle = _settings.ToggleKey.Value;
            if (_windowTitle == null || toggle != _windowTitleKey)
            {
                _windowTitleKey = toggle;
                _windowTitle = "Co-op  (" + toggle + " hides)";
            }
            _window = GUILayout.Window(WindowId, _window, DrawWindow, _windowTitle);

            if (e.type == EventType.Repaint)
                SetKeyBlock(System.Array.IndexOf(TextFields, GUI.GetNameOfFocusedControl()) >= 0);
        }

        public void Close()
        {
            if (_steam != null) _steam.LobbyInviteReceived -= OnLobbyInviteReceived;
            _steamActions.Clear();
            SetKeyBlock(false);
            MouseOver = false;
        }

        private void DrawWindow(int id)
        {
            DrawPendingInvite();

            if (_session.State == SessionState.Offline)
            {
                DrawTransportChoice();
                if (_steamMode) DrawSteamConnectForm();
                else DrawDirectConnectForm();
            }
            else
            {
                bool steamSession = _inSteamLobby || _session.TransportDisplayName != "Direct IP";
                if (steamSession)
                {
                    DrawSteamStatus();
                    DrawFriends(_session.State == SessionState.Hosting);
                }
                switch (_session.State)
                {
                    case SessionState.Connecting:
                        GUILayout.Label("Connecting…");
                        if (GUILayout.Button("Cancel")) LeaveSession();
                        break;
                    default:
                        DrawPlayers();
                        break;
                }
            }

            if (!string.IsNullOrEmpty(_steamUiError))
            {
                GUILayout.Space(4);
                GUILayout.Label(_steamUiError, WrappedLabel);
            }

            GUILayout.Space(6);
            _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUI.skin.box, GUILayout.Height(170));
            foreach (string line in _session.Lines) GUILayout.Label(line);
            GUILayout.EndScrollView();

            if (_session.State == SessionState.Hosting || _session.State == SessionState.Joined)
            {
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName("coop.chat");
                _chatInput = GUILayout.TextField(_chatInput, Protocol.MaxChatLength);
                if (GUILayout.Button("Send", GUILayout.Width(60))) SendChat();
                GUILayout.EndHorizontal();
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void DrawTransportChoice()
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = !_steamMode && _steamAvailable;
            if (GUILayout.Button("Steam"))
            {
                _steamMode = true;
                _settings.PreferredTransport.Value = TransportPreference.Steam;
                _steamUiError = "";
            }
            GUI.enabled = _steamMode;
            if (GUILayout.Button(_steamAvailable ? "Direct IP" : "Direct IP (Steam unavailable)"))
            {
                if (_inSteamLobby || _steamBusy) QueueSteamAction(SteamActionKind.LeaveSession);
                _steamMode = false;
                _settings.PreferredTransport.Value = TransportPreference.DirectIP;
                _steamUiError = "";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!_steamAvailable)
            {
                GUILayout.Label("Steam is unavailable, so Direct IP is the fallback. Start Steam and restart the game to host or join Steam lobbies.",
                    WrappedLabel);
            }
            else if (_steamMode)
            {
                GUILayout.Label("Steam lobbies use reliable P2P; no IP address or port forwarding is needed.",
                    WrappedLabel);
            }
            else
            {
                GUILayout.Label("Direct IP keeps the original TCP connection flow.", WrappedLabel);
            }
        }

        private void DrawSteamConnectForm()
        {
            _name = LabelledField("Your name", "coop.name", _name, Protocol.MaxNameLength);
            DrawVisibilityChoice();
            GUI.enabled = _steamAvailable && !_steamBusy && !_inSteamLobby;
            if (GUILayout.Button("Host Steam lobby")) QueueSteamAction(SteamActionKind.Host);
            GUI.enabled = true;
            DrawSteamStatus();
            DrawFriends(false);
        }

        private void DrawDirectConnectForm()
        {
            _name = LabelledField("Your name", "coop.name", _name, Protocol.MaxNameLength);
            _address = LabelledField("Host address", "coop.address", _address, 64);
            _port = LabelledField("Port", "coop.port", _port, 5);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host a game") && ReadForm(out int hostPort))
            {
                _session.UseDirectTransport();
                _session.Host(hostPort, _name);
            }
            if (GUILayout.Button("Join") && ReadForm(out int joinPort))
            {
                _session.UseDirectTransport();
                _session.Join(_address, joinPort, _name);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawVisibilityChoice()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lobby visibility", GUILayout.Width(105));
            GUI.enabled = !_steamBusy && !_inSteamLobby && _settings.Visibility.Value != HostVisibility.FriendsOnly;
            if (GUILayout.Button("Friends only")) _settings.Visibility.Value = HostVisibility.FriendsOnly;
            GUI.enabled = !_steamBusy && !_inSteamLobby && _settings.Visibility.Value != HostVisibility.InviteOnly;
            if (GUILayout.Button("Invite only")) _settings.Visibility.Value = HostVisibility.InviteOnly;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(_settings.Visibility.Value == HostVisibility.FriendsOnly
                ? "Friends can discover this lobby; use invites for a direct join."
                : "Only players you invite can discover and join this lobby.", WrappedLabel);
        }

        private void DrawSteamStatus()
        {
            GUILayout.Space(3);
            GUILayout.Label("Steam lobby", BoldLabel);
            if (!string.IsNullOrEmpty(_steamStatus)) GUILayout.Label(_steamStatus, WrappedLabel);
            if (_inSteamLobby && _session.State == SessionState.Hosting)
            {
                GUILayout.Label("Visibility: " + (_steamVisibility == SteamLobbyVisibility.FriendsOnly ? "Friends only" : "Invite only"));
                if (GUILayout.Button("Open native Steam invite dialog")) QueueSteamAction(SteamActionKind.OpenInviteDialog);
                if (!string.IsNullOrEmpty(_nativeInviteStatus)) GUILayout.Label(_nativeInviteStatus, WrappedLabel);
            }
            else if ((_inSteamLobby || _steamBusy) && _session.State == SessionState.Offline)
            {
                if (GUILayout.Button("Cancel / close Steam lobby")) QueueSteamAction(SteamActionKind.LeaveSession);
            }

            if (_members.Count == 0)
            {
                if (!_inSteamLobby && !_steamBusy) GUILayout.Label("No active lobby. Host one or accept an invitation.");
                return;
            }

            GUILayout.Label("Lobby members and connection state:");
            foreach (var member in _members)
            {
                string role = member.IsHost ? " (host)" : member.IsLocal ? " (you)" : "";
                GUILayout.Label("  • " + member.Name + role + " — " + MemberConnectionStatus(member), WrappedLabel);
            }
        }

        private string MemberConnectionStatus(MemberRow member)
        {
            PlayerInfo player = null;
            if (member.IsLocal) player = PlayerById(_session.LocalId);
            else if (member.IsHost) player = PlayerById(Session.HostId);
            if (player == null && _session.Players.Count == 2)
            {
                foreach (var candidate in _session.Players)
                    if (candidate.Id != _session.LocalId && candidate.Id != Session.HostId) player = candidate;
            }
            if (player == null)
            {
                foreach (var candidate in _session.Players)
                    if (candidate.Name == member.Name) { player = candidate; break; }
            }
            if (player == null) return "in lobby; handshake pending";
            if (_session.State == SessionState.Hosting)
                return _session.IsPeerReady(player.Id) ? "handshake complete; world ready" : "handshake complete; loading world";
            if (player.Id == _session.LocalId)
                return _session.IsWorldReady ? "handshake complete; world ready" : "handshake complete; loading world";
            return "handshake complete";
        }

        /// <summary>Drawn for every lobby member on every GUI event, so no delegate is built for the search.</summary>
        private PlayerInfo PlayerById(int id)
        {
            foreach (var candidate in _session.Players)
                if (candidate.Id == id) return candidate;
            return null;
        }

        private void DrawFriends(bool canInvite)
        {
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Online Steam friends", BoldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(75))) QueueSteamAction(SteamActionKind.RefreshFriends);
            GUILayout.EndHorizontal();

            if (_friends.Count == 0)
            {
                GUILayout.Label("No online friends found. Check Steam's online status, then Refresh.", WrappedLabel);
                return;
            }

            _friendsScroll = GUILayout.BeginScrollView(_friendsScroll, GUI.skin.box, GUILayout.Height(Mathf.Min(150, 30 + _friends.Count * 27)));
            foreach (var friend in _friends)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(friend.Name, GUILayout.Width(235));
                GUI.enabled = canInvite;
                if (GUILayout.Button("Invite", GUILayout.Width(65))
                    && QueueSteamAction(SteamActionKind.InviteFriend, friend.Id))
                    _friendInviteStatus[friend.Id.m_SteamID] = "Invite queued";
                GUI.enabled = true;
                string status;
                if (_friendInviteStatus.TryGetValue(friend.Id.m_SteamID, out status))
                    GUILayout.Label(status, WrappedLabel);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (!canInvite) GUILayout.Label("Host a Steam lobby to enable per-friend invites.", WrappedLabel);
        }

        private void DrawPendingInvite()
        {
            if (!_hasPendingInvite) return;
            GUILayout.Label("Steam co-op invitation", BoldLabel);
            GUILayout.Label((_pendingInviter.Length > 0 ? _pendingInviter : "A Steam friend") + " invited you to join their lobby.",
                WrappedLabel);
            if (_pendingLobby.Length > 0) GUILayout.Label("Lobby: " + _pendingLobby);
            if (_session.State != SessionState.Offline || _inSteamLobby || _steamBusy)
                GUILayout.Label("Warning: joining replaces your current co-op session and Steam lobby.", WrappedLabel);

            GUILayout.BeginHorizontal();
            GUI.enabled = _steamAvailable;
            if (GUILayout.Button("Join")) QueueSteamAction(SteamActionKind.JoinPendingInvite);
            GUI.enabled = true;
            if (GUILayout.Button("Dismiss")) QueueSteamAction(SteamActionKind.DismissPendingInvite);
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
        }

        private void DrawPlayers()
        {
            GUILayout.BeginHorizontal();
            bool direct = _session.TransportDisplayName == "Direct IP";
            string status;
            if (_session.State == SessionState.Hosting)
                status = direct && _session.Port > 0
                    ? "Hosting on port " + _session.Port
                    : "Hosting via " + _session.TransportDisplayName;
            else
                status = direct ? "Connected to host" : "Connected via " + _session.TransportDisplayName;
            GUILayout.Label(status);
            if (GUILayout.Button(_session.State == SessionState.Hosting ? "Stop hosting" : "Leave", GUILayout.Width(110)))
                LeaveSession();
            GUILayout.EndHorizontal();
            if (!direct && !string.IsNullOrWhiteSpace(_session.TransportStatus))
                GUILayout.Label(_session.TransportStatus, WrappedLabel);

            foreach (var player in _session.Players)
            {
                string tags = (player.IsHost ? " (host)" : "") + (player.Id == _session.LocalId ? " (you)" : "");
                GUILayout.Label("  • " + player.Name + tags);
            }
            DrawParty();
        }

        /// <summary>Who controls each party member. The host changes it by clicking the name.</summary>
        private void DrawParty()
        {
            if (!GameState.IsInGameSession || GameState.IsLoading) return;
            bool any = false;
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
            {
                if (member == null || member.Summoner != null || member.Slot >= 6) continue;
                if (!any) { GUILayout.Space(4); GUILayout.Label("Party"); any = true; }
                int owner = _ownership.OwnerOf(member);
                GUILayout.BeginHorizontal();
                GUILayout.Label("  " + CharacterStats.Name(member));
                if (_session.State == SessionState.Hosting)
                {
                    if (GUILayout.Button(_session.NameOf(owner), GUILayout.Width(130)))
                    {
                        int next = NextPlayer(owner);
                        if (_session.TransportDisplayName == "Direct IP") _ownership.Assign(member, next);
                        else
                        {
                            var action = new SteamAction { Kind = SteamActionKind.AssignParty, Member = member, Owner = next };
                            QueueSteamAction(action);
                        }
                    }
                }
                else
                {
                    GUILayout.Label(_session.NameOf(owner), GUILayout.Width(130));
                }
                GUILayout.EndHorizontal();
            }
        }

        private int NextPlayer(int current)
        {
            var players = _session.Players;
            int index = players.FindIndex(p => p.Id == current);
            return players[(index + 1) % players.Count].Id;
        }

        /// <summary>While the window is hidden: a one-line status, plus new chat lines for a few seconds.</summary>
        private void DrawCollapsed()
        {
            if (_session.State == SessionState.Offline)
            {
                if (_hasPendingInvite) GUI.Label(new Rect(12, 8, 600, 22), CollapsedText(-2));
                return;
            }
            GUI.Label(new Rect(12, 8, 500, 22),
                CollapsedText(_session.State == SessionState.Connecting ? -1 : _session.Players.Count));

            if (Time.unscaledTime - _lastLineTime > RecentLineSeconds) return;
            int first = Mathf.Max(0, _session.Lines.Count - 3);
            for (int i = first; i < _session.Lines.Count; i++)
                GUI.Label(new Rect(12, 28 + (i - first) * 20, 700, 22), _session.Lines[i]);
        }

        /// <summary>
        /// The status line for a player count, -1 while connecting or -2 for a pending
        /// invitation. It is drawn every frame of normal play, so the text is kept
        /// until what it says changes.
        /// </summary>
        private string CollapsedText(int kind)
        {
            KeyCode toggle = _settings.ToggleKey.Value;
            if (_collapsedText != null && kind == _collapsedKind && toggle == _collapsedKey) return _collapsedText;
            _collapsedKind = kind;
            _collapsedKey = toggle;
            string status = kind == -2 ? "Co-op: Steam invitation pending"
                : kind == -1 ? "Co-op: connecting…"
                : "Co-op: " + kind + (kind == 1 ? " player" : " players");
            _collapsedText = status + "  [" + toggle + "]";
            return _collapsedText;
        }

        private bool ReadForm(out int port)
        {
            if (!int.TryParse(_port, out port) || port < 1 || port > 65535)
            {
                _port = Protocol.DefaultPort.ToString();
                port = Protocol.DefaultPort;
            }
            _settings.Name.Value = Protocol.CleanName(_name);
            _settings.Address.Value = (_address ?? "").Trim();
            _settings.Port.Value = port;
            return true;
        }

        private void SaveName()
        {
            _name = Protocol.CleanName(_name);
            _settings.Name.Value = _name;
        }

        private void SendChat()
        {
            if (_session.TransportDisplayName != "Direct IP")
            {
                if (QueueSteamAction(new SteamAction { Kind = SteamActionKind.SendChat, Text = _chatInput })) _chatInput = "";
                return;
            }
            _session.SendChat(_chatInput);
            _chatInput = "";
        }

        private void LeaveSession()
        {
            if (_session.TransportDisplayName == "Direct IP" && !_inSteamLobby && !_steamBusy) _session.Leave();
            else QueueSteamAction(new SteamAction { Kind = SteamActionKind.LeaveSession });
        }

        private bool QueueSteamAction(SteamActionKind kind, CSteamID friend = default(CSteamID))
            => QueueSteamAction(new SteamAction { Kind = kind, Friend = friend });

        private bool QueueSteamAction(SteamAction action)
        {
            action.Generation = _session.Generation;
            foreach (var pending in _steamActions)
            {
                if (pending.Kind != action.Kind || !pending.Friend.Equals(action.Friend)) continue;
                if (action.Kind != SteamActionKind.SendChat && action.Kind != SteamActionKind.AssignParty) return false;
            }
            if (_steamActions.Count >= 32)
            {
                _steamUiError = "Too many Steam actions are queued. Wait a moment, then try again.";
                return false;
            }
            _steamActions.Enqueue(action);
            return true;
        }

        private void RunSteamActions()
        {
            if (_steam == null)
            {
                _steamActions.Clear();
                return;
            }

            while (_steamActions.Count > 0)
            {
                var action = _steamActions.Dequeue();
                try
                {
                    _steamUiError = "";
                    switch (action.Kind)
                    {
                        case SteamActionKind.Host:
                            SaveName();
                            if (!_steam.Host(_settings.Visibility.Value == HostVisibility.FriendsOnly
                                ? SteamLobbyVisibility.FriendsOnly : SteamLobbyVisibility.Private))
                                _steamUiError = "Steam could not start the lobby. Leave any current lobby and check Steam, then try again.";
                            break;
                        case SteamActionKind.RefreshFriends:
                            _steam.RefreshFriends();
                            break;
                        case SteamActionKind.OpenInviteDialog:
                            _nativeInviteStatus = _steam.ActivateGameOverlayInviteDialog()
                                ? "Steam invite dialog opened. Select friends in the Steam overlay."
                                : "Steam could not open the invite dialog. Check the lobby status and Steam overlay, then try again.";
                            break;
                        case SteamActionKind.InviteFriend:
                            bool invited = _steam.InviteUserToLobby(action.Friend);
                            _friendInviteStatus[action.Friend.m_SteamID] = invited
                                ? "Invited" : "Invite failed; check Steam and try again";
                            break;
                        case SteamActionKind.JoinPendingInvite:
                            JoinPendingInvite();
                            break;
                        case SteamActionKind.DismissPendingInvite:
                            ClearPendingInvite();
                            break;
                        case SteamActionKind.LeaveSession:
                            if (action.Generation == _session.Generation)
                            {
                                _steam.Cancel();
                                if (_session.State != SessionState.Offline) _session.Leave();
                            }
                            break;
                        case SteamActionKind.SendChat:
                            if (action.Generation == _session.Generation) _session.SendChat(action.Text);
                            break;
                        case SteamActionKind.AssignParty:
                            if (action.Generation == _session.Generation && action.Member != null)
                                _ownership.Assign(action.Member, action.Owner);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _steamUiError = "Steam action failed: " + ex.Message + " Check that Steam is running and you are online, then try again.";
                    if (action.Kind == SteamActionKind.InviteFriend)
                        _friendInviteStatus[action.Friend.m_SteamID] = "Invite failed; try again";
                }
            }
        }

        private void OnLobbyInviteReceived(CSteamID lobby, CSteamID inviter)
        {
            if (lobby == CSteamID.Nil) return;
            _pendingInviteLobby = lobby;
            _pendingInviterId = inviter;
            _pendingInviter = inviter == CSteamID.Nil ? "" : inviter.ToString();
            _pendingLobby = lobby.ToString();
            _hasPendingInvite = true;
            Visible = true;
        }

        private void JoinPendingInvite()
        {
            if (!_hasPendingInvite || _pendingInviteLobby == CSteamID.Nil) return;
            SaveName();
            CSteamID lobby = _pendingInviteLobby;
            if (_steam.CurrentLobby == lobby)
            {
                ClearPendingInvite();
                _steamMode = true;
                _settings.PreferredTransport.Value = TransportPreference.Steam;
                return;
            }
            if (_session.State != SessionState.Offline || _steam.CurrentLobby != CSteamID.Nil)
            {
                _steam.Cancel();
                if (_session.State != SessionState.Offline) _session.Leave();
            }
            if (_steam.Join(lobby))
            {
                ClearPendingInvite();
                _steamMode = true;
                _settings.PreferredTransport.Value = TransportPreference.Steam;
            }
            else
            {
                _steamUiError = "Steam could not join this invitation. Check that the host is still online, then ask for a new invite if the lobby has closed.";
            }
        }

        private void ClearPendingInvite()
        {
            _hasPendingInvite = false;
            _pendingInviteLobby = CSteamID.Nil;
            _pendingInviterId = CSteamID.Nil;
            _pendingInviter = "";
            _pendingLobby = "";
        }

        private void RefreshSteamSnapshot()
        {
            // The friend and member rows are only drawn in the window. While it is hidden
            // they keep their last contents instead of being rebuilt from Steam every frame.
            bool rows = Visible;
            if (rows || _steam == null)
            {
                _friends.Clear();
                _members.Clear();
            }
            if (_steam == null)
            {
                _steamAvailable = false;
                _inSteamLobby = false;
                _steamBusy = false;
                _hasPendingInvite = false;
                _steamMode = false;
                return;
            }

            try
            {
                _steamAvailable = _steam.IsAvailable;
                if (!_steamAvailable && _session.State == SessionState.Offline) _steamMode = false;
                else if (_steamAvailable && _settings.PreferredTransport.Value == TransportPreference.Steam
                    && _session.State == SessionState.Offline && !_inSteamLobby) _steamMode = true;

                _steamStatus = _steam.StatusText ?? "";
                _inSteamLobby = _steam.CurrentLobby != CSteamID.Nil;
                _steamBusy = _steam.IsBusy;
                if (_hasPendingInvite && _steam.CurrentLobby == _pendingInviteLobby) ClearPendingInvite();
                _steamVisibility = _steam.Visibility ?? SteamLobbyVisibility.FriendsOnly;
                if (rows)
                {
                    var friends = _steam.OnlineFriends;
                    for (int i = 0; i < friends.Count; i++)
                        _friends.Add(new FriendRow { Id = friends[i].SteamId, Name = friends[i].PersonaName });
                }

                if (rows && _inSteamLobby && _steamAvailable)
                {
                    CSteamID local = SteamUser.GetSteamID();
                    int count = SteamMatchmaking.GetNumLobbyMembers(_steam.CurrentLobby);
                    for (int i = 0; i < count; i++)
                    {
                        CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(_steam.CurrentLobby, i);
                        string name = Protocol.Clean(SteamFriends.GetFriendPersonaName(id), 128);
                        if (name.Length == 0) name = id.ToString();
                        _members.Add(new MemberRow
                        {
                            Id = id,
                            Name = name,
                            IsHost = id == _steam.LobbyOwner,
                            IsLocal = id == local,
                        });
                    }
                }

                if (_hasPendingInvite && _pendingInviterId != CSteamID.Nil && _steamAvailable)
                {
                    string inviter = Protocol.Clean(SteamFriends.GetFriendPersonaName(_pendingInviterId), 128);
                    if (inviter.Length > 0) _pendingInviter = inviter;
                }
            }
            catch (Exception ex)
            {
                _friends.Clear();
                _members.Clear();
                _steamAvailable = false;
                _inSteamLobby = false;
                _steamBusy = false;
                if (_session.State == SessionState.Offline) _steamMode = false;
                _steamUiError = "Steam status is unavailable: " + ex.Message + " Use Direct IP or restart Steam and the game.";
            }
        }

        private GUIStyle WrappedLabel => _wrappedLabel ?? (_wrappedLabel = new GUIStyle(GUI.skin.label) { wordWrap = true });
        private GUIStyle BoldLabel => _boldLabel ?? (_boldLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });

        private static string LabelledField(string label, string controlName, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100));
            GUI.SetNextControlName(controlName);
            value = GUILayout.TextField(value ?? "", maxLength);
            GUILayout.EndHorizontal();
            return value;
        }

        /// <summary>Stops typing from firing game hotkeys, the same way the game's own console does.</summary>
        private void SetKeyBlock(bool block)
        {
            if (block == _blockingKeys) return;
            _blockingKeys = block;
            if (block) GameInput.BeginBlockAllKeys();
            else GameInput.EndBlockAllKeys();
        }
    }
}
