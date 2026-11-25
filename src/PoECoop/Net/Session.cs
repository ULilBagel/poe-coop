using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        private const float PerfPingIntervalSeconds = 0.5f;
        private const float PerfPingTimeoutSeconds = 5f;
        private const byte PerfPing = 0;
        private const byte PerfPong = 1;

        // Guest receive budget: once handlers have used this much of a frame, what is still
        // queued waits for the next frame, in arrival order. See DispatchInbox.
        private const double ReceiveBudgetMilliseconds = 6.0;
        // A carried-over backlog beyond any of these is applied completely in one frame, so
        // deferring can cost at most about this much latency and never accumulates.
        private const double MaxDeferredAgeMilliseconds = 100.0;
        private const int MaxDeferredMessages = 200;
        private const long MaxDeferredBytes = 256 << 10;
        // After the world becomes ready the first full state is applied without a budget.
        private const double ReadySettleMilliseconds = 1000.0;

        private static readonly long ReceiveBudgetTicks = MillisecondsToTicks(ReceiveBudgetMilliseconds);
        private static readonly long MaxDeferredAgeTicks = MillisecondsToTicks(MaxDeferredAgeMilliseconds);
        private static readonly long ReadySettleTicks = MillisecondsToTicks(ReadySettleMilliseconds);

        private struct Received
        {
            public TransportEvent Event;
            public long Stamp;
            public bool Urgent;
        }

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
        // Guest only, and only with --coop-perf: the round trip being measured.
        private long _perfPingStamp;
        private float _perfPingSentAt;
        private float _nextPerfPing;
        // Everything taken from the transport and not handled yet, oldest first. It belongs to
        // the current transport: Reset empties it together with the connection it came from.
        private readonly Queue<Received> _inbox = new Queue<Received>();
        private long _inboxBytes;
        private int _inboxUrgent;
        private int _inboxCountedDeferred;
        private long _deferAllowedAt;

        /// <summary>
        /// Game-side veto for the guest receive budget: return false whenever received
        /// messages must all be applied in the frame they arrive (loading, no level, a
        /// world barrier). Not set means never defer. Main thread only.
        /// </summary>
        internal Func<bool> CanDeferReceive;

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
                Transmit(0, new MessageWriter(MessageType.Chat).String(text).ToArray());
        }

        /// <summary>Guest only. Gameplay is held until the correlated world state is ready.</summary>
        public void SendToHost(byte[] message)
        {
            if (State != SessionState.Joined) return;
            MessageType type = SentType(message);
            if (!_localWorldReady && type >= MessageType.FirstGameMessage
                && type != MessageType.SaveLoaded && type != MessageType.WorldReady) return;
            Transmit(0, message);
        }

        /// <summary>Host only. Handoff messages may cross before this peer is ready; gameplay may not.</summary>
        public void SendToPlayer(int playerId, byte[] message)
        {
            if (State != SessionState.Hosting) return;
            MessageType type = SentType(message);
            if (type >= MessageType.FirstGameMessage && !_readyPeers.Contains(playerId)
                && type != MessageType.SaveBegin && type != MessageType.SaveChunk && type != MessageType.WorldState) return;
            foreach (var pair in _byConnection)
                if (pair.Value.Id == playerId) { Transmit(pair.Key, message); return; }
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
            long perfStart = PerfStats.Begin();
            long started = Stopwatch.GetTimestamp();
            if (perfStart != 0L) SampleIncomingQueue(transport);

            // What earlier frames left behind decides, before anything new is added, whether
            // this frame has to catch up completely.
            bool drainAll = State != SessionState.Joined || DeferredBacklogTooLarge(started);

            // The transport is always emptied, so its sending, flushing, keep-alives and
            // timeouts run every frame no matter how much of this is handled now.
            while (transport == _transport && transport.Poll(out var e))
            {
                bool urgent = IsUrgent(e);
                _inbox.Enqueue(new Received { Event = e, Stamp = started, Urgent = urgent });
                if (urgent) _inboxUrgent++;
                if (e.Type == TransportEventType.Data && e.Data != null && e.Data.Length > 0)
                {
                    _inboxBytes += e.Data.Length;
                    if (perfStart != 0L) PerfStats.NetReceived(e.Data[0], e.Data.Length);
                }
            }

            // A transport callback inside Poll may have ended or replaced the session.
            if (transport != _transport) ClearInbox();

            int perfMessages = 0;
            long perfBytes = 0;
            if (transport == _transport) DispatchInbox(transport, now, started, drainAll, ref perfMessages, ref perfBytes);

            if (State == SessionState.Hosting && _awaitingHello.Count > 0) DropSilentConnections(now);
            if (perfStart != 0L)
            {
                PerfStats.Gauge("net.frame_messages", perfMessages);
                PerfStats.Gauge("net.frame_bytes", perfBytes);
                PerfStats.Gauge("net.backlog_messages", _inbox.Count);
                PerfStats.Gauge("net.backlog_age_ms", _inbox.Count > 0
                    ? (Stopwatch.GetTimestamp() - _inbox.Peek().Stamp) * 1000.0 / Stopwatch.Frequency : 0.0);
                if (transport == _transport)
                {
                    SampleOutgoingQueue(transport);
                    SendPerfPing(now);
                }
                PerfStats.End("session.update", perfStart);
            }
        }

        /// <summary>
        /// Handles queued events strictly in arrival order. On a guest inside a ready world
        /// it stops once the frame's budget is used and leaves the rest for the next frame;
        /// nothing is dropped, merged or reordered. It never stops in front of an urgent
        /// event (see IsUrgent), so those and everything that arrived before them are
        /// handled in the frame they arrive, and it always handles at least one event.
        /// </summary>
        private void DispatchInbox(ITransport transport, float now, long started, bool drainAll,
            ref int perfMessages, ref long perfBytes)
        {
            long deadline = started + ReceiveBudgetTicks;
            bool handledAny = false;
            while (transport == _transport && _inbox.Count > 0)
            {
                if (!drainAll && handledAny && _inboxUrgent == 0
                    && Stopwatch.GetTimestamp() >= deadline && MayDeferReceive())
                {
                    if (_inbox.Count > _inboxCountedDeferred)
                    {
                        PerfStats.Count("net.deferred", _inbox.Count - _inboxCountedDeferred);
                        _inboxCountedDeferred = _inbox.Count;
                    }
                    return;
                }

                Received item = _inbox.Dequeue();
                TransportEvent e = item.Event;
                if (item.Urgent) _inboxUrgent--;
                if (_inboxCountedDeferred > 0) _inboxCountedDeferred--;
                if (e.Type == TransportEventType.Data && e.Data != null && e.Data.Length > 0)
                {
                    _inboxBytes -= e.Data.Length;
                    perfMessages++;
                    perfBytes += e.Data.Length;
                }
                handledAny = true;
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
        }

        /// <summary>
        /// Events that are never held back: connection events, session control, the save
        /// transfer, world handoff/readiness markers and shared-flow commands and results.
        /// Because order is kept, one of these in the queue also forces everything that
        /// arrived before it to be handled in the same frame.
        /// </summary>
        private static bool IsUrgent(TransportEvent e)
        {
            if (e.Type != TransportEventType.Data || e.Data == null || e.Data.Length == 0) return true;
            MessageType type = (MessageType)e.Data[0];
            if (type < MessageType.FirstGameMessage) return true;
            switch (type)
            {
                case MessageType.SaveBegin:
                case MessageType.SaveChunk:
                case MessageType.SaveLoaded:
                case MessageType.WorldState:
                case MessageType.WorldReady:
                case MessageType.FlowCommand:
                case MessageType.FlowResult:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Asked only at the moment a frame would stop early, so it sees the state the handlers just left.</summary>
        private bool MayDeferReceive()
        {
            if (State != SessionState.Joined || !_localWorldReady) return false;
            if (Stopwatch.GetTimestamp() < _deferAllowedAt) return false;
            var allowed = CanDeferReceive;
            return allowed != null && allowed();
        }

        private bool DeferredBacklogTooLarge(long nowTicks)
        {
            if (_inbox.Count == 0) return false;
            return _inbox.Count > MaxDeferredMessages || _inboxBytes > MaxDeferredBytes
                || nowTicks - _inbox.Peek().Stamp >= MaxDeferredAgeTicks;
        }

        private void ClearInbox()
        {
            _inbox.Clear();
            _inboxBytes = 0;
            _inboxUrgent = 0;
            _inboxCountedDeferred = 0;
        }

        private static long MillisecondsToTicks(double milliseconds)
            => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

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
                            if (reader.Type == MessageType.PerfProbe)
                            {
                                AnswerPerfPing(e.Connection, reader);
                                break;
                            }
                            long perfStart = PerfStats.Begin();
                            GameMessage?.Invoke(sender, reader);
                            PerfStats.NetDispatched((byte)reader.Type, perfStart);
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
                refusal = "mod version differs: host has " + _modVersion + ", you have " + modVersion
                    + ". " + Protocol.UpdateAdvice(_modVersion, modVersion);
            else if (gameVersion != _gameVersion)
                refusal = "game version differs: host has " + _gameVersion + ", you have " + gameVersion;
            if (refusal != null)
            {
                Transmit(connection, new MessageWriter(MessageType.Reject).String(refusal).ToArray());
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
            Transmit(connection, welcome.ToArray());
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
                Transmit(pair.Key, message);
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
                    Transmit(0, new MessageWriter(MessageType.Hello)
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
                    // A host on an older release names the versions but not what to do about it.
                    if (reason.StartsWith("mod version differs", StringComparison.Ordinal)
                        && reason.IndexOf(Protocol.UpdateUrl, StringComparison.Ordinal) < 0)
                    {
                        const string marker = "host has ";
                        int at = reason.IndexOf(marker, StringComparison.Ordinal);
                        int comma = at < 0 ? -1 : reason.IndexOf(',', at);
                        string hostVersion = comma < 0 ? null : reason.Substring(at + marker.Length, comma - at - marker.Length);
                        reason += ". " + Protocol.UpdateAdvice(hostVersion, _modVersion);
                    }
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
                    if (reader.Type == MessageType.PerfProbe)
                    {
                        ReadPerfPong(reader);
                        break;
                    }
                    DevTools.UpdateTrace("message-", reader.Type);
                    long perfStart = PerfStats.Begin();
                    GameMessage?.Invoke(null, reader);
                    PerfStats.NetDispatched((byte)reader.Type, perfStart);
                    DevTools.UpdateTrace("message-done-", reader.Type);
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
            if (!IsGuest) return;
            if (ready && !_localWorldReady) _deferAllowedAt = Stopwatch.GetTimestamp() + ReadySettleTicks;
            _localWorldReady = ready;
        }

        internal void RaiseWorldStateReady(PlayerInfo player, long worldEpoch)
            => WorldStateReady?.Invoke(player, worldEpoch);

        internal void RequestFullState(int playerId, long worldEpoch)
        {
            if (!IsHost || (playerId >= 0 && !IsPeerReady(playerId))) return;
            FullStateRequested?.Invoke(playerId, worldEpoch);
        }

        /// <summary>Every send goes through here so traffic is counted per message type.</summary>
        private void Transmit(int connection, byte[] message)
        {
            if (PerfStats.Enabled && message != null && message.Length > 0) PerfStats.NetSent(message[0], message.Length);
            _transport.Send(connection, message);
        }

        // ---- diagnostics (--coop-perf) ----

        private static void SampleIncomingQueue(ITransport transport)
        {
            var stats = transport as ITransportStats;
            if (stats == null) return;
            PerfStats.Gauge("net.queue.in_messages", stats.PendingIncomingMessages);
            PerfStats.Gauge("net.queue.in_bytes", stats.PendingIncomingBytes);
        }

        private static void SampleOutgoingQueue(ITransport transport)
        {
            var stats = transport as ITransportStats;
            if (stats == null) return;
            PerfStats.Gauge("net.queue.out_messages", stats.PendingOutgoingMessages);
            PerfStats.Gauge("net.queue.out_bytes", stats.PendingOutgoingBytes);
        }

        /// <summary>Guest: measures the round trip through both main threads, one ping at a time.</summary>
        private void SendPerfPing(float now)
        {
            if (State != SessionState.Joined || !_localWorldReady) return;
            if (_perfPingStamp != 0L)
            {
                if (now - _perfPingSentAt < PerfPingTimeoutSeconds) return;
                _perfPingStamp = 0L;
                PerfStats.Count("net.rtt_lost");
            }
            if (now < _nextPerfPing) return;
            _nextPerfPing = now + PerfPingIntervalSeconds;
            _perfPingSentAt = now;
            _perfPingStamp = Stopwatch.GetTimestamp();
            Transmit(0, new MessageWriter(MessageType.PerfProbe).Byte(PerfPing).Long(_perfPingStamp).ToArray());
        }

        /// <summary>Host: echoes a guest's ping whether or not diagnostics are on here.</summary>
        private void AnswerPerfPing(int connection, MessageReader reader)
        {
            byte kind = reader.Byte();
            long stamp = reader.Long();
            reader.Finish();
            if (kind != PerfPing) throw new FormatException("perf probe kind " + kind);
            Transmit(connection, new MessageWriter(MessageType.PerfProbe).Byte(PerfPong).Long(stamp).ToArray());
        }

        private void ReadPerfPong(MessageReader reader)
        {
            byte kind = reader.Byte();
            long stamp = reader.Long();
            reader.Finish();
            if (kind != PerfPong || stamp != _perfPingStamp || stamp == 0L) return;
            _perfPingStamp = 0L;
            PerfStats.Gauge("net.rtt_ms", (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency);
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
            // Undelivered events die with the connection they came from; none reach a later session.
            ClearInbox();
            _byConnection.Clear();
            _awaitingHello.Clear();
            _readyPeers.Clear();
            _localWorldReady = false;
            Players.Clear();
            _nextPlayerId = 1;
            _perfPingStamp = 0L;
            _nextPerfPing = 0f;
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
