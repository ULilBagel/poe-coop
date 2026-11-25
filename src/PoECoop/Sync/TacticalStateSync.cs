using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Host-authoritative tactical snapshots. The guest receives complete ordered
    /// event lists, but applies them only after every actor, free action and cast
    /// attack in the snapshot can be resolved locally.
    ///
    /// Wire layout after the type byte: epoch, sequence, kind, then for a snapshot
    /// (kind 0 or 1 = full) mode, all static round settings, manager flags,
    /// current event/phase/timers, turn locks, actors, event records, then the
    /// exact ordered queue of event IDs. Sequence gaps are repaired through
    /// TacticalOrders' semantic resync command.
    ///
    /// Kind 2 is a timers-only update for the snapshot whose epoch and sequence it
    /// names: a tick counter, the four manager timers and the turn-lock remaining
    /// times. It does not consume a sequence number and is ignored by a guest that
    /// does not hold exactly that snapshot.
    /// </summary>
    public sealed class TacticalStateSync : IDisposable
    {
        internal const int MaxActors = 128;
        internal const int MaxEvents = 512;
        internal const int MaxFreeActions = 32;
        internal const int MaxSnapshotBytes = 256 * 1024;
        internal const int MaxAbilityNameLength = 96;
        internal const int MaxDisplayStringId = 10000000;
        internal const int MaxAbilityOccurrence = 191;
        internal const int MaxAttackOccurrence = 63;

        private const float SendInterval = 0.05f;
        private const float FullRefreshInterval = 2f;
        private const float ResyncInterval = 0.5f;
        // Applied after a forced refresh so this stream's periodic refresh does not
        // share a frame with the other streams that were forced by the same event.
        private const float RefreshPhaseOffset = 1.95f;
        private const float MinResyncSpacing = 0.1f;
        private const float ResolveRetryInterval = 0.1f;
        private const float TimerInterval = 0.1f;
        // A guest skips re-applying a byte-identical snapshot, but not for longer than this.
        private const float IdenticalReapplyInterval = 10f;
        // Message layout: type byte, epoch, sequence, kind, then the body.
        private const int SequenceOffset = 5;
        private const int BodyOffset = 10;
        // Body: mode byte, 16 round settings, manager flags, current event ID and
        // phase byte precede the four timers, the turn-lock count and its values.
        private const int TimersOffset = BodyOffset + 1 + 16 * 4 + 4 + 4 + 1;
        private const int TimersLength = 4 * 4;
        private const int LockCountOffset = TimersOffset + TimersLength;
        private const byte KindSnapshot = 0;
        private const byte KindFull = 1;
        private const byte KindTimers = 2;
        private const int MaxTurnLocks = 64;
        private const int MaxRound = 4096;
        private const int MaxInitiativeBias = 4096;
        private const float MaxTimer = 86400f;
        private const float MaxInitiative = 10000000f;

        private const int ManagerAutoPass = 1;
        private const int ManagerUnlimitedMovement = 2;
        private const int ManagerUnlimitedActions = 4;
        private const int ManagerHasRoundStart = 8;
        private const int ManagerPartyInCombat = 16;
        private const int ManagerRefreshLocked = 32;
        private const int ManagerAllFlags = 63;

        private const byte ActorAvailableAction = 1;
        private const byte ActorUsedFreePotion = 2;
        private const byte ActorSwitchedWeapons = 4;
        private const byte ActorEvaluateTarget = 8;
        private const byte ActorAllFlags = 15;

        private static readonly AccessTools.FieldRef<TacticalModeManager, float> CurrentEventTimer =
            AccessTools.FieldRefAccess<TacticalModeManager, float>("m_currentEventTimer");
        private static readonly AccessTools.FieldRef<TacticalModeManager, float> SpinUpTimer =
            AccessTools.FieldRefAccess<TacticalModeManager, float>("m_spinUpTimer");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> SpinDownComplete =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_spinDownComplete");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> HasRoundStart =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_hasRoundStart");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> PartyInCombat =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_isPartyMemberInCombat");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> RefreshLocked =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_refreshCurrentEventLocked");
        private static readonly FieldInfo TurnLocksField =
            AccessTools.Field(typeof(TacticalModeManager), "m_turnLockedBy");
        private static readonly FieldInfo TurnLockTimeoutField =
            TurnLocksField != null ? AccessTools.Field(TurnLocksField.FieldType.GetGenericArguments()[0], "TimeOutTime") : null;

        [ThreadStatic]
        private static int s_applyDepth;

        private static TacticalStateSync s_instance;

        private readonly Session _session;
        private readonly int _mainThreadId;
        private readonly TacticalEventPresentation _presentation;
        private readonly Dictionary<TacticalEvent, int> _eventIds = new Dictionary<TacticalEvent, int>();
        private readonly HashSet<Guid> _knownActors = new HashSet<Guid>();
        private readonly HashSet<TacticalEvent> _seenEvents = new HashSet<TacticalEvent>();
        private readonly Dictionary<Guid, AIController> _captureActors = new Dictionary<Guid, AIController>();
        private readonly List<Guid> _captureActorIds = new List<Guid>();
        private readonly List<TacticalEvent> _staleEvents = new List<TacticalEvent>();
        private readonly Dictionary<GenericAbility, AbilityKeyEntry> _abilityKeys = new Dictionary<GenericAbility, AbilityKeyEntry>();
        private readonly Dictionary<TacticalEvent, CastAttackEntry> _castAttacks = new Dictionary<TacticalEvent, CastAttackEntry>();

        private static readonly HashSet<Guid> s_readActorIds = new HashSet<Guid>();
        private static readonly HashSet<int> s_readEventIds = new HashSet<int>();
        private static readonly HashSet<int> s_readQueued = new HashSet<int>();
        private static readonly List<float> s_readLocks = new List<float>();

        private long _generation;
        private bool _wasHost, _wasGuest;
        private int _epoch = 1, _sequence, _nextEventId = 1;
        private int _lastEpoch = -1, _lastSequence = -1;
        private float _nextSend, _nextFullRefresh, _nextResync, _nextCaptureWarning;
        private float _lastResync = float.NegativeInfinity, _nextResolve;
        // Host: timers-only updates sent for the snapshot numbered _sequence.
        private float _nextTimers;
        private int _timerTick;
        // Guest: the snapshot the presentation currently shows, the body bytes it
        // was decoded from (null once timers-only updates moved it on), and the
        // last timers-only tick accepted for _lastSequence.
        private int _appliedEpoch = -1, _appliedSequence = -1, _lastTimerTick;
        private byte[] _appliedBody, _pendingBody;
        private float _lastApply;
        private byte[] _lastSignature;
        // Set only while _lastSignature is a Disabled-mode snapshot.
        private TacticalSettings _idleSettings;
        private bool _idleAutoPass;
        private TacticalSnapshot _pending;

        public static bool ApplyingHostState => s_applyDepth > 0;
        public static int CurrentEpoch { get; private set; }
        public static int CurrentEventId { get; private set; }

        public TacticalStateSync(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _generation = session.Generation;
            _presentation = new TacticalEventPresentation(session);
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.WorldStateReady += OnWorldStateReady;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
            GameState.OnCombatStart += OnCombatChanged;
            GameState.OnCombatEnd += OnCombatChanged;
            GameState.OnTacticalModeChanged.Subscribe(OnTacticalModeChanged);
        }

        public void Dispose()
        {
            RequireMainThread();
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.WorldStateReady -= OnWorldStateReady;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            GameState.OnCombatStart -= OnCombatChanged;
            GameState.OnCombatEnd -= OnCombatChanged;
            GameState.OnTacticalModeChanged.Unsubscribe(OnTacticalModeChanged);
            _presentation.Dispose();
            _eventIds.Clear();
            _knownActors.Clear();
            _seenEvents.Clear();
            _captureActors.Clear();
            _abilityKeys.Clear();
            _castAttacks.Clear();
            _pending = null;
            if (s_instance == this)
            {
                s_instance = null;
                s_applyDepth = 0;
                CurrentEpoch = 0;
                CurrentEventId = 0;
            }
        }

        internal static IDisposable HostApplyScope()
        {
            s_applyDepth++;
            return new ApplyScope();
        }

        private sealed class ApplyScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                s_applyDepth--;
            }
        }

        public void Update(float now)
        {
            RequireMainThread();
            SyncSession();
            if (_session.IsHost) HostUpdate(now);
            else if (_session.IsGuest) GuestUpdate(now);
            else if (CurrentEpoch != 0 || CurrentEventId != 0)
            {
                CurrentEpoch = 0;
                CurrentEventId = 0;
            }
        }

        public static void SendLatestTo(int playerId)
        {
            TacticalStateSync self = s_instance;
            if (self == null) return;
            self.RequireMainThread();
            self.SendLatestToPlayer(playerId);
        }

        private void SendLatestToPlayer(int playerId)
        {
            if (!_session.IsHost || playerId == Session.HostId || !CanCapture()) return;
            if (_sequence == int.MaxValue)
            {
                AdvanceEpoch();
                _sequence = 0;
                _lastSignature = null;
            }
            UpdateHostToken();
            long started = PerfStats.Begin();
            TacticalSnapshot snapshot = Capture(true);
            byte[] data = null;
            if (snapshot != null)
            {
                snapshot.Sequence = _sequence + 1;
                data = Serialize(snapshot);
            }
            PerfStats.End("tactical.capture", started);
            if (data == null) return;
            if (data.Length > MaxSnapshotBytes)
            {
                CaptureWarning("Tactical snapshot exceeded 256 KiB and was not sent");
                return;
            }
            _session.SendToPlayer(playerId, data);
            _sequence = snapshot.Sequence;
            PerfStats.Count("tactical.resync");
            PerfStats.Count("tactical.full");
            PerfStats.Count("tactical.records", snapshot.Actors.Count + snapshot.Events.Count);
            if (_session.Players.Count == 2)
            {
                // The only guest now holds this exact sequence, so no other peer
                // needs the repairing broadcast and later snapshots stay continuous.
                Remember(snapshot, data);
                _nextFullRefresh = Time.unscaledTime + FullRefreshInterval + RefreshPhaseOffset;
                return;
            }
            _lastSignature = null;
            _idleSettings = null;
            _nextFullRefresh = 0f;
        }

        private void Remember(TacticalSnapshot snapshot, byte[] data)
        {
            _lastSignature = data;
            _timerTick = 0;
            _idleSettings = snapshot.Mode == TacticalMode.Disabled ? snapshot.Settings : null;
            _idleAutoPass = snapshot.AutoPass;
        }

        private void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("Tactical synchronization must run on the Unity main thread");
        }

        private void SyncSession()
        {
            bool host = _session.IsHost, guest = _session.IsGuest;
            if (_generation != _session.Generation || host != _wasHost || guest != _wasGuest)
            {
                _generation = _session.Generation;
                _pending = null;
                _pendingBody = null;
                _nextResync = 0f;
                if (host) ResetHostStream();
                else
                {
                    ResetGuestStream();
                    if (!guest) _presentation.Reset(false);
                }
            }
            _wasHost = host;
            _wasGuest = guest;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) SendLatestTo(player.Id);
        }

        // Game messages are withheld from a peer until its world is ready, so the
        // join-time snapshot may never have reached it.
        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            if (player != null && _session.IsHost) SendLatestTo(player.Id);
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            RequireMainThread();
            if (_session.IsHost) ResetHostStream();
            else ResetGuestStream();
        }

        private void OnCombatChanged(object sender, EventArgs e)
        {
            RequireMainThread();
            if (_session.IsHost) ResetHostStream();
            else ResetGuestStream();
        }

        private void OnTacticalModeChanged(TacticalMode mode)
        {
            RequireMainThread();
            if (_session.IsHost) ResetHostStream();
            else ResetGuestStream();
        }

        private void ResetHostStream()
        {
            AdvanceEpoch();
            _sequence = 0;
            _lastSignature = null;
            _idleSettings = null;
            _eventIds.Clear();
            _knownActors.Clear();
            _abilityKeys.Clear();
            _castAttacks.Clear();
            _nextEventId = 1;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            _nextTimers = 0f;
            _timerTick = 0;
            CurrentEpoch = 0;
            CurrentEventId = 0;
        }

        private void ResetGuestStream()
        {
            _lastEpoch = -1;
            _lastSequence = -1;
            _pending = null;
            _pendingBody = null;
            ForgetApplied();
            _lastTimerTick = 0;
            _nextResolve = 0f;
            // A reset may ask again promptly, but repeated resets cannot turn the
            // resync request into a per-frame message.
            _nextResync = Mathf.Min(_nextResync, _lastResync + MinResyncSpacing);
            CurrentEpoch = 0;
            CurrentEventId = 0;
            _presentation.Reset(true);
        }

        private void ForgetApplied()
        {
            _appliedEpoch = -1;
            _appliedSequence = -1;
            _appliedBody = null;
        }

        private void AdvanceEpoch()
        {
            _epoch = _epoch == int.MaxValue ? 1 : _epoch + 1;
        }

        // ---- host ----

        private void HostUpdate(float now)
        {
            UpdateHostToken();
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !CanCapture()) return;

            bool forced = _lastSignature == null || _nextFullRefresh <= 0f;
            bool full = forced || now >= _nextFullRefresh;
            if (!full && IdleUnchanged())
            {
                PerfStats.Count("tactical.skipped");
                return;
            }

            long started = PerfStats.Begin();
            TacticalSnapshot snapshot = Capture(full);
            byte[] data = null;
            if (snapshot != null)
            {
                snapshot.Sequence = unchecked(_sequence + 1);
                snapshot.Full = full;
                data = Serialize(snapshot);
            }
            PerfStats.End("tactical.capture", started);
            if (data == null) return;
            if (!full)
            {
                if (SameSnapshot(data, _lastSignature))
                {
                    PerfStats.Count("tactical.skipped");
                    return;
                }
                if (SameStructure(data, _lastSignature) && TimerLayoutMatches(data, snapshot))
                {
                    // Only timers moved since the snapshot the guests hold. Until the
                    // rate limit allows an update, later ticks compare again.
                    if (now >= _nextTimers) SendTimers(snapshot, data, now);
                    return;
                }
            }
            if (_sequence == int.MaxValue)
            {
                AdvanceEpoch();
                _sequence = 0;
                snapshot.Epoch = _epoch;
                snapshot.Sequence = 1;
                full = true;
                snapshot.Full = true;
                data = Serialize(snapshot);
            }

            if (data.Length > MaxSnapshotBytes)
            {
                CaptureWarning("Tactical snapshot exceeded 256 KiB and was not sent");
                return;
            }
            _session.SendToGuests(data);
            _sequence = snapshot.Sequence;
            Remember(snapshot, data);
            PerfStats.Count(full ? "tactical.full" : "tactical.delta");
            PerfStats.Count("tactical.records", snapshot.Actors.Count + snapshot.Events.Count);
            if (full) _nextFullRefresh = now + FullRefreshInterval + (forced ? RefreshPhaseOffset : 0f);
        }

        /// <summary>
        /// Sends the timers of a snapshot whose structure equals the one numbered
        /// _sequence, and remembers it as what the guests now hold.
        /// </summary>
        private void SendTimers(TacticalSnapshot snapshot, byte[] data, float now)
        {
            _timerTick = _timerTick == int.MaxValue ? 1 : _timerTick + 1;
            var writer = new MessageWriter(MessageType.TacticalState)
                .Int(_epoch).Int(_sequence).Byte(KindTimers).Int(_timerTick)
                .Float(snapshot.CurrentTimer).Float(snapshot.RealTimer).Float(snapshot.SpinUpTimer).Float(snapshot.EffectTime)
                .Int(snapshot.TurnLocks.Count);
            foreach (float remaining in snapshot.TurnLocks) writer.Float(remaining);
            _session.SendToGuests(writer.ToArray());
            _lastSignature = data;
            _nextTimers = now + TimerInterval;
            PerfStats.Count("tactical.timers");
        }

        /// <summary>
        /// True while tactical mode is off and nothing a Disabled snapshot carries
        /// differs from the one last sent, so real-time play captures nothing.
        /// </summary>
        private bool IdleUnchanged()
        {
            if (_idleSettings == null || GameState.Option.TacticalMode != TacticalMode.Disabled) return false;
            TacticalModeManager manager = TacticalModeManager.Instance;
            return _idleAutoPass == (manager != null && manager.AutoPassTurns) && _idleSettings.MatchesCurrent();
        }

        private static bool CanCapture()
        {
            return GameState.IsInGameSession && !GameState.IsLoading && GameState.Option != null;
        }

        private void UpdateHostToken()
        {
            if (!CanCapture() || !TacticalModeManager.IsTacticalModeEnabled || TacticalModeManager.Instance == null)
            {
                CurrentEpoch = 0;
                CurrentEventId = 0;
                return;
            }
            TacticalEvent current = TacticalModeManager.Instance.CurrentEvent;
            CurrentEpoch = _epoch;
            CurrentEventId = current != null ? EventId(current) : 0;
        }

        private TacticalSnapshot Capture(bool full)
        {
            try
            {
                // Identity keys are recomputed on every full snapshot, so a cached
                // key never outlives one refresh interval.
                if (full)
                {
                    _abilityKeys.Clear();
                    _castAttacks.Clear();
                }
                var settings = TacticalSettings.Capture();
                if (GameState.Option.TacticalMode == TacticalMode.Disabled)
                {
                    return new TacticalSnapshot
                    {
                        Epoch = _epoch,
                        Full = full,
                        Mode = TacticalMode.Disabled,
                        Settings = settings,
                        AutoPass = TacticalModeManager.Instance != null && TacticalModeManager.Instance.AutoPassTurns,
                    };
                }

                TacticalModeManager manager = TacticalModeManager.Instance;
                if (manager == null) return null;
                var snapshot = new TacticalSnapshot
                {
                    Epoch = _epoch,
                    Full = full,
                    Mode = GameState.Option.TacticalMode,
                    Settings = settings,
                    AutoPass = manager.AutoPassTurns,
                    UnlimitedMovement = manager.UnlimitedMovement,
                    UnlimitedActions = manager.UnlimitedActions,
                    HasRoundStart = HasRoundStart(manager),
                    PartyInCombat = PartyInCombat(manager),
                    RefreshLocked = RefreshLocked(manager),
                    CurrentEventId = manager.CurrentEvent != null ? EventId(manager.CurrentEvent) : 0,
                    CurrentTimer = BoundedTimer(CurrentEventTimer(manager)),
                    RealTimer = BoundedTimer(manager.CurrentEventRealTimer),
                    SpinUpTimer = BoundedTimer(SpinUpTimer(manager)),
                    Phase = CurrentPhase(manager),
                };

                IList<TacticalEvent> nativeEvents = manager.Events;
                if (nativeEvents.Count > MaxEvents) throw new InvalidOperationException("native tactical queue exceeds sync bound");
                HashSet<TacticalEvent> seen = _seenEvents;
                seen.Clear();
                for (int i = 0; i < nativeEvents.Count; i++)
                {
                    TacticalEvent tacticalEvent = nativeEvents[i];
                    if (tacticalEvent == null || !seen.Add(tacticalEvent)) continue;
                    TacticalEventRecord record = CaptureEvent(tacticalEvent);
                    snapshot.Events.Add(record);
                    snapshot.Queue.Add(record.Id);
                }
                if (manager.CurrentEvent != null && seen.Add(manager.CurrentEvent))
                {
                    if (snapshot.Events.Count >= MaxEvents) throw new InvalidOperationException("detached current event exceeds sync bound");
                    snapshot.Events.Add(CaptureEvent(manager.CurrentEvent));
                }

                CaptureTurnLocks(manager, snapshot);
                CaptureActors(manager, snapshot);
                snapshot.EffectTime = snapshot.Events.Count > 0 || snapshot.PartyInCombat
                    ? BoundedTimer(manager.EffectTime) : 0f;
                PruneEventIds(seen);
                return snapshot;
            }
            catch (Exception ex)
            {
                CaptureWarning("Could not capture tactical state: " + ex.Message);
                return null;
            }
        }

        private TacticalPhase CurrentPhase(TacticalModeManager manager)
        {
            if (manager.CurrentEvent == null) return TacticalPhase.None;
            if (!SpinDownComplete(manager)) return TacticalPhase.Ending;
            if (SpinUpTimer(manager) > 0f || !manager.CurrentEvent.IsActive) return TacticalPhase.Starting;
            return TacticalPhase.Active;
        }

        private static void CaptureTurnLocks(TacticalModeManager manager, TacticalSnapshot snapshot)
        {
            var locks = TurnLocksField.GetValue(manager) as System.Collections.IList;
            if (locks == null) return;
            if (locks.Count > MaxTurnLocks) throw new InvalidOperationException("turn lock count exceeds sync bound");
            foreach (object entry in locks)
            {
                float timeout = (float)TurnLockTimeoutField.GetValue(entry);
                snapshot.TurnLocks.Add(BoundedTimer(timeout - Time.time));
            }
        }

        private void CaptureActors(TacticalModeManager manager, TacticalSnapshot snapshot)
        {
            Dictionary<Guid, AIController> actors = _captureActors;
            actors.Clear();
            foreach (TacticalEventRecord record in snapshot.Events)
            {
                if (record.ActorId == Guid.Empty || actors.ContainsKey(record.ActorId)) continue;
                GameObject obj = ResolveObject(record.ActorId);
                AIController actor = obj != null ? GameUtilities.FindActiveAIController(obj) : null;
                if (actor == null) throw new InvalidOperationException("event actor cannot be resolved on host");
                actors.Add(record.ActorId, actor);
            }
            foreach (Guid id in _knownActors)
            {
                if (actors.Count >= MaxActors || actors.ContainsKey(id)) continue;
                GameObject obj = ResolveObject(id);
                AIController actor = obj != null ? GameUtilities.FindActiveAIController(obj) : null;
                if (actor != null) actors.Add(id, actor);
            }
            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            for (int i = 0; i < members.Length && actors.Count < MaxActors; i++)
            {
                PartyMemberAI member = members[i];
                if (member == null || member.TacticalTurnEventCount == 0) continue;
                Guid id = NetworkId(member.gameObject);
                if (id != Guid.Empty && !actors.ContainsKey(id)) actors.Add(id, member);
            }
            if (actors.Count > MaxActors) throw new InvalidOperationException("tactical actor count exceeds sync bound");

            List<Guid> ids = _captureActorIds;
            ids.Clear();
            ids.AddRange(actors.Keys);
            ids.Sort((a, b) => a.CompareTo(b));
            _knownActors.Clear();
            foreach (Guid id in ids)
            {
                AIController actor = actors[id];
                CharacterStats stats = actor.CharacterStats;
                if (stats == null) throw new InvalidOperationException("tactical actor has no CharacterStats");
                snapshot.Actors.Add(CaptureActor(id, actor, stats));
                _knownActors.Add(id);
            }
            actors.Clear();
        }

        private TacticalActorRecord CaptureActor(Guid id, AIController actor, CharacterStats stats)
        {
            var record = new TacticalActorRecord
            {
                Id = id,
                CurrentInitiative = BoundedInitiative(stats.CurrentInitiative),
                TempInitiative = BoundedInitiative(stats.TempInitiative),
                PreviousInitiativeIncrement = BoundedInitiative(stats.PreviousInitiativeIncrement),
                PreviousBaseInitiative = BoundedInitiative(stats.PreviousBaseInitiative),
                PreviousBaseReload = BoundedInitiative(stats.PreviousBaseReload),
                InitiativeDelayFromReload = BoundedInitiative(stats.InitiativeDelayFromReload),
                AvailableMovement = BoundedTimer(actor.AvailableMovement),
                TurnEventCount = Mathf.Clamp(actor.TacticalTurnEventCount, 0, MaxEvents),
                AvailableAction = actor.AvailableAction,
                UsedFreePotion = UsedFreePotionField(actor),
                SwitchedWeapons = actor.SwitchedWeaponsThisTurn,
                EvaluateTarget = actor.EvaluateTarget,
            };
            var freeActions = UsedFreeActionsField(actor);
            if (freeActions != null)
            {
                if (freeActions.Count > MaxFreeActions) throw new InvalidOperationException("free-action count exceeds sync bound");
                foreach (GenericAbility ability in freeActions)
                {
                    if (ability == null) continue;
                    record.FreeActions.Add(CachedAbilityKey(stats, ability));
                }
            }
            return record;
        }

        private static readonly AccessTools.FieldRef<AIController, List<GenericAbility>> UsedFreeActionsField =
            AccessTools.FieldRefAccess<AIController, List<GenericAbility>>("UsedFreeActions");
        private static readonly AccessTools.FieldRef<AIController, bool> UsedFreePotionField =
            AccessTools.FieldRefAccess<AIController, bool>("UsedFreePotion");

        private TacticalEventRecord CaptureEvent(TacticalEvent tacticalEvent)
        {
            var record = new TacticalEventRecord
            {
                Id = EventId(tacticalEvent),
                Kind = EventKind(tacticalEvent),
                ActorId = tacticalEvent.Actor != null ? NetworkId(tacticalEvent.Actor.gameObject) : Guid.Empty,
                Active = tacticalEvent.IsActive,
                Preview = tacticalEvent.IsPreview,
                Visible = tacticalEvent.IsVisible,
                Obscured = tacticalEvent.IsObscured,
                SafeDestroy = tacticalEvent.UseSafeDestroy,
                Round = tacticalEvent.RoundOffset,
                InitiativeDelay = BoundedInitiative(tacticalEvent.InitiativeDelay),
                InitiativeSort = BoundedInitiative(tacticalEvent.InitiativeSort),
                InitiativeBias = tacticalEvent.InitiativeBias,
                Duration = BoundedTimer(tacticalEvent.TimeOutDuration),
            };
            if ((record.Kind == TacticalEventKind.Turn || record.Kind == TacticalEventKind.CastFinished
                || record.Kind == TacticalEventKind.ProjectileTurn) && record.ActorId == Guid.Empty)
                throw new InvalidOperationException("tactical actor has no network identity");

            if (tacticalEvent is TacticalTurnEvent turn)
            {
                record.PassStyle = turn.PassStyle;
                record.SkipTurnStart = turn.SkipTurnStart;
                record.SkipTurnEnd = turn.SkipTurnEnd;
                record.PreviewInitiativeModifier = BoundedInitiative(turn.PreviewInitiativeModifier);
            }
            else if (tacticalEvent is TacticalCastFinishedEvent cast)
            {
                if (cast.Attack == null) throw new InvalidOperationException("cast event has no attack");
                record.SurpriseRound = cast.IsSurpriseRound;
                record.AttackTime = BoundedTimer(cast.AttackTime);
                record.Attack = CachedAttackIdentity(cast);
            }
            return record;
        }

        private static TacticalEventKind EventKind(TacticalEvent tacticalEvent)
        {
            if (tacticalEvent is TacticalCastFinishedEvent) return TacticalEventKind.CastFinished;
            if (tacticalEvent is TacticalTurnEvent) return TacticalEventKind.Turn;
            if (tacticalEvent is TacticalRoundStartEvent) return TacticalEventKind.RoundStart;
            if (tacticalEvent is TacticalDelayEvent) return TacticalEventKind.Delay;
            if (tacticalEvent is TacticalTurnLockedEvent) return TacticalEventKind.TurnLocked;
            if (tacticalEvent is TacticalProjectileTurnEvent) return TacticalEventKind.ProjectileTurn;
            throw new InvalidOperationException("unsupported tactical event type " + tacticalEvent.GetType().Name);
        }

        private int EventId(TacticalEvent tacticalEvent)
        {
            if (_eventIds.TryGetValue(tacticalEvent, out int id)) return id;
            for (int guard = 0; guard < MaxEvents + 1; guard++)
            {
                id = _nextEventId++;
                if (_nextEventId <= 0 || _nextEventId == int.MaxValue) _nextEventId = 1;
                if (id > 0 && !_eventIds.ContainsValue(id))
                {
                    _eventIds.Add(tacticalEvent, id);
                    return id;
                }
            }
            throw new InvalidOperationException("no tactical event ID available");
        }

        private void PruneEventIds(HashSet<TacticalEvent> seen)
        {
            if (_eventIds.Count == seen.Count) return;
            List<TacticalEvent> stale = _staleEvents;
            stale.Clear();
            foreach (TacticalEvent tacticalEvent in _eventIds.Keys)
                if (!seen.Contains(tacticalEvent)) stale.Add(tacticalEvent);
            foreach (TacticalEvent tacticalEvent in stale)
            {
                _eventIds.Remove(tacticalEvent);
                _castAttacks.Remove(tacticalEvent);
            }
            stale.Clear();
        }

        // ---- wire ----

        private static byte[] Serialize(TacticalSnapshot snapshot)
        {
            var writer = new MessageWriter(MessageType.TacticalState)
                .Int(snapshot.Epoch).Int(snapshot.Sequence).Byte(snapshot.Full ? KindFull : KindSnapshot);
            WriteBody(writer, snapshot);
            return writer.ToArray();
        }

        private static void WriteBody(MessageWriter writer, TacticalSnapshot snapshot)
        {
            writer.Byte((byte)snapshot.Mode);
            snapshot.Settings.Write(writer);
            int managerFlags = (snapshot.AutoPass ? ManagerAutoPass : 0)
                | (snapshot.UnlimitedMovement ? ManagerUnlimitedMovement : 0)
                | (snapshot.UnlimitedActions ? ManagerUnlimitedActions : 0)
                | (snapshot.HasRoundStart ? ManagerHasRoundStart : 0)
                | (snapshot.PartyInCombat ? ManagerPartyInCombat : 0)
                | (snapshot.RefreshLocked ? ManagerRefreshLocked : 0);
            writer.Int(managerFlags).Int(snapshot.CurrentEventId).Byte((byte)snapshot.Phase)
                .Float(snapshot.CurrentTimer).Float(snapshot.RealTimer).Float(snapshot.SpinUpTimer).Float(snapshot.EffectTime)
                .Int(snapshot.TurnLocks.Count);
            foreach (float remaining in snapshot.TurnLocks) writer.Float(remaining);

            writer.Int(snapshot.Actors.Count);
            foreach (TacticalActorRecord actor in snapshot.Actors)
            {
                writer.Guid(actor.Id).Float(actor.CurrentInitiative).Float(actor.TempInitiative)
                    .Float(actor.PreviousInitiativeIncrement).Float(actor.PreviousBaseInitiative)
                    .Float(actor.PreviousBaseReload).Float(actor.InitiativeDelayFromReload)
                    .Float(actor.AvailableMovement).Int(actor.TurnEventCount);
                byte flags = (byte)((actor.AvailableAction ? ActorAvailableAction : 0)
                    | (actor.UsedFreePotion ? ActorUsedFreePotion : 0)
                    | (actor.SwitchedWeapons ? ActorSwitchedWeapons : 0)
                    | (actor.EvaluateTarget ? ActorEvaluateTarget : 0));
                writer.Byte(flags).Int(actor.FreeActions.Count);
                foreach (TacticalAbilityKey key in actor.FreeActions) WriteAbilityKey(writer, key);
            }

            writer.Int(snapshot.Events.Count);
            foreach (TacticalEventRecord record in snapshot.Events)
            {
                byte flags = (byte)((record.Active ? 1 : 0) | (record.Preview ? 2 : 0)
                    | (record.Visible ? 4 : 0) | (record.Obscured ? 8 : 0)
                    | (record.SurpriseRound ? 16 : 0) | (record.SkipTurnStart ? 32 : 0)
                    | (record.SkipTurnEnd ? 64 : 0) | (record.SafeDestroy ? 128 : 0));
                writer.Int(record.Id).Byte((byte)record.Kind).Guid(record.ActorId).Byte(flags).Byte((byte)record.PassStyle)
                    .Int(record.Round).Float(record.InitiativeDelay).Float(record.InitiativeSort)
                    .Float(record.PreviewInitiativeModifier).Int(record.InitiativeBias).Float(record.Duration);
                if (record.Kind == TacticalEventKind.CastFinished)
                {
                    writer.String(record.Attack.Name).Int(record.Attack.Occurrence).Float(record.AttackTime);
                    WriteOptionalAbilityKey(writer, record.Attack.Ability);
                }
            }
            writer.Int(snapshot.Queue.Count);
            foreach (int id in snapshot.Queue) writer.Int(id);
        }

        private static void WriteAbilityKey(MessageWriter writer, TacticalAbilityKey key)
        {
            writer.String(key.Name).Int(key.DisplayId).Int(key.Occurrence);
        }

        private static void WriteOptionalAbilityKey(MessageWriter writer, TacticalAbilityKey key)
        {
            bool present = key.Occurrence >= 0;
            writer.Byte((byte)(present ? 1 : 0));
            if (present) WriteAbilityKey(writer, key);
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            RequireMainThread();
            if (reader.Type != MessageType.TacticalState || sender != null || !_session.IsGuest) return;

            long started = PerfStats.Begin();
            TacticalSnapshot snapshot = null;
            byte[] body = null;
            try
            {
                int epoch = reader.Int(), sequence = reader.Int();
                if (epoch <= 0 || sequence <= 0) throw new FormatException("tactical epoch/sequence");
                byte kind = reader.Byte();
                if (kind > KindTimers) throw new FormatException("tactical message kind");
                if (kind == KindTimers)
                {
                    ReceiveTimers(reader, epoch, sequence);
                    return;
                }
                bool full = kind == KindFull;
                bool acceptable = epoch == _lastEpoch && sequence > _lastSequence && (full || sequence == _lastSequence + 1);
                body = reader.PeekRemainingBytes();
                // The same bytes were validated and applied before, and nothing has
                // been written over them since: take the sequence, keep the state.
                if (acceptable && _pending == null && _appliedBody != null && _appliedEpoch == epoch
                    && _appliedSequence == _lastSequence && Time.unscaledTime - _lastApply < IdenticalReapplyInterval
                    && SameBytes(body, _appliedBody))
                {
                    _lastSequence = sequence;
                    _appliedSequence = sequence;
                    _lastTimerTick = 0;
                    PerfStats.Count("tactical.skipped");
                    return;
                }
                snapshot = ReadSnapshot(reader, epoch, sequence, full);
                reader.Finish();
            }
            finally { PerfStats.End("tactical.update", started); }
            if (snapshot.Epoch < _lastEpoch || snapshot.Epoch == _lastEpoch && snapshot.Sequence <= _lastSequence) return;
            bool continuous = snapshot.Epoch == _lastEpoch && snapshot.Sequence == _lastSequence + 1;
            bool initialFull = snapshot.Epoch != _lastEpoch && snapshot.Full;
            bool repairFull = snapshot.Epoch == _lastEpoch && snapshot.Full;
            if (!continuous && !initialFull && !repairFull)
            {
                RequestResync();
                return;
            }

            if (snapshot.Epoch != _lastEpoch)
            {
                CurrentEpoch = 0;
                CurrentEventId = 0;
                ForgetApplied();
                _presentation.Reset(true);
            }
            _lastEpoch = snapshot.Epoch;
            _lastSequence = snapshot.Sequence;
            _lastTimerTick = 0;
            _pending = snapshot;
            _pendingBody = body;
            _nextResolve = 0f;
        }

        /// <summary>
        /// Applies a timers-only update to the snapshot it names. Without exactly
        /// that snapshot here it is dropped; the next complete snapshot or the
        /// resync it triggers carries the timers again.
        /// </summary>
        private void ReceiveTimers(MessageReader reader, int epoch, int sequence)
        {
            int tick = reader.Int();
            if (tick <= 0) throw new FormatException("tactical timer tick");
            float currentTimer = ReadTimer(reader), realTimer = ReadTimer(reader);
            float spinUpTimer = ReadTimer(reader), effectTime = ReadTimer(reader);
            int lockCount = reader.Int();
            CheckCount(lockCount, MaxTurnLocks, "turn locks");
            List<float> locks = s_readLocks;
            locks.Clear();
            for (int i = 0; i < lockCount; i++) locks.Add(ReadTimer(reader));
            reader.Finish();

            if (epoch != _lastEpoch || sequence != _lastSequence || tick <= _lastTimerTick) return;
            _lastTimerTick = tick;
            if (_pending != null)
            {
                // Not applied yet: it will be applied with the newest timers.
                TacticalSnapshot pending = _pending;
                if (pending.Mode == TacticalMode.Disabled || pending.TurnLocks.Count != lockCount) return;
                pending.CurrentTimer = currentTimer;
                pending.RealTimer = realTimer;
                pending.SpinUpTimer = spinUpTimer;
                pending.EffectTime = effectTime;
                for (int i = 0; i < lockCount; i++) pending.TurnLocks[i] = locks[i];
                _pendingBody = null;
                return;
            }
            if (_appliedEpoch != epoch || _appliedSequence != sequence || !Plugin.IsMirroring) return;
            bool applied;
            using (HostApplyScope())
            {
                applied = _presentation.ApplyTimers(currentTimer, realTimer, spinUpTimer, effectTime, locks);
            }
            if (!applied) return;
            _appliedBody = null;
            PerfStats.Count("tactical.timers");
        }

        private static TacticalSnapshot ReadSnapshot(MessageReader reader, int epoch, int sequence, bool full)
        {
            byte mode = reader.Byte();
            if (mode > (byte)TacticalMode.RoundBased) throw new FormatException("tactical mode " + mode);
            var snapshot = new TacticalSnapshot
            {
                Epoch = epoch,
                Sequence = sequence,
                Full = full,
                Mode = (TacticalMode)mode,
                Settings = TacticalSettings.Read(reader),
            };
            int managerFlags = reader.Int();
            if ((managerFlags & ~ManagerAllFlags) != 0) throw new FormatException("tactical manager flags");
            snapshot.AutoPass = (managerFlags & ManagerAutoPass) != 0;
            snapshot.UnlimitedMovement = (managerFlags & ManagerUnlimitedMovement) != 0;
            snapshot.UnlimitedActions = (managerFlags & ManagerUnlimitedActions) != 0;
            snapshot.HasRoundStart = (managerFlags & ManagerHasRoundStart) != 0;
            snapshot.PartyInCombat = (managerFlags & ManagerPartyInCombat) != 0;
            snapshot.RefreshLocked = (managerFlags & ManagerRefreshLocked) != 0;
            snapshot.CurrentEventId = reader.Int();
            if (snapshot.CurrentEventId < 0) throw new FormatException("current tactical event ID");
            byte phase = reader.Byte();
            if (phase > (byte)TacticalPhase.Ending) throw new FormatException("tactical current phase");
            snapshot.Phase = (TacticalPhase)phase;
            snapshot.CurrentTimer = ReadTimer(reader);
            snapshot.RealTimer = ReadTimer(reader);
            snapshot.SpinUpTimer = ReadTimer(reader);
            snapshot.EffectTime = ReadTimer(reader);
            if (snapshot.CurrentEventId == 0 && snapshot.Phase != TacticalPhase.None
                || snapshot.CurrentEventId != 0 && snapshot.Phase == TacticalPhase.None)
                throw new FormatException("tactical current event/phase mismatch");

            int lockCount = reader.Int();
            CheckCount(lockCount, MaxTurnLocks, "turn locks");
            for (int i = 0; i < lockCount; i++) snapshot.TurnLocks.Add(ReadTimer(reader));

            int actorCount = reader.Int();
            CheckCount(actorCount, MaxActors, "actors");
            HashSet<Guid> actorIds = s_readActorIds;
            actorIds.Clear();
            for (int i = 0; i < actorCount; i++)
            {
                TacticalActorRecord actor = ReadActor(reader);
                if (!actorIds.Add(actor.Id)) throw new FormatException("duplicate tactical actor");
                snapshot.Actors.Add(actor);
            }

            int eventCount = reader.Int();
            CheckCount(eventCount, MaxEvents, "events");
            HashSet<int> eventIds = s_readEventIds;
            eventIds.Clear();
            for (int i = 0; i < eventCount; i++)
            {
                TacticalEventRecord record = ReadEvent(reader, actorIds);
                if (!eventIds.Add(record.Id)) throw new FormatException("duplicate tactical event");
                snapshot.Events.Add(record);
            }

            int queueCount = reader.Int();
            CheckCount(queueCount, MaxEvents, "event queue");
            HashSet<int> queued = s_readQueued;
            queued.Clear();
            for (int i = 0; i < queueCount; i++)
            {
                int id = reader.Int();
                if (!eventIds.Contains(id) || !queued.Add(id)) throw new FormatException("invalid tactical queue event ID");
                snapshot.Queue.Add(id);
            }
            if (snapshot.CurrentEventId != 0 && !eventIds.Contains(snapshot.CurrentEventId))
                throw new FormatException("current tactical event has no record");
            if (snapshot.Mode == TacticalMode.Disabled && (actorCount != 0 || eventCount != 0 || queueCount != 0
                || lockCount != 0 || snapshot.CurrentEventId != 0))
                throw new FormatException("disabled tactical snapshot contains combat state");
            return snapshot;
        }

        private static TacticalActorRecord ReadActor(MessageReader reader)
        {
            Guid id = reader.Guid();
            if (id == Guid.Empty) throw new FormatException("empty tactical actor ID");
            var actor = new TacticalActorRecord
            {
                Id = id,
                CurrentInitiative = ReadInitiative(reader),
                TempInitiative = ReadInitiative(reader),
                PreviousInitiativeIncrement = ReadInitiative(reader),
                PreviousBaseInitiative = ReadInitiative(reader),
                PreviousBaseReload = ReadInitiative(reader),
                InitiativeDelayFromReload = ReadInitiative(reader),
                AvailableMovement = ReadTimer(reader),
                TurnEventCount = reader.Int(),
            };
            if (actor.TurnEventCount < 0 || actor.TurnEventCount > MaxEvents) throw new FormatException("actor turn-event count");
            byte flags = reader.Byte();
            if ((flags & ~ActorAllFlags) != 0) throw new FormatException("tactical actor flags");
            actor.AvailableAction = (flags & ActorAvailableAction) != 0;
            actor.UsedFreePotion = (flags & ActorUsedFreePotion) != 0;
            actor.SwitchedWeapons = (flags & ActorSwitchedWeapons) != 0;
            actor.EvaluateTarget = (flags & ActorEvaluateTarget) != 0;
            int freeCount = reader.Int();
            CheckCount(freeCount, MaxFreeActions, "free actions");
            for (int i = 0; i < freeCount; i++) actor.FreeActions.Add(ReadAbilityKey(reader));
            return actor;
        }

        private static TacticalEventRecord ReadEvent(MessageReader reader, HashSet<Guid> actorIds)
        {
            var record = new TacticalEventRecord
            {
                Id = reader.Int(),
                Kind = (TacticalEventKind)reader.Byte(),
                ActorId = reader.Guid(),
            };
            if (record.Id <= 0 || record.Kind < TacticalEventKind.Turn || record.Kind > TacticalEventKind.ProjectileTurn)
                throw new FormatException("tactical event ID/kind");
            byte flags = reader.Byte();
            record.Active = (flags & 1) != 0;
            record.Preview = (flags & 2) != 0;
            record.Visible = (flags & 4) != 0;
            record.Obscured = (flags & 8) != 0;
            record.SurpriseRound = (flags & 16) != 0;
            record.SkipTurnStart = (flags & 32) != 0;
            record.SkipTurnEnd = (flags & 64) != 0;
            record.SafeDestroy = (flags & 128) != 0;
            byte passStyle = reader.Byte();
            if (passStyle > (byte)PassTurnStyle.UI) throw new FormatException("tactical pass style");
            record.PassStyle = (PassTurnStyle)passStyle;
            record.Round = reader.Int();
            if (record.Round < 0 || record.Round > MaxRound) throw new FormatException("tactical round");
            record.InitiativeDelay = ReadInitiative(reader);
            record.InitiativeSort = ReadInitiative(reader);
            record.PreviewInitiativeModifier = ReadInitiative(reader);
            record.InitiativeBias = reader.Int();
            if (record.InitiativeBias < -1 || record.InitiativeBias > MaxInitiativeBias)
                throw new FormatException("tactical initiative bias");
            record.Duration = ReadTimer(reader);

            bool needsActor = record.Kind == TacticalEventKind.Turn || record.Kind == TacticalEventKind.CastFinished
                || record.Kind == TacticalEventKind.ProjectileTurn;
            if (needsActor && (record.ActorId == Guid.Empty || !actorIds.Contains(record.ActorId))
                || !needsActor && record.ActorId != Guid.Empty)
                throw new FormatException("tactical event actor");
            if (record.Kind != TacticalEventKind.Turn && record.PassStyle != PassTurnStyle.Forced)
                throw new FormatException("non-turn tactical pass style");

            if (record.Kind == TacticalEventKind.CastFinished)
            {
                string attackName = ReadName(reader, "attack");
                int attackOccurrence = reader.Int();
                if (attackOccurrence < 0 || attackOccurrence > MaxAttackOccurrence)
                    throw new FormatException("cast attack occurrence");
                record.AttackTime = ReadTimer(reader);
                record.Attack = new TacticalAttackIdentity
                {
                    Name = attackName,
                    Occurrence = attackOccurrence,
                    Ability = ReadOptionalAbilityKey(reader),
                };
            }
            return record;
        }

        private static TacticalAbilityKey ReadAbilityKey(MessageReader reader)
        {
            string name = ReadName(reader, "ability");
            int displayId = reader.Int(), occurrence = reader.Int();
            if (displayId < -1 || displayId > MaxDisplayStringId || occurrence < 0 || occurrence > MaxAbilityOccurrence)
                throw new FormatException("ability identity");
            return new TacticalAbilityKey(name, displayId, occurrence);
        }

        private static TacticalAbilityKey ReadOptionalAbilityKey(MessageReader reader)
        {
            byte present = reader.Byte();
            if (present > 1) throw new FormatException("optional cast ability flag");
            return present != 0 ? ReadAbilityKey(reader) : TacticalAbilityKey.None;
        }

        private static string ReadName(MessageReader reader, string kind)
        {
            string raw = reader.String();
            if (raw.Length == 0 || raw.Length > MaxAbilityNameLength) throw new FormatException(kind + " name length");
            string clean = Protocol.Clean(raw, MaxAbilityNameLength);
            if (clean != raw) throw new FormatException("non-canonical " + kind + " name");
            return clean;
        }

        private static float ReadTimer(MessageReader reader)
        {
            float value = reader.Float();
            if (!Finite(value) || value < 0f || value > MaxTimer) throw new FormatException("tactical timer");
            return value;
        }

        private static float ReadInitiative(MessageReader reader)
        {
            float value = reader.Float();
            if (!Finite(value) || value < -MaxInitiative || value > MaxInitiative) throw new FormatException("tactical initiative");
            return value;
        }

        private static void CheckCount(int count, int max, string name)
        {
            if (count < 0 || count > max) throw new FormatException("tactical " + name + " count " + count);
        }

        private void RequestResync()
        {
            float now = Time.unscaledTime;
            if (now < _nextResync) return;
            _nextResync = now + ResyncInterval;
            _lastResync = now;
            PerfStats.Count("tactical.resync");
            TacticalOrders.CaptureResync();
        }

        // ---- guest dependency resolution ----

        private void GuestUpdate(float now)
        {
            if (_pending == null || now < _nextResolve || !Plugin.IsMirroring) return;
            TacticalSnapshot snapshot = _pending;
            long started = PerfStats.Begin();
            bool applied = false;
            try
            {
                if (TryResolve(snapshot, out TacticalResolvedSnapshot resolved))
                {
                    using (HostApplyScope())
                    {
                        applied = _presentation.Apply(resolved);
                    }
                }
            }
            finally { PerfStats.End("tactical.apply", started); }
            if (!applied)
            {
                // The same snapshot fails the same way until its actors exist here;
                // a newer snapshot clears the delay.
                _nextResolve = now + ResolveRetryInterval;
                PerfStats.Count("tactical.retry");
                return;
            }
            PerfStats.Count(snapshot.Full ? "tactical.full" : "tactical.delta");
            PerfStats.Count("tactical.records", snapshot.Actors.Count + snapshot.Events.Count);
            CurrentEpoch = snapshot.Mode == TacticalMode.Disabled ? 0 : snapshot.Epoch;
            CurrentEventId = snapshot.Mode == TacticalMode.Disabled ? 0 : snapshot.CurrentEventId;
            _appliedEpoch = snapshot.Epoch;
            _appliedSequence = snapshot.Sequence;
            _appliedBody = _pendingBody;
            _lastApply = Time.unscaledTime;
            _pending = null;
            _pendingBody = null;
        }

        private static bool TryResolve(TacticalSnapshot snapshot, out TacticalResolvedSnapshot resolved)
        {
            resolved = new TacticalResolvedSnapshot { Snapshot = snapshot };
            foreach (TacticalActorRecord actorRecord in snapshot.Actors)
            {
                GameObject obj = ResolveObject(actorRecord.Id);
                AIController actor = obj != null ? GameUtilities.FindActiveAIController(obj) : null;
                CharacterStats stats = actor != null ? actor.CharacterStats : null;
                if (actor == null || stats == null) return false;
                var actorResolution = new TacticalActorResolution { Actor = actor, Stats = stats };
                foreach (TacticalAbilityKey key in actorRecord.FreeActions)
                {
                    GenericAbility ability = FindAbility(stats, key);
                    if (ability == null) return false;
                    actorResolution.FreeActions.Add(ability);
                }
                resolved.Actors.Add(actorRecord.Id, actorResolution);
            }
            foreach (TacticalEventRecord eventRecord in snapshot.Events)
            {
                if (eventRecord.Kind != TacticalEventKind.CastFinished) continue;
                TacticalActorResolution actor = resolved.Actors[eventRecord.ActorId];
                AttackBase attack = FindAttack(actor.Actor, actor.Stats, eventRecord.Attack);
                if (attack == null) return false;
                resolved.CastAttacks.Add(eventRecord.Id, attack);
            }
            return true;
        }

        private static GenericAbility FindAbility(CharacterStats stats, TacticalAbilityKey key)
        {
            int occurrence = 0;
            IList<GenericAbility> abilities = stats.ActiveAbilities;
            if (abilities == null) return null;
            for (int i = 0; i < abilities.Count; i++)
            {
                GenericAbility ability = abilities[i];
                if (ability == null || NormalizeName(ability.name) != key.Name) continue;
                int displayId = ability.DisplayName != null ? ability.DisplayName.StringID : -1;
                if (displayId != key.DisplayId) continue;
                if (occurrence++ == key.Occurrence) return ability;
            }
            return null;
        }

        private static AttackBase FindAttack(AIController actor, CharacterStats stats, TacticalAttackIdentity identity)
        {
            if (identity.Ability.Occurrence >= 0)
            {
                GenericAbility ability = FindAbility(stats, identity.Ability);
                if (ability == null) return null;
                var candidates = new List<AttackBase>();
                if (ability.Attack != null) candidates.Add(ability.Attack);
                foreach (AttackBase candidate in ability.GetComponents<AttackBase>())
                    if (candidate != null && !candidates.Contains(candidate)) candidates.Add(candidate);
                return FindAttackOccurrence(candidates, identity);
            }
            return FindAttackOccurrence(actor.GetComponentsInChildren<AttackBase>(true), identity);
        }

        private static AttackBase FindAttackOccurrence(IEnumerable<AttackBase> candidates, TacticalAttackIdentity identity)
        {
            int occurrence = 0;
            foreach (AttackBase attack in candidates)
            {
                if (attack == null || NormalizeName(attack.name) != identity.Name) continue;
                if (occurrence++ == identity.Occurrence) return attack;
            }
            return null;
        }

        // ---- shared identity and bounds ----

        private struct AbilityKeyEntry
        {
            public CharacterStats Stats;
            public int AbilityCount;
            public TacticalAbilityKey Key;
        }

        private struct CastAttackEntry
        {
            public AIController Actor;
            public AttackBase Attack;
            public int AbilityCount;
            public TacticalAttackIdentity Identity;
        }

        private static int AbilityCount(CharacterStats stats)
        {
            IList<GenericAbility> abilities = (object)stats != null ? stats.ActiveAbilities : null;
            return abilities != null ? abilities.Count : -1;
        }

        private TacticalAbilityKey CachedAbilityKey(CharacterStats stats, GenericAbility ability)
        {
            int count = AbilityCount(stats);
            if (_abilityKeys.TryGetValue(ability, out AbilityKeyEntry entry)
                && ReferenceEquals(entry.Stats, stats) && entry.AbilityCount == count) return entry.Key;
            TacticalAbilityKey key = CaptureAbilityKey(stats, ability);
            _abilityKeys[ability] = new AbilityKeyEntry { Stats = stats, AbilityCount = count, Key = key };
            return key;
        }

        private TacticalAttackIdentity CachedAttackIdentity(TacticalCastFinishedEvent cast)
        {
            AIController actor = cast.Actor;
            AttackBase attack = cast.Attack;
            int count = (object)actor != null ? AbilityCount(actor.CharacterStats) : -1;
            if (_castAttacks.TryGetValue(cast, out CastAttackEntry entry) && ReferenceEquals(entry.Actor, actor)
                && ReferenceEquals(entry.Attack, attack) && entry.AbilityCount == count) return entry.Identity;
            TacticalAttackIdentity identity = CaptureAttackIdentity(actor, attack);
            _castAttacks[cast] = new CastAttackEntry { Actor = actor, Attack = attack, AbilityCount = count, Identity = identity };
            return identity;
        }

        private static TacticalAbilityKey CaptureAbilityKey(CharacterStats stats, GenericAbility ability)
        {
            string name = NormalizeName(ability.name);
            int displayId = ability.DisplayName != null ? ability.DisplayName.StringID : -1;
            int occurrence = 0;
            foreach (GenericAbility candidate in stats.ActiveAbilities)
            {
                if (candidate == null || NormalizeName(candidate.name) != name) continue;
                int candidateDisplay = candidate.DisplayName != null ? candidate.DisplayName.StringID : -1;
                if (candidateDisplay != displayId) continue;
                if (candidate == ability) return new TacticalAbilityKey(name, displayId, occurrence);
                occurrence++;
            }
            throw new InvalidOperationException("ability is not in its owner's ActiveAbilities");
        }

        private static TacticalAttackIdentity CaptureAttackIdentity(AIController actor, AttackBase attack)
        {
            var identity = new TacticalAttackIdentity { Name = NormalizeName(attack.name) };
            if (identity.Name.Length == 0 || identity.Name.Length > MaxAbilityNameLength)
                throw new InvalidOperationException("invalid cast attack name");
            GenericAbility ability = attack.AbilityOrigin;
            if (ability != null)
            {
                identity.Ability = CaptureAbilityKey(actor.CharacterStats, ability);
                var candidates = new List<AttackBase>();
                if (ability.Attack != null) candidates.Add(ability.Attack);
                foreach (AttackBase candidate in ability.GetComponents<AttackBase>())
                    if (candidate != null && !candidates.Contains(candidate)) candidates.Add(candidate);
                identity.Occurrence = AttackOccurrence(candidates, attack);
            }
            else identity.Occurrence = AttackOccurrence(actor.GetComponentsInChildren<AttackBase>(true), attack);
            if (identity.Occurrence < 0 || identity.Occurrence > MaxAttackOccurrence)
                throw new InvalidOperationException("cast attack occurrence cannot be identified");
            return identity;
        }

        private static int AttackOccurrence(IEnumerable<AttackBase> candidates, AttackBase sought)
        {
            int occurrence = 0;
            foreach (AttackBase attack in candidates)
            {
                if (attack == null || NormalizeName(attack.name) != NormalizeName(sought.name)) continue;
                if (attack == sought) return occurrence;
                occurrence++;
            }
            return -1;
        }

        internal static string NormalizeName(string name)
        {
            return Protocol.Clean((name ?? "").Replace("(Clone)", ""), MaxAbilityNameLength);
        }

        private static Guid NetworkId(GameObject obj)
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

        private static float BoundedTimer(float value)
        {
            return !Finite(value) || value <= 0f ? 0f : Mathf.Min(value, MaxTimer);
        }

        private static float BoundedInitiative(float value)
        {
            if (!Finite(value)) throw new InvalidOperationException("non-finite tactical initiative");
            return Mathf.Clamp(value, -MaxInitiative, MaxInitiative);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Compares two serialized snapshots, ignoring the sequence and full flag.</summary>
        private static bool SameSnapshot(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length < BodyOffset) return false;
            for (int i = 0; i < SequenceOffset; i++) if (a[i] != b[i]) return false;
            for (int i = BodyOffset; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// True when two serialized snapshots differ at most in sequence, full
        /// flag, the four manager timers and the turn-lock remaining times.
        /// </summary>
        private static bool SameStructure(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length < LockCountOffset + 4) return false;
            for (int i = 0; i < SequenceOffset; i++) if (a[i] != b[i]) return false;
            for (int i = BodyOffset; i < TimersOffset; i++) if (a[i] != b[i]) return false;
            for (int i = LockCountOffset; i < LockCountOffset + 4; i++) if (a[i] != b[i]) return false;
            int locks = a[LockCountOffset] | a[LockCountOffset + 1] << 8 | a[LockCountOffset + 2] << 16 | a[LockCountOffset + 3] << 24;
            if (locks < 0 || locks > MaxTurnLocks) return false;
            int rest = LockCountOffset + 4 + locks * 4;
            if (rest > a.Length) return false;
            for (int i = rest; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Confirms that the bytes SameStructure skipped are this snapshot's timers,
        /// so a layout change can never hide a structural difference.
        /// </summary>
        private static bool TimerLayoutMatches(byte[] data, TacticalSnapshot snapshot)
        {
            if (!BitConverter.IsLittleEndian || data.Length < LockCountOffset + 4 + snapshot.TurnLocks.Count * 4) return false;
            if (BitConverter.ToSingle(data, TimersOffset) != snapshot.CurrentTimer
                || BitConverter.ToSingle(data, TimersOffset + 4) != snapshot.RealTimer
                || BitConverter.ToSingle(data, TimersOffset + 8) != snapshot.SpinUpTimer
                || BitConverter.ToSingle(data, TimersOffset + 12) != snapshot.EffectTime
                || BitConverter.ToInt32(data, LockCountOffset) != snapshot.TurnLocks.Count) return false;
            for (int i = 0; i < snapshot.TurnLocks.Count; i++)
                if (BitConverter.ToSingle(data, LockCountOffset + 4 + i * 4) != snapshot.TurnLocks[i]) return false;
            return true;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private void CaptureWarning(string text)
        {
            float now = Time.unscaledTime;
            if (now < _nextCaptureWarning) return;
            _nextCaptureWarning = now + 2f;
            Plugin.Log.LogWarning(text);
        }
    }

    internal enum TacticalPhase : byte { None = 0, Starting = 1, Active = 2, Ending = 3 }
    internal enum TacticalEventKind : byte { Turn = 1, CastFinished = 2, RoundStart = 3, Delay = 4, TurnLocked = 5, ProjectileTurn = 6 }

    internal struct TacticalAbilityKey
    {
        public string Name;
        public int DisplayId;
        public int Occurrence;
        public static TacticalAbilityKey None => new TacticalAbilityKey { DisplayId = -1, Occurrence = -1, Name = "" };
        public TacticalAbilityKey(string name, int displayId, int occurrence)
        {
            Name = name;
            DisplayId = displayId;
            Occurrence = occurrence;
        }
    }

    internal struct TacticalAttackIdentity
    {
        public string Name;
        public int Occurrence;
        public TacticalAbilityKey Ability;
    }

    internal sealed class TacticalSettings
    {
        public float EffectTimePerRound, EnterCombatBaseRecovery, DelayTurnStep, NextTurnStep;
        public float BaseRecovery, PostBaseRecovery, RecoveryMultiplier, ReloadMultiplier;
        public int VisibleRounds;
        public float WeakestInterrupt, AverageInterrupt, StrongestInterrupt;
        public float IntellectDurationBonus, DualWieldingMultiplier, MovementTimePerRound, ImmediateTimeThreshold;

        public static TacticalSettings Capture()
        {
            return new TacticalSettings
            {
                EffectTimePerRound = TacticalModeSettingsComponent.EffectTimePerRound,
                EnterCombatBaseRecovery = TacticalModeSettingsComponent.EnterCombatBaseRecovery,
                DelayTurnStep = TacticalModeSettingsComponent.DelayTurnStep,
                NextTurnStep = TacticalModeSettingsComponent.NextTurnStep,
                BaseRecovery = TacticalModeSettingsComponent.BaseRecovery,
                PostBaseRecovery = TacticalModeSettingsComponent.PostBaseRecovery,
                RecoveryMultiplier = TacticalModeSettingsComponent.BaseRecoveryMultiplier,
                ReloadMultiplier = TacticalModeSettingsComponent.ReloadMultiplier,
                VisibleRounds = TacticalModeSettingsComponent.VisibleRounds,
                WeakestInterrupt = TacticalModeSettingsComponent.WeakestTacticalInterupt,
                AverageInterrupt = TacticalModeSettingsComponent.AverageTacticalInterupt,
                StrongestInterrupt = TacticalModeSettingsComponent.StrongestTacticalInterupt,
                IntellectDurationBonus = TacticalModeSettingsComponent.TacticalIntellectDurationBonus,
                DualWieldingMultiplier = TacticalModeSettingsComponent.TacticalDualWieldingMult,
                MovementTimePerRound = TacticalModeSettingsComponent.MovementTimePerRound,
                ImmediateTimeThreshold = TacticalModeSettingsComponent.ImmediateTimeThreshold,
            };
        }

        /// <summary>True when the live round settings still equal this capture.</summary>
        public bool MatchesCurrent()
        {
            return EffectTimePerRound == TacticalModeSettingsComponent.EffectTimePerRound
                && EnterCombatBaseRecovery == TacticalModeSettingsComponent.EnterCombatBaseRecovery
                && DelayTurnStep == TacticalModeSettingsComponent.DelayTurnStep
                && NextTurnStep == TacticalModeSettingsComponent.NextTurnStep
                && BaseRecovery == TacticalModeSettingsComponent.BaseRecovery
                && PostBaseRecovery == TacticalModeSettingsComponent.PostBaseRecovery
                && RecoveryMultiplier == TacticalModeSettingsComponent.BaseRecoveryMultiplier
                && ReloadMultiplier == TacticalModeSettingsComponent.ReloadMultiplier
                && VisibleRounds == TacticalModeSettingsComponent.VisibleRounds
                && WeakestInterrupt == TacticalModeSettingsComponent.WeakestTacticalInterupt
                && AverageInterrupt == TacticalModeSettingsComponent.AverageTacticalInterupt
                && StrongestInterrupt == TacticalModeSettingsComponent.StrongestTacticalInterupt
                && IntellectDurationBonus == TacticalModeSettingsComponent.TacticalIntellectDurationBonus
                && DualWieldingMultiplier == TacticalModeSettingsComponent.TacticalDualWieldingMult
                && MovementTimePerRound == TacticalModeSettingsComponent.MovementTimePerRound
                && ImmediateTimeThreshold == TacticalModeSettingsComponent.ImmediateTimeThreshold;
        }

        public void Write(MessageWriter writer)
        {
            writer.Float(EffectTimePerRound).Float(EnterCombatBaseRecovery).Float(DelayTurnStep).Float(NextTurnStep)
                .Float(BaseRecovery).Float(PostBaseRecovery).Float(RecoveryMultiplier).Float(ReloadMultiplier)
                .Int(VisibleRounds).Float(WeakestInterrupt).Float(AverageInterrupt).Float(StrongestInterrupt)
                .Float(IntellectDurationBonus).Float(DualWieldingMultiplier).Float(MovementTimePerRound).Float(ImmediateTimeThreshold);
        }

        public static TacticalSettings Read(MessageReader reader)
        {
            var settings = new TacticalSettings
            {
                EffectTimePerRound = ReadSetting(reader, true),
                EnterCombatBaseRecovery = ReadSetting(reader, false),
                DelayTurnStep = ReadSetting(reader, false),
                NextTurnStep = ReadSetting(reader, false),
                BaseRecovery = ReadSetting(reader, false),
                PostBaseRecovery = ReadSetting(reader, false),
                RecoveryMultiplier = ReadSetting(reader, true),
                ReloadMultiplier = ReadSetting(reader, true),
                VisibleRounds = reader.Int(),
                WeakestInterrupt = ReadSetting(reader, false),
                AverageInterrupt = ReadSetting(reader, false),
                StrongestInterrupt = ReadSetting(reader, false),
                IntellectDurationBonus = ReadSetting(reader, false),
                DualWieldingMultiplier = ReadSetting(reader, true),
                MovementTimePerRound = ReadSetting(reader, true),
                ImmediateTimeThreshold = ReadSetting(reader, false),
            };
            if (settings.VisibleRounds < 0 || settings.VisibleRounds > 16) throw new FormatException("visible tactical rounds");
            return settings;
        }

        private static float ReadSetting(MessageReader reader, bool positive)
        {
            float value = reader.Float();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 86400f || positive && value <= 0f)
                throw new FormatException("tactical round setting");
            return value;
        }

        public void Apply()
        {
            TacticalModeSettingsComponent.m_EffectTimePerRound = EffectTimePerRound;
            TacticalModeSettingsComponent.m_EnterCombatBaseRecovery = EnterCombatBaseRecovery;
            TacticalModeSettingsComponent.m_DelayTurnStep = DelayTurnStep;
            TacticalModeSettingsComponent.m_NextTurnStep = NextTurnStep;
            TacticalModeSettingsComponent.m_BaseRecovery = BaseRecovery;
            TacticalModeSettingsComponent.m_PostBaseRecovery = PostBaseRecovery;
            TacticalModeSettingsComponent.m_RecoveryMultiplier = RecoveryMultiplier;
            TacticalModeSettingsComponent.m_ReloadMultiplier = ReloadMultiplier;
            TacticalModeSettingsComponent.m_VisibleRounds = VisibleRounds;
            TacticalModeSettingsComponent.m_WeakestTacticalInterupt = WeakestInterrupt;
            TacticalModeSettingsComponent.m_AverageTacticalInterupt = AverageInterrupt;
            TacticalModeSettingsComponent.m_StrongestTacticalInterupt = StrongestInterrupt;
            TacticalModeSettingsComponent.m_TacticalIntellectDurationBonus = IntellectDurationBonus;
            TacticalModeSettingsComponent.m_TacticalDualWieldingMult = DualWieldingMultiplier;
        }
    }

    internal sealed class TacticalSnapshot
    {
        public int Epoch, Sequence;
        public bool Full;
        public TacticalMode Mode;
        public TacticalSettings Settings;
        public bool AutoPass, UnlimitedMovement, UnlimitedActions, HasRoundStart, PartyInCombat, RefreshLocked;
        public int CurrentEventId;
        public TacticalPhase Phase;
        public float CurrentTimer, RealTimer, SpinUpTimer, EffectTime;
        public readonly List<float> TurnLocks = new List<float>();
        public readonly List<TacticalActorRecord> Actors = new List<TacticalActorRecord>();
        public readonly List<TacticalEventRecord> Events = new List<TacticalEventRecord>();
        public readonly List<int> Queue = new List<int>();
    }

    internal sealed class TacticalActorRecord
    {
        public Guid Id;
        public float CurrentInitiative, TempInitiative, PreviousInitiativeIncrement;
        public float PreviousBaseInitiative, PreviousBaseReload, InitiativeDelayFromReload, AvailableMovement;
        public int TurnEventCount;
        public bool AvailableAction, UsedFreePotion, SwitchedWeapons, EvaluateTarget;
        public readonly List<TacticalAbilityKey> FreeActions = new List<TacticalAbilityKey>();
    }

    internal sealed class TacticalEventRecord
    {
        public int Id;
        public TacticalEventKind Kind;
        public Guid ActorId;
        public bool Active, Preview, Visible, Obscured, SurpriseRound, SkipTurnStart, SkipTurnEnd, SafeDestroy;
        public PassTurnStyle PassStyle;
        public int Round;
        public float InitiativeDelay, InitiativeSort, PreviewInitiativeModifier;
        public int InitiativeBias;
        public float Duration, AttackTime;
        public TacticalAttackIdentity Attack;
    }

    internal sealed class TacticalActorResolution
    {
        public AIController Actor;
        public CharacterStats Stats;
        public readonly List<GenericAbility> FreeActions = new List<GenericAbility>();
    }

    internal sealed class TacticalResolvedSnapshot
    {
        public TacticalSnapshot Snapshot;
        public readonly Dictionary<Guid, TacticalActorResolution> Actors = new Dictionary<Guid, TacticalActorResolution>();
        public readonly Dictionary<int, AttackBase> CastAttacks = new Dictionary<int, AttackBase>();
    }
}
