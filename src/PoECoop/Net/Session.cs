using System;
using System.Collections.Generic;

namespace PoECoop.Net
{
    public enum SessionState { Offline, Hosting, Connecting, Joined }

    public sealed class PlayerInfo
    {
        public int Id;
        public string Name;
        public bool IsHost => Id == Session.HostId;
    }

    /// <summary>
    /// Who is in the game and what was said. The host holds the truth and relays;
    /// guests only talk to the host. No Unity types in here.
    /// </summary>
    public sealed class Session : IDisposable
    {
        public const int HostId = 0;

        private const float HelloTimeoutSeconds = 5f;

        private readonly string _modVersion;
        private readonly string _gameVersion;
        private readonly Func<ITransport> _createDirectTransport;
        private Func<ITransport> _createTransport;
        private string _selectedTransportDisplayName;
        private readonly Action<string> _log;

        private ITransport _transport;
        // Host only: connection id -> player, and connections that have not said Hello yet.
        private readonly Dictionary<int, PlayerInfo> _byConnection = new Dictionary<int, PlayerInfo>();
        private readonly Dictionary<int, float> _awaitingHello = new Dictionary<int, float>();
        private readonly HashSet<int> _readyPeers = new HashSet<int>();
        private bool _localWorldReady;
        private int _nextPlayerId = 1;

        public SessionState State { get; private set; }
        /// <summary>Changes whenever the local session is replaced or reset.</summary>
        public long Generation { get; private set; }
        public int LocalId { get; private set; }
        public string LocalName { get; private set; }
        public int Port { get; private set; }
        /// <summary>Selected transport name, suitable for display before or during a session.</summary>
        public string TransportDisplayName => _transport != null ? _transport.DisplayName : _selectedTransportDisplayName;
        /// <summary>Selected transport's neutral listener or connection description.</summary>
        public string TransportStatus => _transport != null ? _transport.Status : _selectedTransportDisplayName;
        /// <summary>Everyone in the session including this player, host first.</summary>
        public List<PlayerInfo> Players { get; } = new List<PlayerInfo>();
        /// <summary>Chat and status lines, oldest first.</summary>
        public List<string> Lines { get; } = new List<string>();
        /// <summary>Goes up by one whenever Lines changes.</summary>
        public int LinesVersion { get; private set; }

        /// <summary>Raised on this machine after a player other than the local one joins. Guests also get it for players already present.</summary>
        public event Action<PlayerInfo> PlayerJoined;

        /// <summary>Raised on the host when a guest leaves.</summary>
        public event Action<PlayerInfo> PlayerLeft;

        /// <summary>
        /// A message of type FirstGameMessage or above arrived. The player is the
        /// sender on the host, and null on a guest (it came from the host).
        /// A handler that throws drops the sender, like any malformed message.
        /// </summary>
        public event Action<PlayerInfo, MessageReader> GameMessage;

        /// <summary>Raised after a guest loads a world, and after the host sends its full-state marker.</summary>
        public event Action<PlayerInfo, long> WorldStateReady;

        /// <summary>Host-only refresh request. Player id -1 means every ready guest.</summary>
        public event Action<int, long> FullStateRequested;

        /// <summary>Raised after transport and peer state have been cleared.</summary>
        public event Action SessionReset;

        public bool IsHost => State == SessionState.Hosting;
        public bool IsGuest => State == SessionState.Joined;
        public bool IsWorldReady => IsHost || (IsGuest && _localWorldReady);

        public bool IsPeerReady(int playerId)
        {
            if (playerId == HostId) return IsWorldReady;
            return IsHost && _readyPeers.Contains(playerId);
        }

        public Session(string modVersion, string gameVersion, Func<ITransport> createTransport, Action<string> log)
        {
            _modVersion = modVersion;
            _gameVersion = gameVersion;
            _createDirectTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
            _createTransport = createTransport;
            _selectedTransportDisplayName = "Direct IP";
            _log = log;
        }

        /// <summary>Selects the transport used by subsequent Host and Join calls. Main thread, offline only.</summary>
        public void SetTransportFactory(string displayName, Func<ITransport> createTransport)
        {
            EnsureTransportSelectionAllowed();
            _createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
            _selectedTransportDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Network" : displayName.Trim();
        }

        /// <summary>Restores the direct-IP factory supplied to the constructor. Main thread, offline only.</summary>
        public void UseDirectTransport()
        {
            EnsureTransportSelectionAllowed();
            _createTransport = _createDirectTransport;
            _selectedTransportDisplayName = "Direct IP";
        }

        private void EnsureTransportSelectionAllowed()
        {
            if (State != SessionState.Offline || _transport != null)
                throw new InvalidOperationException("Leave the current session before changing transport");
        }

        public void Host(int port, string name)
        {
            if (State != SessionState.Offline || _transport != null) Leave();
            ITransport transport = null;
            try
            {
                transport = _createTransport() ?? throw new InvalidOperationException("transport factory returned null");
                transport.Host(port);
            }
            catch (Exception ex)
            {
                DisposeFailedTransport(transport);
                Status("Could not host via " + (transport != null ? transport.DisplayName : _selectedTransportDisplayName) + ": " + ex.Message);
                return;
            }
            _transport = transport;
            Port = port;
            LocalId = HostId;
            LocalName = Protocol.CleanName(name);
            Players.Add(new PlayerInfo { Id = HostId, Name = LocalName });
            State = SessionState.Hosting;
            Status((string.IsNullOrWhiteSpace(transport.Status) ? "Hosting via " + transport.DisplayName : transport.Status) + " as " + LocalName);
        }

        public void Join(string address, int port, string name)
        {
            if (State != SessionState.Offline || _transport != null) Leave();
            address = (address ?? "").Trim();
            if (address.Length == 0) { Status("Enter the host address first"); return; }

            ITransport transport;
            try
            {
                transport = _createTransport() ?? throw new InvalidOperationException("transport factory returned null");
            }
            catch (Exception ex)
            {
                Status("Could not connect via " + _selectedTransportDisplayName + ": " + ex.Message);
                return;
            }

            _transport = transport;
            Port = port;
            LocalName = Protocol.CleanName(name);
            State = SessionState.Connecting;
            try
            {
                transport.Connect(address, port);
            }
            catch (Exception ex)
            {
                Reset();
                Status("Could not connect via " + _selectedTransportDisplayName + ": " + ex.Message);
                return;
            }
            Status(string.IsNullOrWhiteSpace(transport.Status) ? "Connecting via " + transport.DisplayName : transport.Status);
        }

        public void Leave()
        {
            bool wasActive = State == SessionState.Hosting || State == SessionState.Joined;
            Reset();
            if (wasActive) Status("Left the session");
        }

        public void SendChat(string text)
        {
            text = Protocol.Clean(text, Protocol.MaxChatLength);
            if (text.Length == 0) return;
            if (State == SessionState.Hosting)
                Broadcast(HostId, text);
            else if (State == SessionState.Joined)
                _transport.Send(0, new MessageWriter(MessageType.Chat).String(text).ToArray());
        }

        /// <summary>Guest only. Gameplay is held until the correlated world state is ready.</summary>
        public void SendToHost(byte[] message)
        {
            if (State != SessionState.Joined) return;
            MessageType type = SentType(message);
            if (!_localWorldReady && type >= MessageType.FirstGameMessage
                && type != MessageType.SaveLoaded && type != MessageType.WorldReady) return;
            _transport.Send(0, message);
        }

        /// <summary>Host only. Handoff messages may cross before this peer is ready; gameplay may not.</summary>
        public void SendToPlayer(int playerId, byte[] message)
        {
            if (State != SessionState.Hosting) return;
            MessageType type = SentType(message);
            if (type >= MessageType.FirstGameMessage && !_readyPeers.Contains(playerId)
                && type != MessageType.SaveBegin && type != MessageType.SaveChunk && type != MessageType.WorldState) return;
            foreach (var pair in _byConnection)
                if (pair.Value.Id == playerId) { _transport.Send(pair.Key, message); return; }
        }

        /// <summary>Adds a line to the chat log on this machine only.</summary>
        public void Note(string text) => Status(text);

        public string NameOf(int id)
        {
            var player = Players.Find(p => p.Id == id);
            return player != null ? player.Name : "Player " + id;
        }

        /// <summary>Call once per frame from the main thread.</summary>
        public void Update(float now)
        {
            if (_transport == null) return;
            // The transport can be replaced or dropped while handling an event.
            var transport = _transport;
            while (transport == _transport && transport.Poll(out var e))
            {
                try
                {
                    if (State == SessionState.Hosting) HostHandle(e, now);
                    else GuestHandle(e);
                }
                catch (Exception ex)
                {
                    // Malformed data from the other side: drop that connection, keep running.
                    string messageType = e.Data != null && e.Data.Length > 0
                        ? ((MessageType)e.Data[0]).ToString() : e.Type.ToString();
                    _log("Bad message on connection " + e.Connection + " (" + messageType + "): " + ex);
                    if (State == SessionState.Hosting) transport.Disconnect(e.Connection);
                    else { Reset(); Status("Disconnected: bad data from host"); }
                }
            }
            if (State == SessionState.Hosting && _awaitingHello.Count > 0) DropSilentConnections(now);
        }

        public void Dispose() => Reset();

        // ---- host ----

        private void HostHandle(TransportEvent e, float now)
        {
            switch (e.Type)
            {
                case TransportEventType.Connected:
                    _awaitingHello[e.Connection] = now + HelloTimeoutSeconds;
                    break;

                case TransportEventType.Disconnected:
                    if (e.Connection == HostId)
                    {
                        string reason = string.IsNullOrWhiteSpace(e.Text) ? "listener startup failed" : e.Text;
                        Reset();
                        Status("Could not host via " + _selectedTransportDisplayName + ": " + reason);
                        break;
                    }
                    _awaitingHello.Remove(e.Connection);
                    if (_byConnection.TryGetValue(e.Connection, out var gone))
                    {
                        _byConnection.Remove(e.Connection);
                        _readyPeers.Remove(gone.Id);
                        Players.Remove(gone);
                        SendToGuests(new MessageWriter(MessageType.PlayerLeft).Int(gone.Id).ToArray());
                        Status(gone.Name + " left (" + e.Text + ")");
                        PlayerLeft?.Invoke(gone);
                    }
                    break;

                case TransportEventType.Data:
                    var reader = new MessageReader(e.Data);
                    if (_byConnection.TryGetValue(e.Connection, out var sender))
                    {
                        if (reader.Type >= MessageType.FirstGameMessage)
                        {
                            if (!_readyPeers.Contains(sender.Id)
                                && reader.Type != MessageType.SaveLoaded && reader.Type != MessageType.WorldReady) break;
                            GameMessage?.Invoke(sender, reader);
                            break;
                        }
                        if (reader.Type != MessageType.Chat) throw new FormatException("unexpected " + reader.Type);
                        string text = Protocol.Clean(reader.String(), Protocol.MaxChatLength);
                        if (text.Length > 0) Broadcast(sender.Id, text);
                    }
                    else if (_awaitingHello.Remove(e.Connection))
                    {
                        if (reader.Type != MessageType.Hello) throw new FormatException("expected Hello, got " + reader.Type);
                        HostHandleHello(e.Connection, reader, now);
                    }
                    break;
            }
        }

        private void HostHandleHello(int connection, MessageReader reader, float now)
        {
            int protocol = reader.Int();
            string modVersion = Protocol.Clean(reader.String(), 32);
            string gameVersion = Protocol.Clean(reader.String(), 64);
            string name = Protocol.CleanName(reader.String());

            string refusal = null;
            if (protocol != Protocol.Version || modVersion != _modVersion)
                refusal = "mod version differs: host has " + _modVersion + ", you have " + modVersion;
            else if (gameVersion != _gameVersion)
                refusal = "game version differs: host has " + _gameVersion + ", you have " + gameVersion;
            if (refusal != null)
            {
                _transport.Send(connection, new MessageWriter(MessageType.Reject).String(refusal).ToArray());
                // Not disconnected right away, so the reason has time to go out. The guest
                // drops the link when it reads it; if it does not, this deadline closes it.
                _awaitingHello[connection] = now + 2f;
                Status("Refused " + name + ": " + refusal);
                return;
            }

            var player = new PlayerInfo { Id = _nextPlayerId++, Name = UniqueName(name) };
            var welcome = new MessageWriter(MessageType.Welcome).Int(player.Id).Int(Players.Count + 1);
            foreach (var p in Players) welcome.Int(p.Id).String(p.Name);
            welcome.Int(player.Id).String(player.Name);

            SendToGuests(new MessageWriter(MessageType.PlayerJoined).Int(player.Id).String(player.Name).ToArray());
            _byConnection[connection] = player;
            Players.Add(player);
            _transport.Send(connection, welcome.ToArray());
            Status(player.Name + " joined from " + _transport.Describe(connection));
            PlayerJoined?.Invoke(player);
        }

        private void DropSilentConnections(float now)
        {
            List<int> expired = null;
            foreach (var pair in _awaitingHello)
                if (now >= pair.Value) (expired ?? (expired = new List<int>())).Add(pair.Key);
            if (expired == null) return;
            foreach (int connection in expired)
            {
                _awaitingHello.Remove(connection);
                _transport.Disconnect(connection);
            }
        }

        private void Broadcast(int senderId, string text)
        {
            SendToGuests(new MessageWriter(MessageType.Chat).Int(senderId).String(text).ToArray());
            AddLine(NameOf(senderId) + ": " + text);
        }

        /// <summary>Host only (guests have no one to send to here).</summary>
        public void SendToGuests(byte[] message)
        {
            if (State != SessionState.Hosting) return;
            MessageType type = SentType(message);
            foreach (var pair in _byConnection)
            {
                if (type >= MessageType.FirstGameMessage && !_readyPeers.Contains(pair.Value.Id)
                    && type != MessageType.SaveBegin && type != MessageType.SaveChunk && type != MessageType.WorldState) continue;
                _transport.Send(pair.Key, message);
            }
        }

        private string UniqueName(string name)
        {
            string candidate = name;
            for (int n = 2; Players.Exists(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
                candidate = name + " (" + n + ")";
            return candidate;
        }

        // ---- guest ----

        private void GuestHandle(TransportEvent e)
        {
            switch (e.Type)
            {
                case TransportEventType.Connected:
                    _transport.Send(0, new MessageWriter(MessageType.Hello)
                        .Int(Protocol.Version).String(_modVersion).String(_gameVersion).String(LocalName).ToArray());
                    break;

                case TransportEventType.Disconnected:
                    bool wasJoined = State == SessionState.Joined;
                    Reset();
                    Status((wasJoined ? "Lost connection to host: " : "Could not connect: ") + e.Text);
                    break;

                case TransportEventType.Data:
                    GuestHandleMessage(new MessageReader(e.Data));
                    break;
            }
        }

        private void GuestHandleMessage(MessageReader reader)
        {
            switch (reader.Type)
            {
                case MessageType.Welcome:
                {
                    LocalId = reader.Int();
                    int count = reader.Int();
                    if (count < 1 || count > 64) throw new FormatException("player count " + count);
                    Players.Clear();
                    for (int i = 0; i < count; i++)
                        Players.Add(new PlayerInfo { Id = reader.Int(), Name = Protocol.CleanName(reader.String()) });
                    LocalName = NameOf(LocalId);
                    State = SessionState.Joined;
                    _localWorldReady = false;
                    Status("Joined " + NameOf(HostId) + "'s game as " + LocalName);
                    foreach (var p in Players.ToArray())
                        if (p.Id != LocalId) PlayerJoined?.Invoke(p);
                    break;
                }
                case MessageType.Reject:
                {
                    string reason = Protocol.Clean(reader.String(), 200);
                    Reset();
                    Status("Host refused the connection: " + reason);
                    break;
                }
                case MessageType.PlayerJoined:
                {
                    var player = new PlayerInfo { Id = reader.Int(), Name = Protocol.CleanName(reader.String()) };
                    Players.RemoveAll(p => p.Id == player.Id);
                    Players.Add(player);
                    Status(player.Name + " joined");
                    PlayerJoined?.Invoke(player);
                    break;
                }
                case MessageType.PlayerLeft:
                {
                    int id = reader.Int();
                    string name = NameOf(id);
                    if (Players.RemoveAll(p => p.Id == id) > 0) Status(name + " left");
                    break;
                }
                case MessageType.Chat:
                {
                    int id = reader.Int();
                    string text = Protocol.Clean(reader.String(), Protocol.MaxChatLength);
                    if (text.Length > 0) AddLine(NameOf(id) + ": " + text);
                    break;
                }
                default:
                    if (reader.Type < MessageType.FirstGameMessage || State != SessionState.Joined)
                        throw new FormatException("unexpected " + reader.Type);
                    if (!_localWorldReady && reader.Type != MessageType.SaveBegin
                        && reader.Type != MessageType.SaveChunk && reader.Type != MessageType.WorldState) break;
                    DevTools.UpdateTrace("message-" + reader.Type);
                    GameMessage?.Invoke(null, reader);
                    DevTools.UpdateTrace("message-done-" + reader.Type);
                    break;
            }
        }

        // ---- shared ----

        internal void ResetWorldReadiness()
        {
            _readyPeers.Clear();
            _localWorldReady = false;
        }

        internal void SetPeerWorldReady(int playerId, bool ready)
        {
            if (!IsHost || playerId == HostId || !Players.Exists(p => p.Id == playerId)) return;
            if (ready) _readyPeers.Add(playerId);
            else _readyPeers.Remove(playerId);
        }

        internal void SetLocalWorldReady(bool ready)
        {
            if (IsGuest) _localWorldReady = ready;
        }

        internal void RaiseWorldStateReady(PlayerInfo player, long worldEpoch)
            => WorldStateReady?.Invoke(player, worldEpoch);

        internal void RequestFullState(int playerId, long worldEpoch)
        {
            if (!IsHost || (playerId >= 0 && !IsPeerReady(playerId))) return;
            FullStateRequested?.Invoke(playerId, worldEpoch);
        }

        private static MessageType SentType(byte[] message)
        {
            if (message == null || message.Length == 0) throw new ArgumentException("empty network message", nameof(message));
            return (MessageType)message[0];
        }

        private void DisposeFailedTransport(ITransport transport)
        {
            if (transport == null) return;
            try { transport.Dispose(); }
            catch (Exception ex) { _log("Transport cleanup after startup failure failed: " + ex); }
        }

        private void Reset()
        {
            Generation++;
            _transport?.Dispose();
            _transport = null;
            _byConnection.Clear();
            _awaitingHello.Clear();
            _readyPeers.Clear();
            _localWorldReady = false;
            Players.Clear();
            _nextPlayerId = 1;
            LocalId = 0;
            State = SessionState.Offline;
            SessionReset?.Invoke();
        }

        private void Status(string text) => AddLine("* " + text);

        private void AddLine(string line)
        {
            Lines.Add(line);
            if (Lines.Count > 200) Lines.RemoveRange(0, Lines.Count - 200);
            LinesVersion++;
            _log(line);
        }
    }
}
