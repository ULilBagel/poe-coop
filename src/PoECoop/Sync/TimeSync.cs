using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Full host snapshots for shared pause, speed and the persistent world clock.
    /// FlowCoordinator owns world context and parses FlowCommand/FlowResult envelopes.
    /// </summary>
    public sealed class TimeSync : IDisposable
    {
        public const FlowDomain Domain = FlowDomain.Pause;

        public enum PauseKind
        {
            Pause = 1,
            Resume = 2,
            Slow = 3,
            Fast = 4,
            Normal = 5,
        }

        private const float SendInterval = 0.1f;
        private const float FullRefreshInterval = 2f;
        private const float MaxTimeScale = 16f;
        private const int MaxPendingRequests = 64;
        private const int MaxCachedResultsPerPlayer = 256;

        private const int PlayerPausedFlag = 1;
        private const int UiPausedFlag = 2;
        private const int BarrierPausedFlag = 4;
        private const int TurnLockedFlag = 8;
        private const int AllFlags = 15;

        private static readonly AccessTools.FieldRef<TimeController, bool> PlayerPaused =
            AccessTools.FieldRefAccess<TimeController, bool>("m_PlayerPaused");
        private static readonly AccessTools.FieldRef<TimeController, bool> UiPaused =
            AccessTools.FieldRefAccess<TimeController, bool>("m_UiPaused");
        private static readonly AccessTools.FieldRef<TimeController, float> SelectedTimeScale =
            AccessTools.FieldRefAccess<TimeController, float>("m_TimeScale");
        private static readonly MethodInfo TimeScaleSetter =
            AccessTools.PropertySetter(typeof(TimeController), "TimeScale");
        private static readonly MethodInfo UpdateDayNightCycle =
            AccessTools.Method(typeof(WorldTime), "UpdateDayNightCycle");

        [ThreadStatic]
        private static int s_applyDepth;

        private static TimeSync s_instance;
        internal static TimeSync Instance => s_instance;

        private readonly Session _session;
        private long _generation;
        private bool _wasHost, _wasGuest;

        private long _worldEpoch;
        private bool _worldReady;
        private bool _barrierPaused = true;

        private long _sequence;
        private float _nextSend, _nextFullRefresh;
        private Snapshot _sent;
        private bool _hasSent;
        private bool _sequenceExhaustedLogged;

        private long _guestEpoch = -1, _guestSequence = -1;
        private Snapshot _guestSnapshot;
        private bool _hasGuestSnapshot;

        private long _nextRequestId = 1;
        private readonly Dictionary<long, PauseKind> _pendingRequests = new Dictionary<long, PauseKind>();
        private readonly Dictionary<int, RequestCache> _requestCache = new Dictionary<int, RequestCache>();

        private struct Snapshot
        {
            public int Flags;
            public float SelectedRtwP, Tactical, Effective;
            public int Current, AdventureStart, Travel;
        }

        private struct CachedResult
        {
            public PauseKind Kind;
            public FlowResultCode Result;
            public long StateSequence;
        }

        private sealed class RequestCache
        {
            public readonly Dictionary<long, CachedResult> Results = new Dictionary<long, CachedResult>();
            public readonly Queue<long> Order = new Queue<long>();
        }

        public static bool ApplyingHostState => s_applyDepth > 0;

        public static bool SharedPaused
        {
            get
            {
                TimeSync self = s_instance;
                if (self == null) return false;
                if (self._session.IsGuest)
                    return self._hasGuestSnapshot && (self._guestSnapshot.Flags & PlayerPausedFlag) != 0;
                TimeController time = TimeController.Instance;
                return time != null && PlayerPaused(time);
            }
        }

        public TimeSync(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _generation = session.Generation;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.WorldStateReady += OnWorldStateReady;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelLoaded;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.WorldStateReady -= OnWorldStateReady;
            _session.FullStateRequested -= OnFullStateRequested;
            GameState.OnLevelLoaded -= OnLevelLoaded;
            bool restoreLocal = _session.IsGuest;
            if (s_instance == this) s_instance = null;
            if (restoreLocal) ClearGuestSnapshot(true);
            _pendingRequests.Clear();
            _requestCache.Clear();
        }

        public long WorldEpoch => _worldEpoch;
        public long StateSequence => _sequence;
        public long GuestSequence => _guestSequence;
        public bool HasGuestSnapshot => _hasGuestSnapshot;
        public bool BarrierPaused => _barrierPaused;
        public bool TurnLocked => _hasGuestSnapshot
            ? (_guestSnapshot.Flags & TurnLockedFlag) != 0
            : TacticalModeManager.Instance != null && TacticalModeManager.IsInTacticalCombat()
                && TacticalModeManager.Instance.TurnLocked;

        /// <summary>Called by FlowCoordinator after it parses a Pause command envelope.</summary>
        public void HandleFlowCommand(PlayerInfo sender, long worldEpoch, long requestId, int kind, MessageReader reader)
        {
            SyncContext();
            reader.Finish();
            if (_session.IsHost) HostCommand(sender, worldEpoch, requestId, kind);
        }

        /// <summary>Called by FlowCoordinator after it parses a Pause result envelope.</summary>
        public void HandleFlowResult(long worldEpoch, long requestId, int kind, FlowResultCode result, long stateSeq)
        {
            SyncContext();
            if (_session.IsGuest) GuestResult(worldEpoch, requestId, kind, result, stateSeq);
        }

        public static void SendLatestTo(int playerId)
        {
            TimeSync self = s_instance;
            if (self == null) return;
            self.SyncContext();
            if (self._session.IsHost && self._worldReady && self.CanCapture()
                && (playerId < 0 || self._session.IsPeerReady(playerId))) self.SendSnapshot(playerId);
        }

        public static bool RequestPause(bool paused)
        {
            return SendRequest(paused ? PauseKind.Pause : PauseKind.Resume);
        }

        public static void HostPauseControl(bool paused)
        {
            TimeSync self = s_instance;
            TimeController time = TimeController.Instance;
            if (self == null || !self._session.IsHost || time == null) return;
            ApplySafePaused(time, paused);
        }

        public static bool RequestSlowToggle()
        {
            TimeSync self = s_instance;
            if (self == null) return false;
            bool selected = self._hasGuestSnapshot && TimeController.Instance != null
                && Mathf.Approximately(self._guestSnapshot.SelectedRtwP, TimeController.Instance.SlowTime);
            return SendRequest(selected ? PauseKind.Normal : PauseKind.Slow);
        }

        public static bool RequestFastToggle()
        {
            TimeSync self = s_instance;
            if (self == null) return false;
            bool selected = self._hasGuestSnapshot && TimeController.Instance != null
                && Mathf.Approximately(self._guestSnapshot.SelectedRtwP, TimeController.Instance.FastTime);
            return SendRequest(selected ? PauseKind.Normal : PauseKind.Fast);
        }

        public static bool RequestNormalSpeed() => SendRequest(PauseKind.Normal);

        private static bool SendRequest(PauseKind kind)
        {
            TimeSync self = s_instance;
            if (self == null) return false;
            self.SyncContext();
            if (!self._session.IsGuest || !self._worldReady || self._barrierPaused
                || self._worldEpoch <= 0 || self._nextRequestId <= 0
                || self._pendingRequests.Count >= MaxPendingRequests) return false;

            long requestId = self._nextRequestId;
            self._nextRequestId = requestId == long.MaxValue ? -1 : requestId + 1;
            byte[] data = FlowWire.WriteCommand(self._worldEpoch, requestId, Domain, (int)kind).ToArray();
            self._pendingRequests.Add(requestId, kind);
            self._session.SendToHost(data);
            return true;
        }

        public void Update(float now)
        {
            SyncContext();
            if (_session.IsHost)
            {
                if (_barrierPaused && TimeController.Instance != null) Time.timeScale = 0f;
                if (!_worldReady || _session.Players.Count < 2 || !CanCapture() || now < _nextSend) return;
                _nextSend = now + SendInterval;
                bool full = now >= _nextFullRefresh;
                if (full) _nextFullRefresh = now + FullRefreshInterval;
                Snapshot snapshot = Capture();
                if (!full && _hasSent && Same(_sent, snapshot)) return;
                SendSnapshot(0);
            }
            else if (_session.IsGuest)
            {
                ApplyGuestSnapshot();
            }
        }

        private void SyncContext()
        {
            SyncSession();
            long epoch = FlowCoordinator.WorldEpoch;
            bool ready = _session.IsWorldReady;
            bool barrier = FlowCoordinator.ApplyingWorldBarrier;
            if (_worldEpoch != epoch)
            {
                ResetWorld();
                _worldEpoch = epoch;
            }
            if (_worldReady == ready && _barrierPaused == barrier) return;
            _worldReady = ready;
            _barrierPaused = barrier;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            if (_session.IsHost && barrier && TimeController.Instance != null) Time.timeScale = 0f;
            else if (_session.IsGuest) ApplyGuestSnapshot();
        }

        private void SyncSession()
        {
            bool host = _session.IsHost, guest = _session.IsGuest;
            if (_generation == _session.Generation && host == _wasHost && guest == _wasGuest) return;
            bool clearNativePause = _wasGuest;
            _generation = _session.Generation;
            _wasHost = host;
            _wasGuest = guest;
            _requestCache.Clear();
            _pendingRequests.Clear();
            ResetWorld();
            _worldEpoch = 0;
            _worldReady = false;
            _barrierPaused = true;
            if (clearNativePause) ClearGuestSnapshot(true);
        }

        private void ResetWorld()
        {
            _sequence = 0;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            _hasSent = false;
            _sequenceExhaustedLogged = false;
            _guestEpoch = -1;
            _guestSequence = -1;
            _nextRequestId = 1;
            _pendingRequests.Clear();
            _requestCache.Clear();
            ClearGuestSnapshot(true);
        }

        private bool CanCapture()
        {
            return GameState.IsInGameSession && !GameState.IsLoading
                && TimeController.Instance != null && WorldTime.Instance != null && GameState.Option != null;
        }

        private Snapshot Capture()
        {
            TimeController time = TimeController.Instance;
            WorldTime world = WorldTime.Instance;
            int flags = 0;
            if (PlayerPaused(time)) flags |= PlayerPausedFlag;
            if (UiPaused(time)) flags |= UiPausedFlag;
            if (_barrierPaused) flags |= BarrierPausedFlag;
            TacticalModeManager tactical = TacticalModeManager.Instance;
            if (tactical != null && TacticalModeManager.IsInTacticalCombat() && tactical.TurnLocked)
                flags |= TurnLockedFlag;

            return new Snapshot
            {
                Flags = flags,
                SelectedRtwP = BoundedTimeScale(SelectedTimeScale(time), time.NormalTime, false),
                Tactical = BoundedTimeScale(GameState.Option.CombatSpeed, 1f, false),
                Effective = SharedEffectiveTimeScale(time),
                Current = BoundedSeconds(world.CurrentTime != null ? world.CurrentTime.TotalSeconds : 0),
                AdventureStart = BoundedSeconds(world.AdventureStart != null ? world.AdventureStart.TotalSeconds : 0),
                Travel = BoundedSeconds(world.TimeSpentTravelling != null ? world.TimeSpentTravelling.TotalSeconds() : 0),
            };
        }

        private float SharedEffectiveTimeScale(TimeController time)
        {
            if (_barrierPaused) return 0f;
            float value;
            if (time.OverrideTime != -1f && !PlayerPaused(time)) value = time.OverrideTime;
            else if (PlayerPaused(time) && !time.ProhibitPause) value = 0f;
            else if (Cutscene.CutsceneActive) value = 1f;
            else if (GameState.InCombat)
                value = TacticalModeManager.IsInTacticalCombat()
                    ? GameState.Option.CombatSpeed : SelectedTimeScale(time);
            else value = SelectedTimeScale(time) * time.DebugTimeScaleMod;
            return BoundedTimeScale(value, 0f, true);
        }

        private long SendSnapshot(int targetPlayerId)
        {
            if (_sequence == long.MaxValue)
            {
                if (!_sequenceExhaustedLogged)
                {
                    _sequenceExhaustedLogged = true;
                    Plugin.Log.LogWarning("Time snapshot sequence exhausted; waiting for the next world epoch");
                }
                return _sequence;
            }

            Snapshot snapshot = Capture();
            long next = _sequence + 1;
            byte[] data = Serialize(_worldEpoch, next, snapshot);
            if (targetPlayerId < 0 || targetPlayerId == Session.HostId) _session.SendToGuests(data);
            else _session.SendToPlayer(targetPlayerId, data);
            _sequence = next;
            _sent = snapshot;
            _hasSent = true;
            return next;
        }

        private static byte[] Serialize(long epoch, long sequence, Snapshot snapshot)
        {
            return FlowWire.WriteSnapshot(MessageType.TimeState, epoch, sequence, true, 1)
                .Int(snapshot.Flags)
                .Float(snapshot.SelectedRtwP).Float(snapshot.Tactical).Float(snapshot.Effective)
                .Int(snapshot.Current).Int(snapshot.AdventureStart).Int(snapshot.Travel).ToArray();
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (reader.Type != MessageType.TimeState || sender != null) return;
            SyncContext();
            if (!_session.IsGuest) return;

            FlowSnapshotHeader header = FlowWire.ReadSnapshot(reader, MessageType.TimeState, 1);
            long epoch = header.WorldEpoch;
            long sequence = header.StateSeq;
            int flags = reader.Int();
            float selected = ReadTimeScale(reader.Float(), false, "RTwP speed");
            float tactical = ReadTimeScale(reader.Float(), false, "tactical speed");
            float effective = ReadTimeScale(reader.Float(), true, "effective timescale");
            int current = ReadSeconds(reader.Int(), "current time");
            int adventure = ReadSeconds(reader.Int(), "adventure start");
            int travel = ReadSeconds(reader.Int(), "travel time");
            reader.Finish();
            if (!header.Full || header.Count != 1 || sequence <= 0 || (flags & ~AllFlags) != 0)
                throw new FormatException("invalid full time snapshot header");
            if (epoch != _worldEpoch) return;
            if (epoch == _guestEpoch && sequence <= _guestSequence) return;

            _guestEpoch = epoch;
            _guestSequence = sequence;
            _guestSnapshot = new Snapshot
            {
                Flags = flags,
                SelectedRtwP = selected,
                Tactical = tactical,
                Effective = effective,
                Current = current,
                AdventureStart = adventure,
                Travel = travel,
            };
            _hasGuestSnapshot = true;
            ApplyGuestSnapshot();
        }

        private void ApplyGuestSnapshot()
        {
            if (!_session.IsGuest || !Plugin.IsMirroring) return;
            TimeController time = TimeController.Instance;
            if (time == null) return;

            if (_hasGuestSnapshot)
            {
                Snapshot snapshot = _guestSnapshot;
                SelectedTimeScale(time) = snapshot.SelectedRtwP;
                if (GameState.Option != null && !Mathf.Approximately(GameState.Option.CombatSpeed, snapshot.Tactical))
                    GameState.Option.CombatSpeed = snapshot.Tactical;
                bool paused = (snapshot.Flags & PlayerPausedFlag) != 0;
                if (PlayerPaused(time) != paused)
                {
                    s_applyDepth++;
                    try { time.Paused = paused; }
                    finally { s_applyDepth--; }
                }
                ApplyWorldClock(snapshot);
            }
            Time.timeScale = GuestEffectiveTimeScale(time);
        }

        private void ApplyWorldClock(Snapshot snapshot)
        {
            WorldTime world = WorldTime.Instance;
            if (world == null) return;
            bool changed = world.CurrentTime == null || world.CurrentTime.TotalSeconds != snapshot.Current;
            if (changed) world.CurrentTime = new EternityDateTime(snapshot.Current);
            if (world.AdventureStart == null || world.AdventureStart.TotalSeconds != snapshot.AdventureStart)
                world.AdventureStart = new EternityDateTime(snapshot.AdventureStart);
            if (world.TimeSpentTravelling == null || world.TimeSpentTravelling.TotalSeconds() != snapshot.Travel)
                world.TimeSpentTravelling = new EternityTimeInterval(snapshot.Travel);
            if (changed) UpdateDayNightCycle.Invoke(world, null);
        }

        private float GuestEffectiveTimeScale(TimeController time)
        {
            if (UiPaused(time) && !time.ProhibitPause) return 0f;
            if (!_worldReady || _barrierPaused || !_hasGuestSnapshot || _guestEpoch != _worldEpoch) return 0f;
            return _guestSnapshot.Effective;
        }

        internal static bool TryApplyGuestTimeScale()
        {
            TimeSync self = s_instance;
            if (self == null || !self._session.IsGuest || !Plugin.IsMirroring) return false;
            TimeController time = TimeController.Instance;
            if (time == null) return false;
            Time.timeScale = self.GuestEffectiveTimeScale(time);
            return true;
        }

        internal static void OnHostTimeScaleUpdated()
        {
            TimeSync self = s_instance;
            if (self != null && self._session.IsHost && self._barrierPaused) Time.timeScale = 0f;
        }

        private void ClearGuestSnapshot(bool restoreNative)
        {
            bool hadSnapshot = _hasGuestSnapshot;
            _hasGuestSnapshot = false;
            _guestSnapshot = default(Snapshot);
            if (!restoreNative || !hadSnapshot || TimeController.Instance == null) return;
            TimeController time = TimeController.Instance;
            if (PlayerPaused(time))
            {
                s_applyDepth++;
                try { time.Paused = false; }
                finally { s_applyDepth--; }
            }
            time.UpdateTimeScale();
        }

        private void HostCommand(PlayerInfo sender, long worldEpoch, long requestId, int kind)
        {
            if (sender == null || sender.IsHost || !_session.Players.Exists(p => p.Id == sender.Id)
                || !_session.IsPeerReady(sender.Id)) return;
            PauseKind pauseKind = (PauseKind)kind;
            if (requestId > 0 && _requestCache.TryGetValue(sender.Id, out RequestCache cache)
                && cache.Results.TryGetValue(requestId, out CachedResult cached))
            {
                if (cached.Kind == pauseKind)
                {
                    SendResult(sender.Id, worldEpoch, requestId, pauseKind, cached.Result, cached.StateSequence);
                    return;
                }
                SendResult(sender.Id, worldEpoch, requestId, pauseKind, FlowResultCode.Invalid, _sequence);
                return;
            }

            FlowResultCode result = ValidateCommand(worldEpoch, requestId, pauseKind);
            if (result == FlowResultCode.Ok) result = ApplyCommand(pauseKind);
            long stateSequence = _sequence;
            if (result == FlowResultCode.Ok && _worldReady && CanCapture())
                stateSequence = SendSnapshot(0);
            SendResult(sender.Id, worldEpoch, requestId, pauseKind, result, stateSequence);
            if (requestId > 0) CacheResult(sender.Id, requestId, pauseKind, result, stateSequence);
        }

        private FlowResultCode ValidateCommand(long worldEpoch, long requestId, PauseKind kind)
        {
            if (requestId <= 0) return FlowResultCode.Invalid;
            if (worldEpoch != _worldEpoch || worldEpoch <= 0) return FlowResultCode.StaleWorld;
            if (!_worldReady || _barrierPaused || !CanCapture()) return FlowResultCode.NotReady;
            if (!Enum.IsDefined(typeof(PauseKind), kind)) return FlowResultCode.Invalid;
            if (kind == PauseKind.Fast && GameState.InCombat) return FlowResultCode.Rejected;
            return FlowResultCode.Ok;
        }

        private static void ApplySafePaused(TimeController time, bool paused)
        {
            s_applyDepth++;
            try { time.SafePaused = paused; }
            finally { s_applyDepth--; }
        }

        private FlowResultCode ApplyCommand(PauseKind kind)
        {
            TimeController time = TimeController.Instance;
            switch (kind)
            {
                case PauseKind.Pause:
                    if (!TimeController.IsSafeToPause || TimeController.NoPauseOnGamepad) return FlowResultCode.Rejected;
                    ApplySafePaused(time, true);
                    return PlayerPaused(time) ? FlowResultCode.Ok : FlowResultCode.Rejected;
                case PauseKind.Resume:
                    ApplySafePaused(time, false);
                    return PlayerPaused(time) ? FlowResultCode.Rejected : FlowResultCode.Ok;
                case PauseKind.Slow:
                    TimeScaleSetter.Invoke(time, new object[] { time.SlowTime });
                    return FlowResultCode.Ok;
                case PauseKind.Fast:
                    TimeScaleSetter.Invoke(time, new object[] { time.FastTime });
                    return FlowResultCode.Ok;
                case PauseKind.Normal:
                    TimeScaleSetter.Invoke(time, new object[] { time.NormalTime });
                    return FlowResultCode.Ok;
                default:
                    return FlowResultCode.Invalid;
            }
        }

        private void CacheResult(int playerId, long requestId, PauseKind kind, FlowResultCode result, long stateSequence)
        {
            if (!_requestCache.TryGetValue(playerId, out RequestCache cache))
            {
                cache = new RequestCache();
                _requestCache.Add(playerId, cache);
            }
            cache.Results.Add(requestId, new CachedResult
            {
                Kind = kind,
                Result = result,
                StateSequence = stateSequence,
            });
            cache.Order.Enqueue(requestId);
            while (cache.Results.Count > MaxCachedResultsPerPlayer)
                cache.Results.Remove(cache.Order.Dequeue());
        }

        private void SendResult(int playerId, long epoch, long requestId, PauseKind kind, FlowResultCode result, long stateSequence)
        {
            byte[] data = FlowWire.WriteResult(epoch, requestId, Domain, (int)kind, result, stateSequence).ToArray();
            _session.SendToPlayer(playerId, data);
        }

        private void GuestResult(long worldEpoch, long requestId, int kind, FlowResultCode result, long stateSeq)
        {
            if (worldEpoch != _worldEpoch || requestId <= 0 || stateSeq < 0) return;
            if (!_pendingRequests.TryGetValue(requestId, out PauseKind pending) || pending != (PauseKind)kind) return;
            _pendingRequests.Remove(requestId);
            if (result == FlowResultCode.Ok) return;
            _session.Note("Host rejected shared " + pending.ToString().ToLowerInvariant() + " request: " + result);
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) SendLatestTo(player.Id);
        }

        private void OnPlayerLeft(PlayerInfo player) => _requestCache.Remove(player.Id);

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            SyncContext();
            if (worldEpoch != _worldEpoch) return;
            if (_session.IsHost && player != null) SendLatestTo(player.Id);
            else if (_session.IsGuest) ApplyGuestSnapshot();
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            SyncContext();
            if (_session.IsHost && worldEpoch == _worldEpoch) SendLatestTo(playerId);
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            if (_session.IsGuest) ApplyGuestSnapshot();
        }

        private static bool Same(Snapshot a, Snapshot b)
        {
            return a.Flags == b.Flags && a.Current == b.Current && a.AdventureStart == b.AdventureStart
                && a.Travel == b.Travel && Mathf.Approximately(a.SelectedRtwP, b.SelectedRtwP)
                && Mathf.Approximately(a.Tactical, b.Tactical) && Mathf.Approximately(a.Effective, b.Effective);
        }

        private static float BoundedTimeScale(float value, float fallback, bool allowZero)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || (!allowZero && value == 0f)) return fallback;
            return Mathf.Min(value, MaxTimeScale);
        }

        private static float ReadTimeScale(float value, bool allowZero, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > MaxTimeScale
                || (!allowZero && value == 0f)) throw new FormatException(name + " out of range");
            return value;
        }

        private static int BoundedSeconds(int value) => Math.Max(0, value);

        private static int ReadSeconds(int value, string name)
        {
            if (value < 0) throw new FormatException(name + " out of range");
            return value;
        }
    }
}
