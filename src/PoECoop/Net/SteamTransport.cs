using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Steamworks;

namespace PoECoop.Net
{
    /// <summary>
    /// Reliable ordered Session messages over legacy Steam P2P. Channel 0 is a
    /// private transport handshake/keepalive channel; channel 1 carries Session
    /// messages unchanged. All calls and backend callbacks run on the main thread.
    /// </summary>
    public sealed class SteamTransport : ITransport, ITransportStats
    {
        public const int MaxMessageSize = 1 << 20;
        public const int DefaultLobbyMembers = 6;

        internal const int ControlChannel = 0;
        internal const int GameplayChannel = 1;
        internal const int MaxQueuedIncomingBytesPerLink = 16 << 20;
        internal const int MaxQueuedIncomingBytes = 64 << 20;
        internal const int MaxQueuedOutgoingBytesPerLink = 288 << 20;
        internal const int EstablishmentTimeoutMs = 30 * 1000;
        internal const int StallTimeoutMs = 30 * 1000;

        private const int MaxQueuedIncomingMessagesPerLink = 16 * 1024;
        private const int MaxQueuedIncomingMessages = 64 * 1024;
        private const int MaxQueuedOutgoingMessagesPerLink = 64 * 1024;
        private const int MaxSteamLobbyMembers = 250;
        private const int KeepaliveIntervalMs = 5 * 1000;
        private const int ClientHelloRetryMs = 1000;
        private const int ControlPacketsPerFrame = 64;
        private const int ControlBytesPerFrame = 256 << 10;
        private const int GameplayPacketsPerFrame = 256;
        private const int GameplayBytesPerFrame = 8 << 20;
        private const int SendPacketsPerFrame = 256;
        private const int SendBytesPerFrame = 8 << 20;
        private const int HeaderSize = 8;
        private const int HelloSize = HeaderSize + 16;
        private const byte ClientHelloKind = 1;
        private const byte ServerHelloKind = 2;
        private const byte KeepaliveKind = 3;

        private readonly ISteamBackend _backend;
        private readonly Func<long> _milliseconds;
        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly Dictionary<CSteamID, Link> _bySteamId = new Dictionary<CSteamID, Link>();
        private readonly Queue<QueuedEvent> _events = new Queue<QueuedEvent>();
        private readonly List<Link> _timeoutScan = new List<Link>();
        private int _sendPacketBudget = SendPacketsPerFrame;
        private int _sendByteBudget = SendBytesPerFrame;
        private byte[] _invalidPacketBuffer;
        private IncomingPacket _lookahead;
        private CSteamID _localUser;
        private CSteamID _requestedLobby;
        private CSteamID _lobbyId;
        private int _nextId = 1;
        private int _queuedIncomingBytes;
        private int _queuedIncomingMessages;
        private long _operationStarted;
        private bool _hosting;
        private bool _guest;
        private bool _lobbyOperationPending;
        private bool _guestDisconnectPending;
        private bool _pumpedSinceEmpty;
        private bool _attachOnly;
        private bool _disposed;

        public SteamTransport() : this(new SteamworksSteamBackend(), null) { }

        public SteamTransport(CSteamID lobby, CSteamID owner, bool hosting)
            : this(new SteamworksSteamBackend(), null)
        {
            try { AttachExistingLobby(lobby, owner, hosting); }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal SteamTransport(ISteamBackend backend, Func<long> milliseconds = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _milliseconds = milliseconds ?? MonotonicMilliseconds;
            _backend.LobbyCreated += OnLobbyCreated;
            _backend.LobbyJoined += OnLobbyJoined;
            _backend.LobbyMembershipChanged += OnLobbyMembershipChanged;
            _backend.LobbyJoinRequested += OnLobbyJoinRequested;
            _backend.P2PSessionRequested += OnP2PSessionRequested;
            _backend.P2PSessionFailed += OnP2PSessionFailed;
        }

        public event Action<CSteamID> LobbyHosted;
        public event Action<CSteamID> LobbyJoined;
        public event Action<string> LobbyFailed;
        /// <summary>Steam overlay invite accepted; parameters are lobby and inviting friend.</summary>
        public event Action<CSteamID, CSteamID> LobbyJoinRequested;

        public CSteamID LobbyId => _lobbyId;
        public CSteamID LobbyOwnerId { get; private set; }
        public CSteamID LocalUserId => _localUser;
        public bool IsLobbyReady => _lobbyId != CSteamID.Nil && !_lobbyOperationPending
            && (_hosting || _guest);
        public string LobbyAddress => _lobbyId == CSteamID.Nil ? "" : _lobbyId.m_SteamID.ToString(CultureInfo.InvariantCulture);
        public string DisplayName => "Steam";

        public int PendingIncomingMessages => _queuedIncomingMessages;
        public long PendingIncomingBytes => _queuedIncomingBytes;

        public int PendingOutgoingMessages
        {
            get
            {
                int total = 0;
                foreach (var link in _links.Values) total += link.OutgoingMessages;
                return total;
            }
        }

        public long PendingOutgoingBytes
        {
            get
            {
                long total = 0;
                foreach (var link in _links.Values) total += link.OutgoingBytes;
                return total;
            }
        }
        public string Status
        {
            get
            {
                if (_lobbyId == CSteamID.Nil) return "Steam";
                if (_hosting) return "Hosting Steam lobby " + LobbyAddress;
                if (_guest)
                {
                    Link link;
                    string prefix = _links.TryGetValue(Session.HostId, out link) && link.Stage == LinkStage.Connected
                        ? "Connected to " : "Connecting to ";
                    return prefix + "Steam host " + LobbyOwnerId.m_SteamID.ToString(CultureInfo.InvariantCulture);
                }
                return "Steam";
            }
        }

        public void Host(int port)
        {
            if (_hosting && IsLobbyReady) return;
            if (_attachOnly) throw new InvalidOperationException("The attached Steam lobby is not available for hosting");
            HostLobby(DefaultLobbyMembers);
        }

        /// <summary>Creates a friends-only lobby. Completion is reported by LobbyHosted or LobbyFailed.</summary>
        public void HostLobby(int maxMembers)
        {
            ThrowIfDisposed();
            if (_attachOnly) throw new InvalidOperationException("This transport is attached to an existing Steam lobby");
            if (maxMembers < 2 || maxMembers > MaxSteamLobbyMembers)
                throw new ArgumentOutOfRangeException(nameof(maxMembers));
            ResetForStart();
            if (!_backend.IsAvailable) throw new InvalidOperationException("Steam is not initialized");
            _localUser = _backend.LocalUserId;
            if (!_localUser.IsValid()) throw new InvalidOperationException("Steam returned an invalid local user id");
            _hosting = true;
            _lobbyOperationPending = true;
            _operationStarted = Now();
            try { _backend.CreateLobby(maxMembers); }
            catch
            {
                ResetForStart();
                throw;
            }
        }

        public void Connect(string address, int port)
        {
            CSteamID lobby;
            if (_attachOnly)
            {
                if (_guest && IsLobbyReady && TryParseLobbyAddress(address, out lobby) && lobby == _lobbyId) return;
                throw new InvalidOperationException("The attached Steam lobby is not available for joining");
            }
            if (!TryParseLobbyAddress(address, out lobby))
            {
                ThrowIfDisposed();
                ResetForStart();
                _guest = true;
                _guestDisconnectPending = true;
                StopGuest("invalid Steam lobby id");
                return;
            }
            JoinLobby(lobby);
        }

        /// <summary>Joins a lobby and connects only to its owner at the instant joining completes.</summary>
        public void JoinLobby(CSteamID lobby)
        {
            ThrowIfDisposed();
            if (_attachOnly) throw new InvalidOperationException("This transport is attached to an existing Steam lobby");
            ResetForStart();
            _guest = true;
            _guestDisconnectPending = true;
            if (!lobby.IsValid() || !lobby.IsLobby())
            {
                StopGuest("invalid Steam lobby id");
                return;
            }
            try
            {
                if (!_backend.IsAvailable) { StopGuest("Steam is not initialized"); return; }
                _localUser = _backend.LocalUserId;
                if (!_localUser.IsValid()) { StopGuest("Steam returned an invalid local user id"); return; }
                _requestedLobby = lobby;
                _lobbyOperationPending = true;
                _operationStarted = Now();
                _backend.JoinLobby(lobby);
            }
            catch (Exception ex)
            {
                StopGuest(ex.Message);
            }
        }

        public bool InviteUser(CSteamID invitee)
        {
            if (_disposed || !_hosting || !IsLobbyReady || invitee == _localUser || !invitee.IsValid()) return false;
            try { return _backend.InviteUserToLobby(_lobbyId, invitee); }
            catch { return false; }
        }

        public static bool TryParseLobbyAddress(string address, out CSteamID lobby)
        {
            ulong value;
            if (ulong.TryParse((address ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                lobby = new CSteamID(value);
                if (lobby.IsValid() && lobby.IsLobby()) return true;
            }
            lobby = CSteamID.Nil;
            return false;
        }

        public bool Poll(out TransportEvent e)
        {
            if (_disposed)
            {
                e = default(TransportEvent);
                return false;
            }
            if (!_pumpedSinceEmpty)
            {
                long started = PerfStats.Begin();
                Pump();
                PerfStats.End("transport.poll", started);
                _pumpedSinceEmpty = true;
            }
            if (_events.Count == 0)
            {
                _pumpedSinceEmpty = false;
                e = default(TransportEvent);
                return false;
            }
            QueuedEvent queued = _events.Dequeue();
            if (queued.IncomingBytes != 0) ReleaseIncoming(queued.Source, queued.IncomingBytes);
            e = queued.Event;
            return true;
        }

        public void Send(int connection, byte[] message)
        {
            if (_disposed) return;
            Link link;
            if (!_links.TryGetValue(connection, out link)) return;
            long started = PerfStats.Begin();
            if (link.Stage != LinkStage.Connected || !SendOrEnqueue(link, message))
                CloseLink(link, "outgoing queue limit, invalid message, or handshake incomplete", true);
            PerfStats.End("transport.send", started);
        }

        /// <summary>
        /// Hands the message to Steam now when nothing older is waiting on this link and
        /// this frame's send budget allows; otherwise it waits in order for the next pump.
        /// Without this every message would sit until the following frame's first Poll.
        /// </summary>
        private bool SendOrEnqueue(Link link, byte[] message)
        {
            if (message == null || message.Length <= 0 || message.Length > MaxMessageSize || link.Closed) return false;
            if (link.Outgoing.Count == 0 && _sendPacketBudget > 0 && message.Length <= _sendByteBudget)
            {
                bool sent;
                try { sent = _backend.SendPacket(link.Remote, message, GameplayChannel); }
                catch (Exception ex)
                {
                    CloseLink(link, ex.Message, true);
                    return true;
                }
                if (sent)
                {
                    link.LastSend = Now();
                    _sendPacketBudget--;
                    _sendByteBudget -= message.Length;
                    PerfStats.Count("transport.send.direct");
                    return true;
                }
                PerfStats.Count("transport.send.refused");
            }
            PerfStats.Count("transport.send.queued");
            return Enqueue(link, message, GameplayChannel, false);
        }

        public void Disconnect(int connection)
        {
            if (_disposed) return;
            if (_guest && connection == Session.HostId)
            {
                StopGuest("closed locally");
                return;
            }
            Link link;
            if (_links.TryGetValue(connection, out link)) CloseLink(link, "closed locally", true);
        }

        public string Describe(int connection)
        {
            Link link;
            return _links.TryGetValue(connection, out link) ? "Steam " + link.Remote.m_SteamID.ToString(CultureInfo.InvariantCulture) : "?";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var link in new List<Link>(_links.Values)) CloseLink(link, "shut down", false);
            CSteamID lobby = _lobbyId;
            ClearLobbyState();
            _events.Clear();
            _lookahead = null;
            _queuedIncomingBytes = 0;
            _queuedIncomingMessages = 0;
            if (!_attachOnly) try { _backend.CancelLobbyOperation(); } catch { }
            if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
            _backend.LobbyCreated -= OnLobbyCreated;
            _backend.LobbyJoined -= OnLobbyJoined;
            _backend.LobbyMembershipChanged -= OnLobbyMembershipChanged;
            _backend.LobbyJoinRequested -= OnLobbyJoinRequested;
            _backend.P2PSessionRequested -= OnP2PSessionRequested;
            _backend.P2PSessionFailed -= OnP2PSessionFailed;
            _backend.Dispose();
        }

        private void Pump()
        {
            long now = Now();
            _sendPacketBudget = SendPacketsPerFrame;
            _sendByteBudget = SendBytesPerFrame;
            CheckTimeouts(now);
            if (_disposed || (!_hosting && !_guest)) return;

            if (_lookahead != null && !TryDispatchLookahead())
            {
                FlushOutgoing(now);
                return;
            }

            int controlPackets = ControlPacketsPerFrame;
            int controlBytes = ControlBytesPerFrame;
            ReceiveChannel(ControlChannel, ref controlPackets, ref controlBytes, now);
            QueueClientHelloRetry(now);
            FlushOutgoing(now);

            if (_lookahead == null)
            {
                int gameplayPackets = GameplayPacketsPerFrame;
                int gameplayBytes = GameplayBytesPerFrame;
                ReceiveChannel(GameplayChannel, ref gameplayPackets, ref gameplayBytes, now);
                if (gameplayPackets == 0) PerfStats.Count("transport.poll.budget_hit");
            }
            QueueKeepalives(now);
        }

        private void CheckTimeouts(long now)
        {
            if (_lobbyOperationPending && Elapsed(now, _operationStarted, EstablishmentTimeoutMs))
            {
                if (_hosting) StopHost("Steam lobby creation timed out", true);
                else if (_guest) StopGuest("Steam lobby join timed out");
                return;
            }
            if (_links.Count == 0) return;
            _timeoutScan.Clear();
            _timeoutScan.AddRange(_links.Values);
            foreach (var link in _timeoutScan)
            {
                if (link.Stage != LinkStage.Connected)
                {
                    if (Elapsed(now, link.Created, EstablishmentTimeoutMs))
                        CloseLink(link, "Steam P2P handshake timed out", true);
                }
                else if (Elapsed(now, link.LastReceived, StallTimeoutMs))
                {
                    CloseLink(link, "Steam P2P receive stalled", true);
                }
                else if (link.Outgoing.Count != 0 && Elapsed(now, link.LastSend, StallTimeoutMs))
                {
                    CloseLink(link, "Steam P2P send stalled", true);
                }
                if (!_hosting && !_guest) return;
            }
        }

        private void ReceiveChannel(int channel, ref int packetBudget, ref int byteBudget, long now)
        {
            while (!_disposed && (_hosting || _guest) && packetBudget > 0 && _lookahead == null)
            {
                uint announced;
                bool available;
                try { available = _backend.IsPacketAvailable(out announced, channel); }
                catch (Exception ex) { AbortBackend(ex.Message); return; }
                if (!available) return;
                packetBudget--;

                if (announced == 0 || announced > MaxMessageSize)
                {
                    RejectInvalidSizedPacket(channel, announced);
                    if (_disposed || (!_hosting && !_guest)) return;
                    continue;
                }
                if (announced > byteBudget) return;

                byte[] packet = new byte[(int)announced];
                uint actual;
                CSteamID remote;
                bool read;
                try { read = _backend.ReadPacket(packet, out actual, out remote, channel); }
                catch (Exception ex) { AbortBackend(ex.Message); return; }
                if (!read)
                {
                    AbortBackend("Steam P2P packet disappeared before it could be read");
                    return;
                }
                byteBudget -= (int)announced;
                if (actual != announced)
                {
                    RejectRemote(remote, "P2P packet size changed from " + announced + " to " + actual);
                    continue;
                }
                AcceptPacket(remote, channel, packet, now);
            }
        }

        private void RejectInvalidSizedPacket(int channel, uint announced)
        {
            byte[] buffer = _invalidPacketBuffer ?? (_invalidPacketBuffer = new byte[MaxMessageSize]);
            uint actual;
            CSteamID remote;
            bool read;
            try { read = _backend.ReadPacket(buffer, out actual, out remote, channel); }
            catch (Exception ex) { AbortBackend(ex.Message); return; }
            if (!read)
            {
                AbortBackend("could not consume invalid Steam P2P packet of size " + announced);
                return;
            }
            RejectRemote(remote, "invalid P2P packet size " + announced);
        }

        private void AcceptPacket(CSteamID remote, int channel, byte[] packet, long now)
        {
            Link link = AuthorizePacket(remote, channel, packet);
            if (link == null)
            {
                if (remote != _localUser) try { _backend.CloseP2PSession(remote); } catch { }
                return;
            }
            link.LastReceived = now;
            if (!ReserveIncoming(link, packet.Length))
            {
                _lookahead = new IncomingPacket { Remote = remote, Channel = channel, Data = packet };
                return;
            }
            DispatchReserved(link, channel, packet);
        }

        private bool TryDispatchLookahead()
        {
            IncomingPacket packet = _lookahead;
            Link link;
            if (packet == null) return true;
            if (!_bySteamId.TryGetValue(packet.Remote, out link) || link.Closed)
            {
                _lookahead = null;
                return true;
            }
            if (!ReserveIncoming(link, packet.Data.Length)) return false;
            _lookahead = null;
            DispatchReserved(link, packet.Channel, packet.Data);
            return true;
        }

        private Link AuthorizePacket(CSteamID remote, int channel, byte[] packet)
        {
            Link link;
            if (_bySteamId.TryGetValue(remote, out link)) return link.Closed ? null : link;
            if (!_hosting || !IsLobbyReady || channel != ControlChannel || remote == _localUser
                || !LooksLikeClientHello(packet) || !IsCurrentLobbyMember(remote)) return null;
            bool accepted;
            try { accepted = _backend.AcceptP2PSession(remote); }
            catch { return null; }
            if (!accepted) return null;
            link = new Link(_nextId++, remote, Now(), LinkStage.AwaitingClientHello);
            _links.Add(link.Id, link);
            _bySteamId.Add(remote, link);
            return link;
        }

        private void DispatchReserved(Link link, int channel, byte[] packet)
        {
            if (channel == GameplayChannel)
            {
                if (link.Stage != LinkStage.Connected)
                {
                    ReleaseIncoming(link, packet.Length);
                    CloseLink(link, "gameplay data arrived before the Steam handshake", true);
                    return;
                }
                _events.Enqueue(new QueuedEvent
                {
                    Event = new TransportEvent { Type = TransportEventType.Data, Connection = link.Id, Data = packet },
                    Source = link,
                    IncomingBytes = packet.Length,
                });
                return;
            }

            try { ProcessControlPacket(link, packet); }
            finally { ReleaseIncoming(link, packet.Length); }
        }

        private void ProcessControlPacket(Link link, byte[] packet)
        {
            byte kind;
            if (!ValidHeader(packet, out kind))
            {
                CloseLink(link, "invalid Steam transport control packet", true);
                return;
            }
            if (kind == KeepaliveKind)
            {
                if (packet.Length != HeaderSize || link.Stage != LinkStage.Connected)
                    CloseLink(link, "unexpected Steam keepalive", true);
                return;
            }
            if (packet.Length != HelloSize)
            {
                CloseLink(link, "invalid Steam transport hello size", true);
                return;
            }

            if (_hosting)
            {
                if (kind != ClientHelloKind)
                {
                    CloseLink(link, "unexpected Steam ClientHello", true);
                    return;
                }
                if (link.Stage != LinkStage.AwaitingClientHello)
                {
                    if (!NonceEquals(packet, link.Nonce))
                        CloseLink(link, "Steam ClientHello nonce mismatch", true);
                    return; // Same-nonce retransmission while the reliable reply is in flight.
                }
                byte[] nonce = new byte[16];
                Buffer.BlockCopy(packet, HeaderSize, nonce, 0, nonce.Length);
                link.Nonce = nonce;
                link.Stage = LinkStage.ServerHelloQueued;
                byte[] hello = CreateHello(ServerHelloKind, nonce);
                if (!Enqueue(link, hello, ControlChannel, true))
                    CloseLink(link, "Steam ServerHello queue limit", true);
                return;
            }

            if (kind != ServerHelloKind || link.Stage != LinkStage.AwaitingServerHello)
            {
                CloseLink(link, "unexpected Steam ServerHello", true);
                return;
            }
            if (!NonceEquals(packet, link.Nonce))
            {
                CloseLink(link, "Steam ServerHello nonce mismatch", true);
                return;
            }
            ConnectLink(link);
        }

        private void QueueClientHelloRetry(long now)
        {
            if (!_guest) return;
            Link link;
            if (!_links.TryGetValue(Session.HostId, out link) || link.Stage != LinkStage.AwaitingServerHello
                || link.Outgoing.Count != 0 || !Elapsed(now, link.LastSend, ClientHelloRetryMs)) return;
            if (!Enqueue(link, CreateHello(ClientHelloKind, link.Nonce), ControlChannel, false))
                CloseLink(link, "Steam ClientHello queue limit", true);
        }

        private void FlushOutgoing(long now)
        {
            bool pending = false;
            foreach (var link in _links.Values)
                if (link.Outgoing.Count != 0) { pending = true; break; }
            if (!pending) return;

            var links = new List<Link>(_links.Values);
            links.Sort((left, right) => left.Id.CompareTo(right.Id));
            var blocked = new HashSet<Link>();
            while (_sendPacketBudget > 0)
            {
                bool attempted = false;
                bool sentAny = false;
                foreach (var link in links)
                {
                    if (link.Closed || link.Outgoing.Count == 0 || blocked.Contains(link)) continue;
                    attempted = true;
                    OutgoingPacket packet = link.Outgoing.Peek();
                    if (packet.Data.Length > _sendByteBudget) return;
                    bool sent;
                    try { sent = _backend.SendPacket(link.Remote, packet.Data, packet.Channel); }
                    catch (Exception ex) { CloseLink(link, ex.Message, true); continue; }
                    if (!sent)
                    {
                        // Retry this exact head packet next frame; never drop or reorder it.
                        blocked.Add(link);
                        continue;
                    }

                    link.Outgoing.Dequeue();
                    link.OutgoingBytes -= packet.Data.Length;
                    link.OutgoingMessages--;
                    link.LastSend = now;
                    _sendPacketBudget--;
                    _sendByteBudget -= packet.Data.Length;
                    sentAny = true;
                    if (packet.IsServerHello && link.Stage == LinkStage.ServerHelloQueued)
                        ConnectLink(link);
                    if (_sendPacketBudget == 0) break;
                }
                if (!attempted || !sentAny) return;
            }
        }

        private void QueueKeepalives(long now)
        {
            foreach (var link in _links.Values)
            {
                if (link.Stage != LinkStage.Connected || !Elapsed(now, link.LastKeepaliveQueued, KeepaliveIntervalMs)
                    || !Elapsed(now, link.LastSend, KeepaliveIntervalMs)) continue;
                byte[] keepalive = new byte[HeaderSize];
                WriteHeader(keepalive, KeepaliveKind);
                if (!Enqueue(link, keepalive, ControlChannel, false))
                {
                    CloseLink(link, "Steam keepalive queue limit", true);
                    return;
                }
                link.LastKeepaliveQueued = now;
            }
        }

        private bool Enqueue(Link link, byte[] message, int channel, bool serverHello)
        {
            if (message == null || message.Length <= 0 || message.Length > MaxMessageSize || link.Closed) return false;
            if (link.OutgoingBytes > MaxQueuedOutgoingBytesPerLink - message.Length
                || link.OutgoingMessages >= MaxQueuedOutgoingMessagesPerLink) return false;
            link.Outgoing.Enqueue(new OutgoingPacket
            {
                Data = message,
                Channel = channel,
                IsServerHello = serverHello,
            });
            link.OutgoingBytes += message.Length;
            link.OutgoingMessages++;
            return true;
        }

        private bool ReserveIncoming(Link link, int count)
        {
            if (link.Closed || _queuedIncomingBytes > MaxQueuedIncomingBytes - count
                || link.QueuedIncomingBytes > MaxQueuedIncomingBytesPerLink - count
                || _queuedIncomingMessages >= MaxQueuedIncomingMessages
                || link.QueuedIncomingMessages >= MaxQueuedIncomingMessagesPerLink) return false;
            _queuedIncomingBytes += count;
            _queuedIncomingMessages++;
            link.QueuedIncomingBytes += count;
            link.QueuedIncomingMessages++;
            return true;
        }

        private void ReleaseIncoming(Link link, int count)
        {
            _queuedIncomingBytes -= count;
            _queuedIncomingMessages--;
            link.QueuedIncomingBytes -= count;
            link.QueuedIncomingMessages--;
        }

        private void AttachExistingLobby(CSteamID lobby, CSteamID owner, bool hosting)
        {
            ThrowIfDisposed();
            _attachOnly = true;
            ResetForStart();
            if (!lobby.IsValid() || !lobby.IsLobby()) throw new ArgumentException("Invalid Steam lobby", nameof(lobby));
            if (!_backend.IsAvailable) throw new InvalidOperationException("Steam is not initialized");
            _backend.AllowP2PPacketRelay();
            _localUser = _backend.LocalUserId;
            if (!_localUser.IsValid()) throw new InvalidOperationException("Steam returned an invalid local user id");

            _lobbyId = lobby;
            LobbyOwnerId = _backend.GetLobbyOwner(lobby);
            if (!LobbyOwnerId.IsValid() || LobbyOwnerId != owner)
                throw new InvalidOperationException("The Steam lobby owner changed before the transport started");
            if (hosting && LobbyOwnerId != _localUser)
                throw new InvalidOperationException("The local Steam user does not own this lobby");
            if (!hosting && LobbyOwnerId == _localUser)
                throw new InvalidOperationException("Cannot join a Steam lobby owned by the local user");
            if (!IsCurrentLobbyMember(_localUser) || !IsCurrentLobbyMember(LobbyOwnerId))
                throw new InvalidOperationException("The Steam lobby membership is not ready");

            _hosting = hosting;
            _guest = !hosting;
            if (_guest)
            {
                _guestDisconnectPending = true;
                Link link = new Link(Session.HostId, LobbyOwnerId, Now(), LinkStage.AwaitingServerHello);
                link.Nonce = Guid.NewGuid().ToByteArray();
                _links.Add(link.Id, link);
                _bySteamId.Add(link.Remote, link);
                if (!_backend.AcceptP2PSession(link.Remote)
                    || !Enqueue(link, CreateHello(ClientHelloKind, link.Nonce), ControlChannel, false))
                    throw new InvalidOperationException("Could not start the Steam P2P handshake");
                LobbyJoined?.Invoke(lobby);
            }
            else
            {
                LobbyHosted?.Invoke(lobby);
            }
        }

        private void OnLobbyCreated(CSteamID lobby, bool success, string reason)
        {
            if (_disposed) return;
            if (!_hosting || !_lobbyOperationPending)
            {
                if (success && lobby != _lobbyId) if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
                return;
            }
            _lobbyOperationPending = false;
            if (!success) { StopHost(reason ?? "Steam lobby creation failed", true); return; }
            _lobbyId = lobby;
            try
            {
                LobbyOwnerId = _backend.GetLobbyOwner(lobby);
                if (LobbyOwnerId != _localUser || !IsCurrentLobbyMember(_localUser))
                {
                    StopHost("Steam did not make the local user the lobby owner", true);
                    return;
                }
            }
            catch (Exception ex) { StopHost(ex.Message, true); return; }
            LobbyHosted?.Invoke(lobby);
        }

        private void OnLobbyJoined(CSteamID lobby, bool success, string reason)
        {
            if (_disposed) return;
            if (!_guest || !_lobbyOperationPending || lobby != _requestedLobby)
            {
                if (success && lobby != _lobbyId) if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
                return;
            }
            _lobbyOperationPending = false;
            if (!success) { StopGuest(reason ?? "Steam lobby join failed"); return; }
            _lobbyId = lobby;
            try
            {
                LobbyOwnerId = _backend.GetLobbyOwner(lobby);
                if (!LobbyOwnerId.IsValid() || LobbyOwnerId == _localUser || !IsCurrentLobbyMember(LobbyOwnerId))
                {
                    StopGuest("lobby owner is unavailable");
                    return;
                }
                Link link = new Link(Session.HostId, LobbyOwnerId, Now(), LinkStage.AwaitingServerHello);
                link.Nonce = Guid.NewGuid().ToByteArray();
                _links.Add(link.Id, link);
                _bySteamId.Add(link.Remote, link);
                if (!_backend.AcceptP2PSession(link.Remote)
                    || !Enqueue(link, CreateHello(ClientHelloKind, link.Nonce), ControlChannel, false))
                {
                    StopGuest("could not start the Steam P2P handshake");
                    return;
                }
            }
            catch (Exception ex) { StopGuest(ex.Message); return; }
            LobbyJoined?.Invoke(lobby);
        }

        private void OnLobbyMembershipChanged(CSteamID lobby, CSteamID changedUser)
        {
            if (_disposed || !IsLobbyReady || lobby != _lobbyId) return;
            try
            {
                if (_hosting)
                {
                    if (_backend.GetLobbyOwner(lobby) != _localUser || !IsCurrentLobbyMember(_localUser))
                    {
                        StopHost("lobby ownership changed or local membership ended; host migration is not supported", true);
                        return;
                    }
                    foreach (var link in new List<Link>(_links.Values))
                        if (!IsCurrentLobbyMember(link.Remote)) CloseLink(link, "Steam user left the lobby", true);
                }
                else if (_guest)
                {
                    if (_backend.GetLobbyOwner(lobby) != LobbyOwnerId || !IsCurrentLobbyMember(_localUser))
                        StopGuest("lobby owner changed or local membership ended; host migration is not supported");
                }
            }
            catch (Exception ex) { AbortBackend(ex.Message); }
        }

        private void OnLobbyJoinRequested(CSteamID lobby, CSteamID friend)
        {
            if (!_disposed) LobbyJoinRequested?.Invoke(lobby, friend);
        }

        private void OnP2PSessionRequested(CSteamID remote)
        {
            if (_disposed || remote == _localUser || !IsLobbyReady) return;
            try
            {
                if (_hosting)
                {
                    if (!_bySteamId.ContainsKey(remote) && IsCurrentLobbyMember(remote))
                        _backend.AcceptP2PSession(remote);
                }
                else if (_guest && remote == LobbyOwnerId)
                {
                    _backend.AcceptP2PSession(remote);
                }
            }
            catch (Exception ex) { AbortBackend(ex.Message); }
        }

        private void OnP2PSessionFailed(CSteamID remote, byte error)
        {
            if (_disposed) return;
            Link link;
            if (_bySteamId.TryGetValue(remote, out link))
                CloseLink(link, "Steam P2P failure " + error, true);
        }

        private bool IsCurrentLobbyMember(CSteamID user)
        {
            if (_lobbyId == CSteamID.Nil || user == CSteamID.Nil) return false;
            int count = _backend.GetLobbyMemberCount(_lobbyId);
            if (count <= 0 || count > MaxSteamLobbyMembers) return false;
            for (int i = 0; i < count; i++)
                if (_backend.GetLobbyMemberByIndex(_lobbyId, i) == user) return true;
            return false;
        }

        private void ConnectLink(Link link)
        {
            if (link.Closed || link.Stage == LinkStage.Connected) return;
            link.Stage = LinkStage.Connected;
            link.LastReceived = Now();
            _events.Enqueue(new QueuedEvent
            {
                Event = new TransportEvent
                {
                    Type = TransportEventType.Connected,
                    Connection = link.Id,
                    Text = Describe(link.Id),
                },
            });
        }

        private void CloseLink(Link link, string reason, bool notify)
        {
            if (link == null || link.Closed) return;
            link.Closed = true;
            Link current;
            if (_links.TryGetValue(link.Id, out current) && current == link) _links.Remove(link.Id);
            if (_bySteamId.TryGetValue(link.Remote, out current) && current == link) _bySteamId.Remove(link.Remote);
            if (_lookahead != null && _lookahead.Remote == link.Remote) _lookahead = null;
            try { _backend.CloseP2PSession(link.Remote); } catch { }
            if (!notify || _disposed) return;
            if (link.Stage == LinkStage.Connected || (_guest && link.Id == Session.HostId && _guestDisconnectPending))
            {
                _events.Enqueue(new QueuedEvent
                {
                    Event = new TransportEvent
                    {
                        Type = TransportEventType.Disconnected,
                        Connection = link.Id,
                        Text = reason ?? "connection closed",
                    },
                });
                if (_guest && link.Id == Session.HostId) _guestDisconnectPending = false;
            }
            if (_guest && link.Id == Session.HostId)
            {
                CSteamID lobby = _lobbyId;
                ClearLobbyState();
                if (!_attachOnly) try { _backend.CancelLobbyOperation(); } catch { }
                if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
            }
        }

        private void StopHost(string reason, bool notifyLobbyFailure)
        {
            if (!_hosting && !_guest) return;
            bool host = _hosting;
            foreach (var link in new List<Link>(_links.Values)) CloseLink(link, reason, true);
            CSteamID lobby = _lobbyId;
            ClearLobbyState();
            if (!_attachOnly) try { _backend.CancelLobbyOperation(); } catch { }
            if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
            if (host && notifyLobbyFailure && !_disposed)
            {
                _events.Enqueue(new QueuedEvent
                {
                    Event = new TransportEvent
                    {
                        Type = TransportEventType.Disconnected,
                        Connection = Session.HostId,
                        Text = reason ?? "Steam lobby closed",
                    },
                });
                LobbyFailed?.Invoke(reason ?? "Steam lobby closed");
            }
        }

        private void StopGuest(string reason)
        {
            if (!_guest && !_hosting) return;
            if (_hosting) { StopHost(reason, true); return; }
            bool needsEvent = _guestDisconnectPending && _links.Count == 0;
            foreach (var link in new List<Link>(_links.Values)) CloseLink(link, reason, true);
            CSteamID lobby = _lobbyId;
            ClearLobbyState();
            if (!_attachOnly) try { _backend.CancelLobbyOperation(); } catch { }
            if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
            if (needsEvent && !_disposed)
                _events.Enqueue(new QueuedEvent
                {
                    Event = new TransportEvent
                    {
                        Type = TransportEventType.Disconnected,
                        Connection = Session.HostId,
                        Text = reason ?? "connection closed",
                    },
                });
            _guestDisconnectPending = false;
        }

        private void AbortBackend(string reason)
        {
            if (_hosting) StopHost("Steam backend failure: " + reason, true);
            else if (_guest) StopGuest("Steam backend failure: " + reason);
        }

        private void RejectRemote(CSteamID remote, string reason)
        {
            Link link;
            if (_bySteamId.TryGetValue(remote, out link)) CloseLink(link, reason, true);
            else
            {
                try { _backend.CloseP2PSession(remote); } catch { }
            }
        }

        private void ResetForStart()
        {
            foreach (var link in new List<Link>(_links.Values)) CloseLink(link, "replaced", false);
            CSteamID lobby = _lobbyId;
            ClearLobbyState();
            _events.Clear();
            _lookahead = null;
            _queuedIncomingBytes = 0;
            _queuedIncomingMessages = 0;
            _nextId = 1;
            _localUser = CSteamID.Nil;
            _requestedLobby = CSteamID.Nil;
            _pumpedSinceEmpty = false;
            if (!_attachOnly) try { _backend.CancelLobbyOperation(); } catch { }
            if (!_attachOnly) try { _backend.LeaveLobby(lobby); } catch { }
        }

        private void ClearLobbyState()
        {
            _hosting = false;
            _guest = false;
            _lobbyOperationPending = false;
            _lobbyId = CSteamID.Nil;
            _requestedLobby = CSteamID.Nil;
            LobbyOwnerId = CSteamID.Nil;
            _guestDisconnectPending = false;
        }

        private long Now() => _milliseconds();

        private static long MonotonicMilliseconds()
        {
            long timestamp = Stopwatch.GetTimestamp();
            return timestamp / Stopwatch.Frequency * 1000
                + timestamp % Stopwatch.Frequency * 1000 / Stopwatch.Frequency;
        }

        private static bool Elapsed(long now, long since, int timeout)
            => now - since >= timeout;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SteamTransport));
        }

        private static byte[] CreateHello(byte kind, byte[] nonce)
        {
            byte[] packet = new byte[HelloSize];
            WriteHeader(packet, kind);
            Buffer.BlockCopy(nonce, 0, packet, HeaderSize, nonce.Length);
            return packet;
        }

        private static void WriteHeader(byte[] packet, byte kind)
        {
            packet[0] = (byte)'P';
            packet[1] = (byte)'C';
            packet[2] = (byte)'8';
            packet[3] = 0;
            packet[4] = kind;
            packet[5] = (byte)Protocol.Version;
            packet[6] = 0;
            packet[7] = 0;
        }

        private static bool ValidHeader(byte[] packet, out byte kind)
        {
            kind = 0;
            if (packet == null || packet.Length < HeaderSize || packet[0] != (byte)'P'
                || packet[1] != (byte)'C' || packet[2] != (byte)'8' || packet[3] != 0
                || packet[5] != (byte)Protocol.Version || packet[6] != 0 || packet[7] != 0) return false;
            kind = packet[4];
            return kind == ClientHelloKind || kind == ServerHelloKind || kind == KeepaliveKind;
        }

        private static bool LooksLikeClientHello(byte[] packet)
        {
            byte kind;
            return packet.Length == HelloSize && ValidHeader(packet, out kind) && kind == ClientHelloKind;
        }

        private static bool NonceEquals(byte[] packet, byte[] nonce)
        {
            if (nonce == null || nonce.Length != 16 || packet.Length != HelloSize) return false;
            int difference = 0;
            for (int i = 0; i < nonce.Length; i++) difference |= packet[HeaderSize + i] ^ nonce[i];
            return difference == 0;
        }

        private enum LinkStage
        {
            AwaitingClientHello,
            ServerHelloQueued,
            AwaitingServerHello,
            Connected,
        }

        private sealed class Link
        {
            public readonly int Id;
            public readonly CSteamID Remote;
            public readonly long Created;
            public readonly Queue<OutgoingPacket> Outgoing = new Queue<OutgoingPacket>();
            public LinkStage Stage;
            public byte[] Nonce;
            public long LastReceived;
            public long LastSend;
            public long LastKeepaliveQueued;
            public int OutgoingBytes;
            public int OutgoingMessages;
            public int QueuedIncomingBytes;
            public int QueuedIncomingMessages;
            public bool Closed;

            public Link(int id, CSteamID remote, long now, LinkStage stage)
            {
                Id = id;
                Remote = remote;
                Created = now;
                LastReceived = now;
                LastSend = now;
                LastKeepaliveQueued = now;
                Stage = stage;
            }
        }

        private sealed class OutgoingPacket
        {
            public byte[] Data;
            public int Channel;
            public bool IsServerHello;
        }

        private sealed class IncomingPacket
        {
            public CSteamID Remote;
            public int Channel;
            public byte[] Data;
        }

        private sealed class QueuedEvent
        {
            public TransportEvent Event;
            public Link Source;
            public int IncomingBytes;
        }
    }
}
