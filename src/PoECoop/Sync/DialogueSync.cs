using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using OEIFormats.FlowCharts;
using OEIFormats.FlowCharts.Conversations;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates the settled, active HUD conversation. Responses are identified by
    /// node ID; guests send intent and only the host runs the native conversation.
    /// Every DialogueState is a complete snapshot for one world epoch.
    /// </summary>
    public sealed class DialogueSync : IDisposable
    {
        public enum CommandKind : byte { PickResponse = 1, Continue = 2 }

        public const FlowDomain Domain = FlowDomain.Dialogue;
        private const int MaxResponses = 64;
        private const int MaxSnapshotBytes = 256 * 1024;
        private const int MaxFilenameBytes = 512;
        private const int MaxTextBytes = 64 * 1024;
        private const int MaxResponseTextBytes = 16 * 1024;
        private const int MaxQualifierBytes = 4 * 1024;
        private const int MaxNodeId = 1000000;
        private const int MaxPortrait = 1024;
        private const float MaxTimer = 86400f;
        private const float RetryInterval = 0.25f;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly MethodInfo PlayerInputMethod =
            AccessTools.Method(typeof(UIConversationManager), "PlayerInput", new[] { typeof(int) });
        private static readonly MethodInfo ContinueMethod =
            AccessTools.Method(typeof(UIConversationManager), "Continue");
        private static readonly FieldInfo OwnershipInstance =
            AccessTools.Field(typeof(Ownership), "s_instance");

        private static DialogueSync s_instance;
        internal static DialogueSync Instance => s_instance;
        private static GameObject s_useActor;
        private static FlowChartPlayer s_chainSource;

        private readonly Session _session;
        private readonly DialoguePresentation _presentation;
        private readonly Dictionary<FlowChartPlayer, HostConversation> _hostConversations =
            new Dictionary<FlowChartPlayer, HostConversation>();
        private readonly RequestLedger _requests = new RequestLedger();

        private long _generation;
        private bool _wasHost, _wasGuest;
        private long _worldEpoch;
        private bool _worldReady;
        private long _sequence;
        private int _nextConversationId = 1;
        private bool _capturingNext;
        private HostConversation _active;
        private Snapshot _lastSnapshot;
        private float _nextCaptureWarning;

        private long _guestEpoch, _guestSequence, _guestAppliedSequence;
        private Snapshot _guestLatest;
        private float _nextRetry;
        private long _nextRequestId = 1;

        internal sealed class ResponseState
        {
            internal int NodeId;
            internal bool Qualified;
            internal string Text;
            internal string Qualifier;
        }

        internal sealed class Snapshot
        {
            internal long WorldEpoch;
            internal long Sequence;
            internal bool Active;
            internal int ConversationId;
            internal string Filename;
            internal Guid OwnerId;
            internal int PickerId;
            internal FlowChartPlayer.DisplayMode DisplayMode;
            internal int CurrentNodeId;
            internal string Text;
            internal readonly List<ResponseState> Responses = new List<ResponseState>();
            internal int NextNodeId;
            internal byte VoState;
            internal float Timer;
            internal bool FadeFromBlackOnExit;
            internal int Portrait;
            internal bool Visible;
            internal bool Ending;

            internal bool SameContent(Snapshot other)
            {
                if (other == null || Active != other.Active) return false;
                if (!Active) return true;
                if (ConversationId != other.ConversationId || Filename != other.Filename
                    || OwnerId != other.OwnerId || PickerId != other.PickerId
                    || DisplayMode != other.DisplayMode || CurrentNodeId != other.CurrentNodeId
                    || Text != other.Text || NextNodeId != other.NextNodeId || VoState != other.VoState
                    || FadeFromBlackOnExit != other.FadeFromBlackOnExit || Portrait != other.Portrait
                    || Visible != other.Visible || Ending != other.Ending
                    || Responses.Count != other.Responses.Count) return false;
                for (int i = 0; i < Responses.Count; i++)
                {
                    ResponseState a = Responses[i], b = other.Responses[i];
                    if (a.NodeId != b.NodeId || a.Qualified != b.Qualified
                        || a.Text != b.Text || a.Qualifier != b.Qualifier) return false;
                }
                return true;
            }
        }

        private sealed class HostConversation
        {
            internal FlowChartPlayer Player;
            internal int Id;
            internal int PickerId;
            internal Guid UseActorId;
            internal int NextNodeId = -1;
            internal int NextNodeCapturedAt = -1;
            internal bool Ended;
        }

        public DialogueSync(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _generation = session.Generation;
            _presentation = new DialoguePresentation(session);
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.WorldStateReady += OnWorldStateReady;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelLoaded;
            GameState.OnLevelUnload += OnLevelUnload;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.WorldStateReady -= OnWorldStateReady;
            _session.FullStateRequested -= OnFullStateRequested;
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelUnload;
            _presentation.Dispose();
            _hostConversations.Clear();
            _requests.Clear();
            _guestLatest = null;
            _lastSnapshot = null;
            s_useActor = null;
            s_chainSource = null;
            if (s_instance == this) s_instance = null;
        }

        public static long CurrentWorldEpoch
        {
            get
            {
                DialogueSync self = s_instance;
                if (self == null) return 0;
                return self._session.IsGuest ? self._guestEpoch : self._worldEpoch;
            }
        }

        public static bool ApplyingHostState => DialoguePresentation.ApplyingHostState;
        internal static bool CanLocalInput
        {
            get
            {
                DialogueSync self = s_instance;
                return self == null || !self._session.IsHost || self._active == null
                    || self._active.PickerId == Session.HostId || DialoguePresentation.ExecutingHostCommand;
            }
        }

        public static void SendLatestTo(int playerId)
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsHost || playerId == Session.HostId) return;
            self.SyncContext();
            if (!self._worldReady || !NativeWorldReady()) return;
            if (self._lastSnapshot == null) self.CaptureSettled();
            if (self._lastSnapshot == null) return;
            if (playerId < 0) self.SendSnapshot(self._lastSnapshot, Session.HostId);
            else if (self._session.IsPeerReady(playerId)) self.SendSnapshot(self._lastSnapshot, playerId);
        }

        public void Update(float now)
        {
            SyncContext();
            if (_session.IsHost)
            {
                if (_worldReady && NativeWorldReady()) CaptureSettled();
            }
            else if (_session.IsGuest)
            {
                _presentation.Update(now);
                if (_worldReady && NativeWorldReady() && _guestLatest != null
                    && _guestLatest.WorldEpoch == _guestEpoch)
                {
                    bool current = _guestAppliedSequence == _guestLatest.Sequence
                        && (!_guestLatest.Active || _presentation.Matches(_guestLatest));
                    bool retry = _guestAppliedSequence != _guestLatest.Sequence || now >= _nextRetry;
                    if (!current && retry)
                    {
                        _nextRetry = now + RetryInterval;
                        if (_presentation.Apply(_guestLatest)) _guestAppliedSequence = _guestLatest.Sequence;
                    }
                }
            }
        }

        private void SyncContext()
        {
            bool host = _session.IsHost, guest = _session.IsGuest;
            if (_generation != _session.Generation || host != _wasHost || guest != _wasGuest)
            {
                bool restoreFade = _wasGuest;
                _generation = _session.Generation;
                _wasHost = host;
                _wasGuest = guest;
                _nextRequestId = 1;
                _presentation.Reset(restoreFade);
                _guestLatest = null;
                _guestEpoch = 0;
                _guestSequence = 0;
                _guestAppliedSequence = 0;
                _nextRetry = 0f;
                _worldEpoch = 0;
                ResetHostWorld();
            }

            long epoch = FlowCoordinator.WorldEpoch;
            if (_worldEpoch != epoch) ResetWorld(epoch);
            bool ready = epoch > 0 && _session.IsWorldReady && !FlowCoordinator.ApplyingWorldBarrier;
            _worldReady = ready;
        }

        private void ResetWorld(long epoch)
        {
            _worldEpoch = epoch;
            _presentation.Reset(false);
            _guestLatest = null;
            _guestEpoch = _session.IsGuest ? epoch : 0;
            _guestSequence = 0;
            _guestAppliedSequence = 0;
            _nextRetry = 0f;
            ResetHostWorld();
        }

        private void ResetHostWorld()
        {
            _hostConversations.Clear();
            _requests.Clear();
            _active = null;
            _lastSnapshot = null;
            _sequence = 0;
            _nextConversationId = 1;
            
            s_useActor = null;
            s_chainSource = null;
        }

        private static bool NativeWorldReady()
        {
            return GameState.IsInGameSession && !GameState.IsLoading && ConversationManager.Instance != null;
        }

        private void OnLevelUnload(object sender, EventArgs e)
        {
            _worldReady = false;
            if (_session.IsGuest) _presentation.Reset(false);
            if (_session.IsHost)
            {
                _hostConversations.Clear();
                _active = null;
                
            }
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            if (_session.IsGuest) _presentation.Reset(false);
            _guestAppliedSequence = 0;
            
        }

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            SyncContext();
            if (_session.IsHost && player != null) SendLatestTo(player.Id);
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            if (worldEpoch == FlowCoordinator.WorldEpoch) SendLatestTo(playerId);
        }

        // ---- native host capture ----

        internal static GameObject CaptureUse(GameObject user)
        {
            GameObject previous = s_useActor;
            if (s_instance != null && s_instance._session.IsHost) s_useActor = user;
            return previous;
        }

        internal static void ReleaseUse(GameObject previous)
        {
            s_useActor = previous;
        }

        internal static FlowChartPlayer BeginNativeMove(FlowChartPlayer player)
        {
            FlowChartPlayer previous = s_chainSource;
            s_chainSource = player;
            return previous;
        }

        internal static void EndNativeMove(FlowChartPlayer previous)
        {
            s_chainSource = previous;
        }

        internal static void ConversationStarted(FlowChartPlayer player)
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsHost || !IsHudPlayer(player)) return;
            HostConversation record = self.EnsureHostConversation(player);
            if (record == null) return;
            if (s_useActor != null)
            {
                record.UseActorId = EntityRegistry.NetworkId(s_useActor);
                record.PickerId = OwnerOf(s_useActor);
            }
            else if (s_chainSource != null && s_chainSource != player
                && self._hostConversations.TryGetValue(s_chainSource, out HostConversation parent))
            {
                record.UseActorId = parent.UseActorId;
                record.PickerId = parent.PickerId;
            }
            record.Ended = false;
            
        }

        internal static void ConversationMoved(FlowChartPlayer player)
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsHost || player == null) return;
            if (!self._hostConversations.ContainsKey(player) && IsHudPlayer(player))
                self.EnsureHostConversation(player);
            if (self._hostConversations.ContainsKey(player))
            {
                self._hostConversations[player].Ended = false;
                
            }
        }

        internal static void ConversationEnded(FlowChartPlayer player)
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsHost || player == null) return;
            if (self._hostConversations.TryGetValue(player, out HostConversation record))
            {
                record.Ended = true;
                
            }
        }

        private HostConversation EnsureHostConversation(FlowChartPlayer player)
        {
            if (player == null) return null;
            if (_hostConversations.TryGetValue(player, out HostConversation existing)) return existing;
            var record = new HostConversation
            {
                Player = player,
                Id = NextConversationId(),
                PickerId = Session.HostId,
            };
            if (s_chainSource != null && s_chainSource != player
                && _hostConversations.TryGetValue(s_chainSource, out HostConversation parent))
            {
                record.PickerId = parent.PickerId;
                record.UseActorId = parent.UseActorId;
            }
            _hostConversations.Add(player, record);
            return record;
        }

        private int NextConversationId()
        {
            int start = _nextConversationId;
            do
            {
                int id = _nextConversationId;
                _nextConversationId = NextPositive(_nextConversationId);
                bool used = false;
                foreach (HostConversation record in _hostConversations.Values)
                    if (record.Id == id) { used = true; break; }
                if (!used) return id;
            } while (_nextConversationId != start);
            throw new InvalidOperationException("No dialogue conversation IDs remain");
        }

        private void CaptureSettled()
        {
            FlowChartPlayer player = ConversationManager.Instance.GetActiveConversationForHUD();
            if (!IsHudPlayer(player))
            {
                Publish(InactiveSnapshot());
                _active = null;
                _hostConversations.Clear();
                
                return;
            }

            HostConversation record = EnsureHostConversation(player);
            _active = record;
            Snapshot snapshot = Capture(record);
            if (snapshot != null) Publish(snapshot);
            

            List<FlowChartPlayer> stale = null;
            foreach (var pair in _hostConversations)
                if (pair.Key != player && (pair.Value.Ended || !ConversationManager.Instance.IsConversationActive(pair.Key)))
                    (stale ?? (stale = new List<FlowChartPlayer>())).Add(pair.Key);
            if (stale != null) foreach (FlowChartPlayer old in stale) _hostConversations.Remove(old);
        }

        private Snapshot Capture(HostConversation record)
        {
            FlowChartPlayer player = record.Player;
            var conversation = player.CurrentFlowChart as Conversation;
            if (conversation == null) return null;
            string filename = CanonicalFilename(conversation.Filename);
            if (!ValidFilename(filename)) return CaptureFailed("invalid conversation filename");
            Guid ownerId = player.OwnerObject != null ? EntityRegistry.NetworkId(player.OwnerObject) : Guid.Empty;
            if (player.OwnerObject != null && ownerId == Guid.Empty)
                return CaptureFailed("conversation owner has no network identity");

            var snapshot = new Snapshot
            {
                Active = true,
                ConversationId = record.Id,
                Filename = filename,
                OwnerId = ownerId,
                PickerId = record.PickerId,
                DisplayMode = player.FlowChartDisplayMode,
                CurrentNodeId = player.CurrentNodeID,
                FadeFromBlackOnExit = player.FadeFromBlackOnExit,
                Portrait = -1,
            };
            try
            {
                snapshot.Text = conversation.GetActiveNodeText(player) ?? "";
                List<PlayerResponseNode> responses = conversation.GetResponseNodes(player);
                if (responses.Count > MaxResponses) return CaptureFailed("too many responses");
                int qualified = 0;
                foreach (PlayerResponseNode response in responses)
                {
                    if (response == null) return CaptureFailed("null response");
                    bool passes = conversation.PassesConditionalsEx(response, player);
                    if (passes) qualified++;
                    snapshot.Responses.Add(new ResponseState
                    {
                        NodeId = response.NodeID,
                        Qualified = passes,
                        Text = conversation.GetNodeText(player, response) ?? "",
                        Qualifier = conversation.GetNodeQualifier(response, player) ?? "",
                    });
                }
                snapshot.NextNodeId = -1;
                record.NextNodeId = -1;
                record.NextNodeCapturedAt = player.CurrentNodeID;
                if (qualified == 0)
                {
                    _capturingNext = true;
                    FlowChartNode next;
                    try { next = conversation.GetNextNode(player); }
                    finally { _capturingNext = false; }
                    snapshot.NextNodeId = next != null ? next.NodeID : -1;
                    record.NextNodeId = snapshot.NextNodeId;
                }
                snapshot.VoState = player.DisableVO ? (byte)0 : conversation.IsVOPlaying(player) ? (byte)1 : (byte)2;
                snapshot.Timer = player.Timer;
                if (player.FlowChartDisplayMode == FlowChartPlayer.DisplayMode.Interaction && player.OwnerObject != null)
                {
                    ScriptedInteraction interaction = player.OwnerObject.GetComponent<ScriptedInteraction>();
                    if (interaction != null) snapshot.Portrait = DialoguePresentation.PortraitOf(interaction);
                }
                DialoguePresentation.ReadHostVisibility(player, out snapshot.Visible, out snapshot.Ending);
                return ValidateSnapshot(snapshot) ? snapshot : CaptureFailed("snapshot exceeds a protocol bound");
            }
            catch (Exception ex)
            {
                WarnCapture("Could not capture dialogue: " + ex.Message);
                return null;
            }
        }

        private Snapshot CaptureFailed(string reason)
        {
            WarnCapture("Dialogue snapshot not sent: " + reason);
            return null;
        }

        private void WarnCapture(string text)
        {
            float now = Time.unscaledTime;
            if (now < _nextCaptureWarning) return;
            _nextCaptureWarning = now + 2f;
            _session.Note(text);
        }

        private static bool IsHudPlayer(FlowChartPlayer player)
        {
            if (player == null || player.Completed || player.CurrentFlowChart is Conversation == false) return false;
            if (player.FlowChartDisplayMode != FlowChartPlayer.DisplayMode.Standard
                && player.FlowChartDisplayMode != FlowChartPlayer.DisplayMode.Interaction) return false;
            FlowChartNode node = player.CurrentFlowChart.GetNode(player.CurrentNodeID);
            return node is DialogueNode dialogue && dialogue.DisplayType == DisplayType.Conversation;
        }

        internal static bool TryGetHostNextNode(FlowChartPlayer player, out int nodeId)
        {
            DialogueSync self = s_instance;
            nodeId = -1;
            if (self == null || !self._session.IsHost || self._capturingNext || player == null) return false;
            if (self._active == null || self._active.Player != player
                || self._active.NextNodeCapturedAt != player.CurrentNodeID) return false;
            nodeId = self._active.NextNodeId;
            return true;
        }

        private static int OwnerOf(GameObject actor)
        {
            PartyMemberAI member = actor != null ? actor.GetComponent<PartyMemberAI>() : null;
            if (member == null) return Session.HostId;
            Ownership ownership = OwnershipInstance != null ? OwnershipInstance.GetValue(null) as Ownership : null;
            return ownership != null ? ownership.OwnerOf(member) : Session.HostId;
        }

        // ---- host wire and commands ----

        private void Publish(Snapshot snapshot)
        {
            snapshot.WorldEpoch = _worldEpoch;
            if (_lastSnapshot != null && snapshot.SameContent(_lastSnapshot)) return;
            snapshot.Sequence = NextSequence();
            _lastSnapshot = snapshot;
            SendSnapshot(snapshot, Session.HostId);
        }

        private long NextSequence()
        {
            if (_sequence == long.MaxValue) throw new InvalidOperationException("Dialogue state sequence exhausted");
            return ++_sequence;
        }

        private Snapshot InactiveSnapshot()
        {
            return new Snapshot { Active = false, Filename = "", Text = "", NextNodeId = -1, Portrait = -1 };
        }

        private void SendSnapshot(Snapshot snapshot, int targetedPlayer)
        {
            byte[] data = Serialize(snapshot);
            if (targetedPlayer != Session.HostId)
            {
                if (_session.IsPeerReady(targetedPlayer)) _session.SendToPlayer(targetedPlayer, data);
                return;
            }
            foreach (PlayerInfo player in _session.Players)
                if (!player.IsHost && _session.IsPeerReady(player.Id)) _session.SendToPlayer(player.Id, data);
        }

        private static byte[] Serialize(Snapshot snapshot)
        {
            MessageWriter writer = FlowWire.WriteSnapshot(MessageType.DialogueState,
                snapshot.WorldEpoch, snapshot.Sequence, true, snapshot.Active ? 1 : 0);
            if (snapshot.Active)
            {
                writer.Int(snapshot.ConversationId).String(snapshot.Filename).Guid(snapshot.OwnerId)
                    .Int(snapshot.PickerId).Byte((byte)snapshot.DisplayMode).Int(snapshot.CurrentNodeId)
                    .String(snapshot.Text).Int(snapshot.Responses.Count);
                foreach (ResponseState response in snapshot.Responses)
                    writer.Int(response.NodeId).Byte((byte)(response.Qualified ? 1 : 0))
                        .String(response.Text).String(response.Qualifier);
                writer.Int(snapshot.NextNodeId).Byte(snapshot.VoState).Float(snapshot.Timer)
                    .Byte((byte)(snapshot.FadeFromBlackOnExit ? 1 : 0)).Int(snapshot.Portrait)
                    .Byte((byte)(snapshot.Visible ? 1 : 0)).Byte((byte)(snapshot.Ending ? 1 : 0));
            }
            byte[] data = writer.ToArray();
            if (data.Length > MaxSnapshotBytes) throw new InvalidOperationException("dialogue snapshot exceeds 256 KiB");
            return data;
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (reader.Type != MessageType.DialogueState || sender != null || !_session.IsGuest) return;
            Snapshot snapshot = ReadSnapshot(reader);
            reader.Finish();
            if (_guestEpoch != 0 && snapshot.WorldEpoch < _guestEpoch) return;
            if (_guestEpoch == snapshot.WorldEpoch && snapshot.Sequence <= _guestSequence) return;
            if (_guestEpoch != snapshot.WorldEpoch)
            {
                _presentation.Reset(false);
                _guestSequence = 0;
                _guestAppliedSequence = 0;
            }
            _guestEpoch = snapshot.WorldEpoch;
            _guestSequence = snapshot.Sequence;
            _guestLatest = snapshot;
            _nextRetry = 0f;
        }

        private static Snapshot ReadSnapshot(MessageReader reader)
        {
            FlowSnapshotHeader header = FlowWire.ReadSnapshot(reader, MessageType.DialogueState, 1);
            if (!header.Full) throw new FormatException("dialogue snapshots must be full");
            var snapshot = new Snapshot
            {
                WorldEpoch = header.WorldEpoch,
                Sequence = header.StateSeq,
                Active = header.Count != 0,
                Filename = "",
                Text = "",
                NextNodeId = -1,
                Portrait = -1,
            };
            if (snapshot.Sequence <= 0) throw new FormatException("dialogue sequence");
            if (!snapshot.Active) return snapshot;

            snapshot.ConversationId = reader.Int();
            snapshot.Filename = ReadString(reader, MaxFilenameBytes, "filename");
            snapshot.OwnerId = reader.Guid();
            snapshot.PickerId = reader.Int();
            snapshot.DisplayMode = (FlowChartPlayer.DisplayMode)reader.Byte();
            snapshot.CurrentNodeId = reader.Int();
            snapshot.Text = ReadString(reader, MaxTextBytes, "text");
            int responses = reader.Int();
            if (snapshot.ConversationId <= 0 || !ValidFilename(snapshot.Filename)
                || snapshot.PickerId < 0 || snapshot.PickerId > 64
                || snapshot.DisplayMode != FlowChartPlayer.DisplayMode.Standard
                && snapshot.DisplayMode != FlowChartPlayer.DisplayMode.Interaction
                || !ValidNode(snapshot.CurrentNodeId) || responses < 0 || responses > MaxResponses)
                throw new FormatException("dialogue snapshot header");
            var ids = new HashSet<int>();
            int approximateBytes = MaxFilenameBytes + MaxTextBytes;
            for (int i = 0; i < responses; i++)
            {
                var response = new ResponseState
                {
                    NodeId = reader.Int(),
                    Qualified = ReadBool(reader),
                    Text = ReadString(reader, MaxResponseTextBytes, "response"),
                    Qualifier = ReadString(reader, MaxQualifierBytes, "qualifier"),
                };
                if (!ValidNode(response.NodeId) || !ids.Add(response.NodeId))
                    throw new FormatException("dialogue response node");
                approximateBytes += StrictUtf8.GetByteCount(response.Text) + StrictUtf8.GetByteCount(response.Qualifier);
                if (approximateBytes > MaxSnapshotBytes) throw new FormatException("dialogue snapshot is too large");
                snapshot.Responses.Add(response);
            }
            snapshot.NextNodeId = reader.Int();
            snapshot.VoState = reader.Byte();
            snapshot.Timer = reader.Float();
            snapshot.FadeFromBlackOnExit = ReadBool(reader);
            snapshot.Portrait = reader.Int();
            snapshot.Visible = ReadBool(reader);
            snapshot.Ending = ReadBool(reader);
            if (snapshot.NextNodeId < -1 || snapshot.NextNodeId > MaxNodeId || snapshot.VoState > 2
                || !Finite(snapshot.Timer) || snapshot.Timer < 0f || snapshot.Timer > MaxTimer
                || snapshot.Portrait < -1 || snapshot.Portrait > MaxPortrait)
                throw new FormatException("dialogue node presentation state");
            return snapshot;
        }

        private static bool ValidateSnapshot(Snapshot snapshot)
        {
            if (!ValidNode(snapshot.CurrentNodeId) || snapshot.NextNodeId < -1 || snapshot.NextNodeId > MaxNodeId
                || snapshot.Portrait < -1 || snapshot.Portrait > MaxPortrait
                || !Finite(snapshot.Timer) || snapshot.Timer < 0f || snapshot.Timer > MaxTimer
                || snapshot.PickerId < 0 || snapshot.PickerId > 64) return false;
            if (!ValidString(snapshot.Filename, MaxFilenameBytes) || !ValidString(snapshot.Text, MaxTextBytes)) return false;
            int approximateBytes = StrictUtf8.GetByteCount(snapshot.Filename) + StrictUtf8.GetByteCount(snapshot.Text);
            var ids = new HashSet<int>();
            foreach (ResponseState response in snapshot.Responses)
            {
                if (!ValidNode(response.NodeId) || !ids.Add(response.NodeId)
                    || !ValidString(response.Text, MaxResponseTextBytes)
                    || !ValidString(response.Qualifier, MaxQualifierBytes)) return false;
                approximateBytes += StrictUtf8.GetByteCount(response.Text) + StrictUtf8.GetByteCount(response.Qualifier) + 16;
                if (approximateBytes > MaxSnapshotBytes) return false;
            }
            return true;
        }

        /// <summary>Called by FlowCoordinator after it reads and validates the Dialogue envelope.</summary>
        public void HandleFlowCommand(PlayerInfo sender, long worldEpoch, long requestId, int rawKind, MessageReader reader)
        {
            SyncContext();
            if (sender == null || !_session.IsHost) throw new FormatException("dialogue command sender");
            CommandKind kind = (CommandKind)rawKind;
            if (kind != CommandKind.PickResponse && kind != CommandKind.Continue)
                throw new FormatException("dialogue command kind " + rawKind);
            var command = new FlowCommandEnvelope
            {
                WorldEpoch = worldEpoch,
                RequestId = requestId,
                Domain = Domain,
                Kind = rawKind,
            };
            RequestLookup lookup = _requests.Begin(sender.Id, command, reader);
            if (lookup.Status == RequestLookupStatus.Duplicate)
            {
                if (lookup.HasResult)
                    SendResult(sender, command, lookup.Result, lookup.StateSeq);
                return;
            }
            if (lookup.Status == RequestLookupStatus.Conflict)
            {
                SendResult(sender, command, FlowResultCode.Conflict, _sequence);
                return;
            }

            int conversationId = reader.Int();
            long revision = reader.Long();
            int currentNode = reader.Int();
            int responseNode = reader.Int();
            reader.Finish();
            if (conversationId <= 0 || revision <= 0 || !ValidNode(currentNode)
                || responseNode < -1 || responseNode > MaxNodeId)
            {
                Complete(sender, command, FlowResultCode.Invalid);
                return;
            }
            if (worldEpoch != _worldEpoch)
            {
                Complete(sender, command, FlowResultCode.StaleWorld);
                SendLatestTo(sender.Id);
                return;
            }
            if (!_worldReady || !NativeWorldReady() || !_session.IsPeerReady(sender.Id))
            {
                Complete(sender, command, FlowResultCode.NotReady);
                return;
            }

            CaptureSettled();
            HostConversation active = _active;
            Snapshot state = _lastSnapshot;
            if (active == null || state == null || !state.Active || active.Id != conversationId
                || state.Sequence != revision || active.Player.CurrentNodeID != currentNode)
            {
                Complete(sender, command, FlowResultCode.Conflict);
                SendLatestTo(sender.Id);
                return;
            }
            if (active.PickerId != sender.Id)
            {
                Complete(sender, command, FlowResultCode.Rejected);
                return;
            }
            if (!state.Visible || state.Ending || !DialoguePresentation.HostUiMatches(active.Player))
            {
                Complete(sender, command, FlowResultCode.NotReady);
                return;
            }

            FlowResultCode validation;
            int index = -1;
            if (kind == CommandKind.PickResponse)
                validation = DialoguePresentation.TryHostResponseIndex(active.Player, state, responseNode, out index)
                    ? FlowResultCode.Ok : FlowResultCode.Rejected;
            else
                validation = responseNode == -1 && state.Responses.TrueForAll(response => !response.Qualified)
                    ? FlowResultCode.Ok : FlowResultCode.Invalid;
            if (validation != FlowResultCode.Ok)
            {
                Complete(sender, command, validation);
                return;
            }

            try
            {
                if (kind == CommandKind.PickResponse) DialoguePresentation.HostPlayerInput(PlayerInputMethod, index);
                else DialoguePresentation.HostContinue(ContinueMethod);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Guest dialogue command failed: " + ex.Message);
                Complete(sender, command, FlowResultCode.Failed);
                return;
            }
            CaptureSettled();
            Complete(sender, command, FlowResultCode.Ok);
        }

        private void Complete(PlayerInfo sender, FlowCommandEnvelope command, FlowResultCode result)
        {
            _requests.Complete(sender.Id, command, result, _sequence);
            SendResult(sender, command, result, _sequence);
        }

        private void SendResult(PlayerInfo sender, FlowCommandEnvelope command, FlowResultCode result, long stateSequence)
        {
            _session.SendToPlayer(sender.Id, FlowWire.WriteResult(command.WorldEpoch, command.RequestId,
                Domain, command.Kind, result, stateSequence).ToArray());
        }

        // ---- guest commands and results ----

        internal static void LocalPlayerInput(int index)
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsGuest) return;
            Snapshot state = self.CommandState();
            if (state == null || index < 0) return;
            if (!state.Responses.Exists(response => response.Qualified))
            {
                if (index == 0 && state.DisplayMode == FlowChartPlayer.DisplayMode.Interaction)
                    self.SendCommand(CommandKind.Continue, state, -1);
                return;
            }
            if (index >= state.Responses.Count) return;
            ResponseState selected = state.Responses[index];
            if (!selected.Qualified) return;
            self.SendCommand(CommandKind.PickResponse, state, selected.NodeId);
        }

        internal static void LocalContinue()
        {
            DialogueSync self = s_instance;
            if (self == null || !self._session.IsGuest) return;
            Snapshot state = self.CommandState();
            if (state == null || state.Responses.Exists(response => response.Qualified)) return;
            self.SendCommand(CommandKind.Continue, state, -1);
        }

        private Snapshot CommandState()
        {
            Snapshot state = _guestLatest;
            if (state == null || !state.Active || !state.Visible || state.Ending || !_worldReady
                || state.WorldEpoch != FlowCoordinator.WorldEpoch || state.PickerId != _session.LocalId
                || _guestAppliedSequence != state.Sequence || !_presentation.Matches(state)) return null;
            return state;
        }

        private void SendCommand(CommandKind kind, Snapshot state, int responseNode)
        {
            if (_nextRequestId <= 0) return;
            long requestId = _nextRequestId++;
            if (_nextRequestId <= 0) _nextRequestId = 0;
            _session.SendToHost(FlowWire.WriteCommand(state.WorldEpoch, requestId, Domain, (int)kind)
                .Int(state.ConversationId).Long(state.Sequence).Int(state.CurrentNodeId).Int(responseNode).ToArray());
        }

        /// <summary>Called by FlowCoordinator after it reads and validates a Dialogue result.</summary>
        public void HandleFlowResult(long worldEpoch, long requestId, int rawKind,
            FlowResultCode result, long stateSequence)
        {
            SyncContext();
            if (!_session.IsGuest || worldEpoch != FlowCoordinator.WorldEpoch) return;
            CommandKind kind = (CommandKind)rawKind;
            if (kind != CommandKind.PickResponse && kind != CommandKind.Continue || stateSequence < 0) return;
            if (result != FlowResultCode.Ok && result != FlowResultCode.StaleWorld
                && result != FlowResultCode.Conflict)
                _session.Note("Dialogue command was not accepted: " + result + ".");
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost && !player.IsHost)
            {
                
                SendLatestTo(player.Id);
            }
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            _requests.RemovePlayer(player.Id);
            if (!_session.IsHost) return;
            foreach (HostConversation record in _hostConversations.Values)
                if (record.PickerId == player.Id)
                {
                    record.PickerId = Session.HostId;
                    record.UseActorId = Guid.Empty;
                    
                }
        }

        private static string CanonicalFilename(string filename)
        {
            filename = (filename ?? "").Replace('\\', '/').ToLowerInvariant();
            if (filename.StartsWith("assets/", StringComparison.Ordinal)) filename = filename.Substring(7);
            return filename;
        }

        private static bool ValidFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename) || filename.Length > MaxFilenameBytes
                || filename.IndexOf('\0') >= 0 || filename.StartsWith("/", StringComparison.Ordinal)
                || filename.Contains("../") || filename.Contains("..\\") || filename == "..") return false;
            foreach (char c in filename) if (char.IsControl(c)) return false;
            return true;
        }

        private static string ReadString(MessageReader reader, int maxBytes, string which)
        {
            string value = reader.String();
            if (StrictUtf8.GetByteCount(value) > maxBytes) throw new FormatException("dialogue " + which + " is too long");
            return value;
        }

        private static bool ValidString(string value, int maxBytes)
        {
            return value != null && StrictUtf8.GetByteCount(value) <= maxBytes;
        }

        private static bool ReadBool(MessageReader reader)
        {
            byte value = reader.Byte();
            if (value > 1) throw new FormatException("dialogue boolean " + value);
            return value != 0;
        }

        private static bool ValidNode(int id) => id >= 0 && id <= MaxNodeId;
        private static int NextPositive(int value) => value <= 0 || value == int.MaxValue ? 1 : value + 1;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
