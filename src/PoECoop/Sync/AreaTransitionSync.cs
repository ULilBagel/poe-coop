using System;
using System.Collections.Generic;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Guest area requests and host-authoritative native transitions. Cross-scene
    /// loads continue through SaveHandoff; same-map teleports send an immediate
    /// position snapshot followed by a fixed-size FlowResult notification.
    /// FlowCoordinator parses the common flow envelope and routes Area payloads here.
    /// </summary>
    public sealed class AreaTransitionSync : IDisposable
    {
        public const FlowDomain Domain = FlowDomain.Area;

        private enum Kind
        {
            SceneTransition = 1,
            WorldMapTravel = 2,
            SameMapTeleport = 3,
        }

        private const int MaxSceneNameLength = FlowWire.MaxSceneLength;
        private const int MaxReceipts = 256;
        private const long SameMapNotificationId = long.MaxValue;
        private const float RequestTimeoutSeconds = 15f;
        private const float MaxCoordinate = 100000f;

        private static AreaTransitionSync s_instance;
        internal static AreaTransitionSync Instance => s_instance;
        private static int s_selectionDepth;
        private static SelectionState s_selectionState;

        private readonly Session _session;
        private readonly Dictionary<CommandKey, Receipt> _receipts = new Dictionary<CommandKey, Receipt>();
        private readonly Queue<CommandKey> _receiptOrder = new Queue<CommandKey>();
        private long _generation;
        private long _nextRequestId = 1;
        private long _observedEpoch;
        private long _stateSequence;
        private PendingRequest _pending;

        private sealed class PendingRequest
        {
            public long Id;
            public long Epoch;
            public Kind Kind;
            public float Deadline;
        }

        private sealed class Receipt
        {
            public Kind Kind;
            public Guid TransitionId;
            public string SceneName;
            public byte[] Data;
        }

        private struct CommandKey : IEquatable<CommandKey>
        {
            public int PlayerId;
            public long Epoch;
            public long RequestId;

            public bool Equals(CommandKey other)
            {
                return PlayerId == other.PlayerId && Epoch == other.Epoch && RequestId == other.RequestId;
            }

            public override bool Equals(object obj) => obj is CommandKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (((PlayerId * 397) ^ Epoch.GetHashCode()) * 397) ^ RequestId.GetHashCode();
                }
            }
        }

        private sealed class SelectionState
        {
            public PartyMemberAI[] Members;
            public bool[] Selected;
            public bool[] DragSelected;
        }

        private sealed class SelectionScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (s_selectionDepth <= 0) return;
                s_selectionDepth--;
                if (s_selectionDepth != 0) return;

                SelectionState state = s_selectionState;
                try
                {
                    if (state != null)
                    {
                        for (int i = 0; i < state.Members.Length; i++)
                        {
                            PartyMemberAI member = state.Members[i];
                            if (member == null) continue;
                            member.DragSelected = state.DragSelected[i];
                            member.Selected = state.Selected[i];
                        }
                    }
                }
                finally
                {
                    s_selectionState = null;
                    s_selectionDepth = 0;
                }
            }
        }

        public AreaTransitionSync(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _generation = session.Generation;
            s_instance = this;
            session.PlayerLeft += OnPlayerLeft;
            GameState.OnLevelLoaded += OnLevelLoaded;
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelLoaded;
            _session.PlayerLeft -= OnPlayerLeft;
            _pending = null;
            _receipts.Clear();
            _receiptOrder.Clear();
            if (s_instance == this) s_instance = null;
        }

        public void Update(float now)
        {
            long perf = PerfStats.Begin();
            UpdateCore(now);
            PerfStats.End("areatransition.update", perf);
        }

        private void UpdateCore(float now)
        {
            SyncSession();
            if (!_session.IsHost && !_session.IsGuest) return;

            long epoch = FlowCoordinator.WorldEpoch;
            if (epoch != _observedEpoch)
            {
                _observedEpoch = epoch;
                _stateSequence = 0;
                if (_pending != null && _pending.Epoch != epoch)
                {
                    Kind kind = _pending.Kind;
                    _pending = null;
                    if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
                }
            }

            if (_session.IsGuest && _pending != null && now >= _pending.Deadline)
            {
                Kind kind = _pending.Kind;
                _pending = null;
                _session.Note("The host did not answer the area transition request.");
                if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
            }
        }

        /// <summary>Called by FlowCoordinator after it reads and validates the Area command envelope.</summary>
        public void HandleFlowCommand(PlayerInfo sender, long worldEpoch, long requestId, int rawKind, MessageReader reader)
        {
            SyncSession();
            if (sender == null || !_session.IsHost) return;

            Kind kind = (Kind)rawKind;
            Guid transitionId = Guid.Empty;
            string sceneName = null;
            switch (kind)
            {
                case Kind.SceneTransition:
                    transitionId = reader.Guid();
                    break;
                case Kind.WorldMapTravel:
                    sceneName = reader.String();
                    if (sceneName.Length == 0 || sceneName.Length > MaxSceneNameLength)
                        throw new FormatException("invalid world-map scene name");
                    break;
                default:
                    throw new FormatException("area command kind " + rawKind);
            }
            reader.Finish();
            if (worldEpoch <= 0 || requestId <= 0) return;

            var key = new CommandKey { PlayerId = sender.Id, Epoch = worldEpoch, RequestId = requestId };
            if (_receipts.TryGetValue(key, out Receipt previous))
            {
                if (previous.Kind == kind && previous.TransitionId == transitionId && previous.SceneName == sceneName)
                    _session.SendToPlayer(sender.Id, previous.Data);
                else SendResult(sender.Id, worldEpoch, requestId, kind, FlowResultCode.Conflict, false);
                return;
            }

            if (worldEpoch != FlowCoordinator.WorldEpoch)
            {
                Complete(sender, requestId, worldEpoch, kind, FlowResultCode.StaleWorld, transitionId, sceneName);
                return;
            }

            FlowResultCode common = ValidateCommonTransition(sender.Id);
            if (common != FlowResultCode.Ok)
            {
                Complete(sender, requestId, worldEpoch, kind, common, transitionId, sceneName);
                return;
            }

            FlowResultCode result = kind == Kind.SceneTransition
                ? BeginSceneTransition(transitionId)
                : BeginWorldMapTravel(sceneName);
            Complete(sender, requestId, worldEpoch, kind, result, transitionId, sceneName);
        }

        /// <summary>Called by FlowCoordinator after it reads and validates an Area result envelope.</summary>
        public void HandleFlowResult(long worldEpoch, long requestId, int rawKind, FlowResultCode result, long stateSeq)
        {
            SyncSession();
            if (!_session.IsGuest || stateSeq < 0) return;

            Kind kind = (Kind)rawKind;
            if (kind == Kind.SameMapTeleport)
            {
                if (requestId != SameMapNotificationId || result != FlowResultCode.Ok
                    || worldEpoch != FlowCoordinator.WorldEpoch || !CanApplyHostNotification()) return;
                long perf = PerfStats.Begin();
                ApplySameMapTeleport();
                PerfStats.End("areatransition.apply", perf);
                return;
            }

            if (kind != Kind.SceneTransition && kind != Kind.WorldMapTravel)
                throw new FormatException("area result kind " + rawKind);

            PendingRequest pending = _pending;
            if (pending == null || pending.Id != requestId || pending.Epoch != worldEpoch || pending.Kind != kind) return;
            _pending = null;

            if (result == FlowResultCode.Ok)
            {
                if (kind == Kind.WorldMapTravel) CloseGuestWorldMap();
                return;
            }

            if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
            _session.Note(result == FlowResultCode.StaleWorld
                ? "The area changed before the host could accept that transition."
                : result == FlowResultCode.NotReady
                    ? "The host cannot transition during the current game state."
                    : "The host refused that area transition.");
        }

        internal static bool GuestUseTransition(SceneTransition transition)
        {
            if (!Plugin.IsGuest) return false;
            AreaTransitionSync self = s_instance;
            if (self == null || transition == null) return true;

            self.SyncSession();
            Guid transitionId = EntityId(transition.gameObject);
            if (transitionId == Guid.Empty)
            {
                self._session.Note("That transition has no persistent identity and cannot be requested.");
                return true;
            }
            self.QueueGuestCommand(Kind.SceneTransition, transitionId, null);
            return true;
        }

        internal static bool GuestWorldMapTravel(string mapName)
        {
            if (!Plugin.IsGuest) return false;
            AreaTransitionSync self = s_instance;
            if (self == null) return true;

            self.SyncSession();
            mapName = mapName ?? "";
            if (mapName.Length == 0 || mapName.Length > MaxSceneNameLength)
            {
                self._session.Note("That world-map destination is invalid.");
                RestoreGuestMapFade();
                return true;
            }
            self.QueueGuestCommand(Kind.WorldMapTravel, Guid.Empty, mapName);
            return true;
        }

        private void QueueGuestCommand(Kind kind, Guid transitionId, string sceneName)
        {
            if (!_session.IsGuest || !_session.IsWorldReady || !CanGuestRequest())
            {
                _session.Note("The host's world is not ready for an area transition.");
                if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
                return;
            }
            if (_pending != null)
            {
                _session.Note("An area transition is already waiting for the host.");
                if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
                return;
            }

            long requestId = NextRequestId();
            long epoch = FlowCoordinator.WorldEpoch;
            MessageWriter message = FlowWire.WriteCommand(epoch, requestId, Domain, (int)kind);
            if (kind == Kind.SceneTransition) message.Guid(transitionId);
            else message.String(sceneName);

            _pending = new PendingRequest
            {
                Id = requestId,
                Epoch = epoch,
                Kind = kind,
                Deadline = Time.unscaledTime + RequestTimeoutSeconds,
            };
            _session.SendToHost(message.ToArray());
            if (kind == Kind.WorldMapTravel) RestoreGuestMapFade();
        }

        private static bool CanGuestRequest()
        {
            return FlowCoordinator.WorldEpoch > 0 && FlowCoordinator.IsInitialized
                && !FlowCoordinator.ApplyingWorldBarrier && GameState.IsInGameSession && !GameState.IsLoading
                && !GameState.IsInMainMenu && !GameState.IsInCharacterCreation
                && GameState.Instance != null && GameState.Instance.CurrentMap != null
                && GameState.s_playerCharacter != null && !GameState.InCombat
                && !GameState.GameOver && !GameState.PartyDead
                && !Cutscene.CutsceneActive && !GameState.IsInCutscene;
        }

        private long NextRequestId()
        {
            if (_nextRequestId <= 0 || _nextRequestId >= SameMapNotificationId) _nextRequestId = 1;
            return _nextRequestId++;
        }

        private FlowResultCode BeginSceneTransition(Guid transitionId)
        {
            GameObject obj = ResolveObject(transitionId);
            SceneTransition transition = obj != null ? obj.GetComponent<SceneTransition>() : null;
            if (transition == null || !transition.isActiveAndEnabled || !transition.IsUsable)
                return FlowResultCode.Rejected;

            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                if (member != null && transition.IsObjectOnWaitList(member.gameObject)) return FlowResultCode.NotReady;

            try
            {
                // The SceneTransition patch supplies the narrow selection bypass and restores it.
                transition.UseTransition(GameState.s_playerCharacter.gameObject);
                return FlowResultCode.Ok;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Guest area transition failed: " + ex);
                return FlowResultCode.Failed;
            }
        }

        private FlowResultCode BeginWorldMapTravel(string sceneName)
        {
            try
            {
                UIWorldMapManager manager = UIWorldMapManager.Instance;
                MapData current = GameState.Instance.CurrentMap;
                MapData target = WorldMap.Instance != null ? WorldMap.Instance.GetMap(sceneName) : null;
                if (manager == null || manager.MasterLinks == null || current == null || target == null
                    || target == current || !InGameHUD.TravelEnabled || !target.IsVisibleToUser
                    || target.Visibility != MapData.VisibilityType.Unlocked
                    || !manager.MasterLinks.GetConnected(current, target)) return FlowResultCode.Rejected;

                // Native host travel owns start-point selection, travel time and difficulty prompts.
                manager.DoTransition(target.SceneName, target.MapTag);
                return FlowResultCode.Ok;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Guest world-map travel failed: " + ex);
                return FlowResultCode.Failed;
            }
        }

        private static FlowResultCode ValidateCommonTransition(int playerId)
        {
            AreaTransitionSync self = s_instance;
            if (self == null || !self._session.IsPeerReady(playerId)) return FlowResultCode.NotReady;
            if (FlowCoordinator.WorldEpoch <= 0 || FlowCoordinator.ApplyingWorldBarrier
                || !GameState.IsInGameSession || GameState.IsLoading
                || GameState.IsInMainMenu || GameState.IsInCharacterCreation
                || GameState.Instance == null || GameState.Instance.CurrentMap == null
                || GameState.s_playerCharacter == null || FadeManager.Instance == null
                || FadeManager.Instance.IsFadeActive()) return FlowResultCode.NotReady;
            if (GameState.InCombat || GameState.GameOver || GameState.PartyDead
                || Cutscene.CutsceneActive || GameState.IsInCutscene
                || ConversationManager.Instance != null && ConversationManager.Instance.IsConversationOrSIRunning())
                return FlowResultCode.NotReady;

            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
            {
                if (member == null) continue;
                Health health = member.GetComponent<Health>();
                if (health != null && (health.CurrentHealth <= 0f || health.CurrentStamina <= 0f))
                    return FlowResultCode.Rejected;
            }
            return FlowResultCode.Ok;
        }

        private void Complete(PlayerInfo sender, long requestId, long worldEpoch, Kind kind,
            FlowResultCode result, Guid transitionId, string sceneName)
        {
            SendResult(sender.Id, worldEpoch, requestId, kind, result, true, transitionId, sceneName);
        }

        private void SendResult(int playerId, long worldEpoch, long requestId, Kind kind,
            FlowResultCode result, bool cache, Guid transitionId = default, string sceneName = null)
        {
            byte[] data = ResultMessage(worldEpoch, requestId, kind, result).ToArray();
            if (cache)
            {
                var key = new CommandKey { PlayerId = playerId, Epoch = worldEpoch, RequestId = requestId };
                while (_receipts.Count >= MaxReceipts && _receiptOrder.Count > 0)
                    _receipts.Remove(_receiptOrder.Dequeue());
                _receiptOrder.Enqueue(key);
                _receipts[key] = new Receipt
                {
                    Kind = kind,
                    TransitionId = transitionId,
                    SceneName = sceneName,
                    Data = data,
                };
            }
            _session.SendToPlayer(playerId, data);
        }

        private MessageWriter ResultMessage(long worldEpoch, long requestId, Kind kind, FlowResultCode result)
        {
            return FlowWire.WriteResult(worldEpoch, requestId, Domain, (int)kind, result, _stateSequence);
        }

        internal static void HostSameMapTeleported(PartyWaypoint waypoint)
        {
            AreaTransitionSync self = s_instance;
            if (self == null || waypoint == null || !self._session.IsHost || self._session.Players.Count < 2) return;

            self.SyncSession();
            if (!self._session.IsHost || FlowCoordinator.WorldEpoch <= 0) return;
            if (self._stateSequence < long.MaxValue) self._stateSequence++;

            // The fixed-size result carries no waypoint payload. Positions arrive first,
            // so the notification can focus the guest's already-corrected party.
            self.SendPartyPositions();
            self._session.SendToGuests(self.ResultMessage(
                FlowCoordinator.WorldEpoch, SameMapNotificationId, Kind.SameMapTeleport, FlowResultCode.Ok).ToArray());
        }

        private void SendPartyPositions()
        {
            var members = new List<PartyMemberAI>(PartyMemberAI.PartyMembers.Length);
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
            {
                if (member == null || EntityRegistry.NetworkId(member.gameObject) == Guid.Empty) continue;
                Vector3 position = member.transform.position;
                float facing = member.transform.eulerAngles.y;
                if (IsFinite(position) && WithinBounds(position) && IsFinite(facing)) members.Add(member);
            }
            if (members.Count == 0) return;

            var message = new MessageWriter(MessageType.Positions).Int(members.Count);
            foreach (PartyMemberAI member in members)
            {
                Vector3 position = member.transform.position;
                message.Guid(EntityRegistry.NetworkId(member.gameObject))
                    .Float(position.x).Float(position.y).Float(position.z)
                    .Float(member.transform.eulerAngles.y).Float(0f).Float(0f);
            }
            _session.SendToGuests(message.ToArray());
        }

        private static void ApplySameMapTeleport()
        {
            ResetGuestInteraction();
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
            {
                if (member == null) continue;
                if (member.Mover != null) member.Mover.Stop();
                if (member.StateManager != null)
                {
                    member.StateManager.AbortStateStack();
                    member.InitAI();
                }
            }

            GameObject focus = GameState.s_playerCharacter != null ? GameState.s_playerCharacter.gameObject : null;
            if (focus == null)
                foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                    if (member != null) { focus = member.gameObject; break; }
            if (focus != null && CameraControl.Instance != null)
                CameraControl.Instance.FocusOnPoint(focus.transform.position);
        }

        private static bool CanApplyHostNotification()
        {
            AreaTransitionSync self = s_instance;
            return self != null && self._session.IsGuest && self._session.IsWorldReady
                && !FlowCoordinator.ApplyingWorldBarrier && GameState.IsInGameSession && !GameState.IsLoading;
        }

        private static void ResetGuestInteraction()
        {
            if (InGameSelectionTargeting.Instance != null)
                InGameSelectionTargeting.Instance.CancelAttackOrInteractions();
            if (GameState.s_playerCharacter != null) GameState.s_playerCharacter.CancelModes(true);
            GameCursor.DesiredCursor = GameCursor.CursorType.Normal;
        }

        private static void CloseGuestWorldMap()
        {
            if (UIWorldMapManager.Instance != null && UIWorldMapManager.Instance.IsVisible)
                UIWorldMapManager.Instance.HideWindow(true);
            InGameHUD.TravelEnabled = false;
            ResetGuestInteraction();
        }

        private static void RestoreGuestMapFade()
        {
            if (FadeManager.Instance != null && FadeManager.Instance.IsFadeActive())
                FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.AreaTransition, 0.35f);
        }

        internal static IDisposable BeginWholePartySelection()
        {
            if (!Plugin.IsHost) return null;
            if (s_selectionDepth > 0)
            {
                s_selectionDepth++;
                return new SelectionScope();
            }

            var state = new SelectionState
            {
                Members = (PartyMemberAI[])PartyMemberAI.PartyMembers.Clone(),
                Selected = new bool[PartyMemberAI.PartyMembers.Length],
                DragSelected = new bool[PartyMemberAI.PartyMembers.Length],
            };
            for (int i = 0; i < state.Members.Length; i++)
            {
                if (state.Members[i] == null) continue;
                state.Selected[i] = state.Members[i].Selected;
                state.DragSelected[i] = state.Members[i].DragSelected;
            }
            s_selectionState = state;
            s_selectionDepth = 1;
            return new SelectionScope();
        }

        internal static bool AllowWholePartySelection => Plugin.IsHost && s_selectionDepth > 0;

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            SyncSession();
            _pending = null;
            _stateSequence = 0;
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            List<CommandKey> remove = null;
            foreach (CommandKey key in _receipts.Keys)
                if (key.PlayerId == player.Id) (remove ?? (remove = new List<CommandKey>())).Add(key);
            if (remove != null) foreach (CommandKey key in remove) _receipts.Remove(key);
        }

        private void SyncSession()
        {
            if (_generation == _session.Generation) return;
            _generation = _session.Generation;
            _nextRequestId = 1;
            _pending = null;
            _receipts.Clear();
            _receiptOrder.Clear();
            _observedEpoch = 0;
            _stateSequence = 0;
        }

        private static Guid EntityId(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            Guid id = EntityRegistry.NetworkId(obj);
            if (id != Guid.Empty) return id;
            InstanceID instance = obj.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static GameObject ResolveObject(Guid id)
        {
            if (id == Guid.Empty) return null;
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj : InstanceID.GetObjectByID(id);
        }

        private static bool WithinBounds(Vector3 value)
        {
            return Mathf.Abs(value.x) <= MaxCoordinate && Mathf.Abs(value.y) <= MaxCoordinate
                && Mathf.Abs(value.z) <= MaxCoordinate;
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
