using System;
using System.Collections.Generic;
using System.Globalization;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates the visible part of host projectiles. A launch supplies the prefab
    /// identity, followed by transforms and bounded tactical pulse-effect records.
    /// Guest copies contain no game scripts or physics, so they cannot roll impacts
    /// or alter combat state.
    /// </summary>
    public sealed class ProjectileVisualSync : IDisposable
    {
        private const float SendInterval = 0.05f;
        private const float FullRefreshInterval = 3f;
        // Added once after a forced refresh so the periodic refreshes of the snapshot streams do not share a frame.
        private const float FullRefreshPhase = 2.55f;
        private const float PrefabScanCacheSeconds = 1f;
        private const int MaxPrefabScanCache = 64;
        private const float SlideSeconds = 0.07f;
        private const float FastProjectileVisibleSeconds = 0.1f;
        private const float MaxDestroyDelay = 30f;
        private const float MaxPulseEffectSeconds = 30f;
        private const float DefaultPulseEffectSeconds = 2f;
        private const float MaxCoordinate = 100000f;
        private const int MaxTracked = 512;
        private const int MaxSpawnsPerMessage = 128;
        private const int MaxFullSpawns = MaxTracked;
        private const int MaxUpdatesPerMessage = 512;
        private const int MaxRemovesPerMessage = 512;
        private const int MaxPulsesPerMessage = 256;
        private const int MaxPendingPulses = 1024;
        private const int MaxPendingPulsesPerProjectile = 32;
        private const int MaxGuestPulseEffects = 256;
        private const int MaxEffectParticles = 400;
        private const int MaxStringLength = 96;
        private const int MaxStringId = 10000000;
        private const int MaxAbilityOccurrence = 4096;

        private static ProjectileVisualSync s_instance;

        private readonly Session _session;
        private readonly Dictionary<Projectile, HostProjectile> _hostByObject = new Dictionary<Projectile, HostProjectile>();
        private readonly Dictionary<int, HostProjectile> _hostById = new Dictionary<int, HostProjectile>();
        private readonly List<HostProjectile> _hostOrder = new List<HostProjectile>();
        private readonly Dictionary<int, GuestProjectile> _guest = new Dictionary<int, GuestProjectile>();
        private readonly HashSet<int> _guestSkipLogged = new HashSet<int>();
        private readonly List<int> _finishedGuest = new List<int>();
        private readonly List<GuestPulseEffect> _guestPulseEffects = new List<GuestPulseEffect>();
        private readonly Stack<CaptureFrame> _captureFrames = new Stack<CaptureFrame>();
        private readonly List<HostProjectile> _sendSpawns = new List<HostProjectile>();
        private readonly List<HostProjectile> _sendUpdates = new List<HostProjectile>();
        private readonly List<HostProjectile> _sendRemoves = new List<HostProjectile>();
        private readonly List<PulseRecord> _sendPulses = new List<PulseRecord>();
        private readonly HashSet<int> _sendSpawnedIds = new HashSet<int>();
        private readonly VisualMessage _incoming = new VisualMessage();
        private readonly HashSet<int> _incomingFullIds = new HashSet<int>();
        private readonly HashSet<int> _incomingSpawned = new HashSet<int>();
        private readonly Dictionary<PrefabScanKey, PrefabScanResult> _prefabScans = new Dictionary<PrefabScanKey, PrefabScanResult>();
        private readonly List<AttackRanged> _prefabCandidates = new List<AttackRanged>();

        private static readonly List<MonoBehaviour> s_behaviourScratch = new List<MonoBehaviour>();
        private static readonly List<Rigidbody> s_bodyScratch = new List<Rigidbody>();
        private static readonly List<Collider> s_colliderScratch = new List<Collider>();
        private static readonly List<Joint> s_jointScratch = new List<Joint>();
        private static readonly List<Animator> s_animatorScratch = new List<Animator>();

        private GameObject _staging;
        private CaptureFrame _capture;
        private bool _wasHost, _wasGuest, _probe;
        private int _epoch, _sequence, _nextHostId = 1;
        private int _guestEpoch = -1, _guestSequence = -1;
        private int _pendingPulseCount;
        private float _nextSend, _nextFullRefresh, _nextHostSkipLog;

        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private enum CaptureKind
        {
            None,
            Impact,
            Sustained,
            Pulse,
        }

        private enum PulsePhase : byte
        {
            Impact = 1,
            Pulse = 2,
            Sustained = 3,
        }

        private enum PulseEffectKind : byte
        {
            None = 0,
            Impact = 1,
            Pulse = 2,
        }

        private struct CaptureFrame
        {
            public Projectile Projectile;
            public CaptureKind Kind;
            public PulseCapture Pulse;
        }

        private sealed class PulseCapture
        {
            public HostProjectile State;
            public Pose Pose;
            public PulseEffectKind Effect;
            public bool SuppressAudio;
            public bool HasEffect;
        }

        private sealed class HostProjectile
        {
            public int Id;
            public Projectile Object;
            public AttackRanged Attack;
            public Guid SourceId, AttackId, TargetId;
            public string AttackName, AttackType, PrefabName;
            public string AbilityName;
            public int AbilityStringId, AbilityOccurrence;
            public int NextPulseSequence;
            public Pose Current, Sent;
            public float DestroyDelay;
            public bool HasSentPose, SpawnSent, Removed, RemoveSent;
            public readonly Queue<PulseRecord> Pulses = new Queue<PulseRecord>();
        }

        private sealed class GuestProjectile
        {
            public GameObject Object;
            public AttackRanged SourceAttack;
            public SpawnRecord Spawn;
            public Pose From, To;
            public float Remaining;
            public int LastPulseSequence = -1;
            public bool Pending, Removing, Stopped;
            public float StopAt, DestroyAt;
        }

        private sealed class GuestPulseEffect
        {
            public int ProjectileId;
            public GameObject Object;
            public float DestroyAt;
            public bool Pending, SuppressAudio;
        }

        private sealed class SpawnRecord
        {
            public int Id;
            public Guid SourceId, AttackId, TargetId;
            public string AttackName, AttackType, PrefabName;
            public string AbilityName;
            public int AbilityStringId, AbilityOccurrence;
            public Pose Pose;
            public float DestroyDelay;
            public bool HasAbility => AbilityOccurrence >= 0;
        }

        private struct UpdateRecord
        {
            public int Id;
            public Pose Pose;
        }

        private struct RemoveRecord
        {
            public int Id;
            public Pose Pose;
            public float DestroyDelay;
        }

        private sealed class PulseRecord
        {
            public int Id, Sequence;
            public Pose Pose;
            public PulsePhase Phase;
            public PulseEffectKind Effect;
            public bool SuppressAudio;
        }

        private sealed class VisualMessage
        {
            public int Epoch, Sequence;
            public bool Full;
            public readonly List<SpawnRecord> Spawns = new List<SpawnRecord>();
            public readonly List<UpdateRecord> Updates = new List<UpdateRecord>();
            public readonly List<RemoveRecord> Removes = new List<RemoveRecord>();
            public readonly List<PulseRecord> Pulses = new List<PulseRecord>();
        }

        /// <summary>The spawn fields the scene-wide attack search matches on.</summary>
        private struct PrefabScanKey : IEquatable<PrefabScanKey>
        {
            public string AttackName, AttackType, PrefabName, AbilityName;
            public int AbilityStringId;
            public bool HasAbility;

            public bool Equals(PrefabScanKey other)
            {
                return HasAbility == other.HasAbility && AbilityStringId == other.AbilityStringId
                    && string.Equals(AttackName, other.AttackName, StringComparison.Ordinal)
                    && string.Equals(AttackType, other.AttackType, StringComparison.Ordinal)
                    && string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal)
                    && string.Equals(AbilityName, other.AbilityName, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is PrefabScanKey other && Equals(other);

            public override int GetHashCode()
            {
                int hash = AttackName != null ? AttackName.GetHashCode() : 0;
                hash = hash * 31 + (PrefabName != null ? PrefabName.GetHashCode() : 0);
                hash = hash * 31 + (AttackType != null ? AttackType.GetHashCode() : 0);
                return hash * 31 + AbilityStringId;
            }
        }

        private struct PrefabScanResult
        {
            public AttackRanged Attack;
            public float Until;
        }

        public ProjectileVisualSync(Session session)
        {
            _session = session;
            _wasHost = session.IsHost;
            _wasGuest = session.IsGuest;
            _probe = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-probe") >= 0;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            ClearHost();
            ClearGuestVisuals();
            DestroyStaging();
            if (s_instance == this) s_instance = null;
        }

        // ---- capture called by Patches/ProjectileVisualPatches.cs ----

        internal static void CaptureLaunch(Projectile projectile)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || projectile == null) return;
            try { self.TrackLaunch(projectile); }
            catch (Exception ex) { self.LogHostSkip(projectile, "launch capture failed: " + ex.Message); }
        }

        internal static void CaptureRemoval(Projectile projectile)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || projectile == null) return;
            try
            {
                if (self._hostByObject.TryGetValue(projectile, out HostProjectile state))
                    self.MarkRemoved(state, SafeDelay(projectile.DestroyDelayTime));
            }
            catch (Exception ex) { self.LogHostSkip(projectile, "removal capture failed: " + ex.Message); }
        }

        internal static void CaptureDestroy(Projectile projectile)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || projectile == null) return;
            try
            {
                if (self._hostByObject.TryGetValue(projectile, out HostProjectile state))
                    self.MarkRemoved(state, 0f);
            }
            catch (Exception ex) { self.LogHostSkip(projectile, "destroy capture failed: " + ex.Message); }
        }

        internal static int BeginImpactCapture(Projectile projectile)
        {
            return BeginCapture(projectile, CaptureKind.Impact);
        }

        internal static int BeginSustainedCapture(Projectile projectile)
        {
            return BeginCapture(projectile, CaptureKind.Sustained);
        }

        internal static int BeginPulseCapture(Projectile projectile)
        {
            return BeginCapture(projectile, CaptureKind.Pulse);
        }

        internal static void EndCapture(int token)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || token == 0) return;
            self.PopCapture();
        }

        internal static void EndPulseCapture(int token)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || token == 0) return;
            try
            {
                PulseCapture pulse = self._capture.Pulse;
                HostProjectile state = pulse != null ? pulse.State : null;
                if (state == null && self._capture.Projectile != null)
                    state = self.CaptureState(self._capture.Projectile);
                if (state != null)
                {
                    if (pulse == null) pulse = new PulseCapture();
                    if (!pulse.HasEffect)
                    {
                        AttackPulsedAOE attack = state.Attack as AttackPulsedAOE;
                        pulse.Effect = attack == null ? PulseEffectKind.None
                            : attack.PulseEffect != null ? PulseEffectKind.Pulse
                            : attack.ImpactEffect != null ? PulseEffectKind.Impact : PulseEffectKind.None;
                        pulse.Pose = EffectPose(state.Object, state.Attack);
                    }
                    self.QueuePulse(state, PulsePhase.Pulse, pulse.Effect, pulse.Pose, pulse.SuppressAudio);
                }
            }
            catch (Exception ex) { self.LogHostSkip(self._capture.Projectile, "pulse capture failed: " + ex.Message); }
            finally { self.PopCapture(); }
        }

        internal static void CaptureEffect(GameObject effectBase, Vector3 position, Quaternion orientation, bool suppressAudio)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || self._capture.Kind == CaptureKind.None) return;
            try { self.CaptureEffectLocal(effectBase, position, orientation, suppressAudio); }
            catch (Exception ex) { self.LogHostSkip(self._capture.Projectile, "effect capture failed: " + ex.Message); }
        }

        private static int BeginCapture(Projectile projectile, CaptureKind kind)
        {
            ProjectileVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || projectile == null) return 0;
            try
            {
                if (!IsTacticalPulseCandidate(projectile)) return 0;
                self._captureFrames.Push(self._capture);
                self._capture = new CaptureFrame { Projectile = projectile, Kind = kind };
                if (kind == CaptureKind.Pulse)
                    self._capture.Pulse = new PulseCapture { State = self.CaptureState(projectile) };
                return 1;
            }
            catch (Exception ex)
            {
                self.LogHostSkip(projectile, "presentation capture failed: " + ex.Message);
                if (self._capture.Projectile == projectile && self._capture.Kind == kind) self.PopCapture();
                return 0;
            }
        }

        private void PopCapture()
        {
            _capture = _captureFrames.Count > 0 ? _captureFrames.Pop() : default(CaptureFrame);
        }

        private static bool IsTacticalPulseCandidate(Projectile projectile)
        {
            return projectile.Attack is AttackPulsedAOE && TacticalModeManager.IsTacticalModeEnabled
                && TacticalModeManager.IsInTacticalCombat(projectile.Owner);
        }

        private HostProjectile CaptureState(Projectile projectile)
        {
            if (projectile == null) return null;
            if (!_hostByObject.TryGetValue(projectile, out HostProjectile state))
            {
                TrackLaunch(projectile);
                _hostByObject.TryGetValue(projectile, out state);
            }
            return state;
        }

        private void CaptureEffectLocal(GameObject effectBase, Vector3 position, Quaternion orientation, bool suppressAudio)
        {
            if (effectBase == null) return;
            CaptureFrame frame = _capture;
            HostProjectile state = frame.Pulse != null ? frame.Pulse.State : CaptureState(frame.Projectile);
            AttackPulsedAOE attack = state != null ? state.Attack as AttackPulsedAOE : null;
            if (attack == null) return;

            PulseEffectKind effect;
            if (effectBase == attack.ImpactEffect) effect = PulseEffectKind.Impact;
            else if (effectBase == attack.PulseEffect) effect = PulseEffectKind.Pulse;
            else return;
            if (!TryPose(position, orientation, out Pose pose)) return;

            if (frame.Pulse != null)
            {
                if (frame.Pulse.HasEffect) return;
                frame.Pulse.State = state;
                frame.Pulse.Pose = pose;
                frame.Pulse.Effect = effect;
                frame.Pulse.SuppressAudio = suppressAudio;
                frame.Pulse.HasEffect = true;
                return;
            }
            QueuePulse(state, frame.Kind == CaptureKind.Impact ? PulsePhase.Impact : PulsePhase.Sustained,
                effect, pose, suppressAudio);
        }

        private void QueuePulse(HostProjectile state, PulsePhase phase, PulseEffectKind effect, Pose pose, bool suppressAudio)
        {
            if (_pendingPulseCount >= MaxPendingPulses)
            {
                LogHostSkip(state.Object, "the pending pulse limit is full");
                return;
            }
            while (state.Pulses.Count >= MaxPendingPulsesPerProjectile)
            {
                state.Pulses.Dequeue();
                _pendingPulseCount--;
            }
            int sequence = state.NextPulseSequence;
            state.NextPulseSequence = sequence == int.MaxValue ? 0 : sequence + 1;
            state.Pulses.Enqueue(new PulseRecord
            {
                Id = state.Id,
                Sequence = sequence,
                Phase = phase,
                Effect = effect,
                Pose = pose,
                SuppressAudio = suppressAudio,
            });
            _pendingPulseCount++;
        }

        private static Pose EffectPose(Projectile projectile, AttackRanged attack)
        {
            if (projectile != null)
            {
                Vector3 position = projectile.transform.position;
                Quaternion rotation = attack != null && attack.Owner != null ? attack.Owner.transform.rotation : projectile.transform.rotation;
                if (TryPose(position, rotation, out Pose pose)) return pose;
            }
            return new Pose { Rotation = Quaternion.identity };
        }

        private void TrackLaunch(Projectile projectile)
        {
            AttackRanged attack = projectile.Attack;
            if (attack == null)
            {
                LogHostSkip(projectile, "it has no AttackRanged identity");
                return;
            }

            if (_hostByObject.TryGetValue(projectile, out HostProjectile existing))
            {
                if (existing.Removed)
                {
                    _pendingPulseCount -= existing.Pulses.Count;
                    existing.Pulses.Clear();
                    existing.Removed = false;
                    existing.RemoveSent = false;
                    existing.SpawnSent = false;
                    existing.HasSentPose = false;
                }
                if (TryReadPose(projectile.transform, out Pose relaunched)) existing.Current = relaunched;
                if (projectile.IsDestroyed()) MarkRemoved(existing, SafeDelay(projectile.DestroyDelayTime));
                return;
            }
            if (_hostById.Count >= MaxTracked)
            {
                LogHostSkip(projectile, "the projectile tracking limit is full");
                return;
            }

            GameObject owner = attack.Owner;
            GenericAbility ability = attack.AbilityOrigin;
            var state = new HostProjectile
            {
                Id = AllocateHostId(),
                Object = projectile,
                Attack = attack,
                SourceId = IdOf(owner),
                AttackId = ability != null && attack.transform.IsChildOf(ability.transform) ? Guid.Empty : IdOf(attack.gameObject),
                TargetId = IdOf(projectile.TargetObject),
                AttackName = NormalizeName(attack.gameObject.name),
                AttackType = Clean(attack.GetType().FullName),
                PrefabName = NormalizeName(attack.ProjectilePrefab != null ? attack.ProjectilePrefab.name : ""),
                AbilityName = ability != null ? NormalizeName(ability.gameObject.name) : "",
                AbilityStringId = ability != null ? ability.DisplayName.StringID : -1,
                AbilityOccurrence = ability != null ? AbilityOccurrence(owner, ability) : -1,
                DestroyDelay = SafeDelay(projectile.DestroyDelayTime),
            };
            if (!TryReadPose(projectile.transform, out state.Current))
            {
                LogHostSkip(projectile, "its launch transform is invalid");
                return;
            }
            _hostByObject[projectile] = state;
            _hostById[state.Id] = state;
            _hostOrder.Add(state);
            ProbeHost("launch", state);
            if (projectile.IsDestroyed()) MarkRemoved(state, state.DestroyDelay);
        }

        private int AllocateHostId()
        {
            for (int guard = 0; guard < MaxTracked; guard++)
            {
                int id = _nextHostId++;
                if (_nextHostId <= 0) _nextHostId = 1;
                if (id > 0 && !_hostById.ContainsKey(id)) return id;
            }
            throw new InvalidOperationException("no projectile id available");
        }

        private void MarkRemoved(HostProjectile state, float delay)
        {
            if (state.Removed) return;
            if (state.Object != null && TryReadPose(state.Object.transform, out Pose pose)) state.Current = pose;
            state.Removed = true;
            state.DestroyDelay = SafeDelay(delay);
            ProbeHost("remove", state);
        }

        private void LogHostSkip(Projectile projectile, string why)
        {
            if (Time.unscaledTime < _nextHostSkipLog) return;
            _nextHostSkipLog = Time.unscaledTime + 2f;
            string name = projectile != null ? projectile.name : "unknown";
            Plugin.Log?.LogWarning("Not replicating projectile " + name + ": " + why);
        }

        // ---- host ----

        private void UpdateHost(float now)
        {
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            long perf = PerfStats.Begin();
            SendHostState(now);
            PerfStats.End("projectile.capture", perf);
        }

        private void SendHostState(float now)
        {
            CaptureHostPoses();

            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading)
            {
                for (int i = _hostOrder.Count - 1; i >= 0; i--)
                {
                    _hostOrder[i].Pulses.Clear();
                    if (_hostOrder[i].Removed) RemoveHostStateAt(i);
                }
                _pendingPulseCount = 0;
                _nextFullRefresh = 0f;
                return;
            }

            // Zero marks a refresh forced by a join, a level change or a stream reset.
            bool forced = _nextFullRefresh <= 0f;
            bool full = now >= _nextFullRefresh;
            if (_sequence == int.MaxValue)
            {
                _epoch = NextEpoch(_epoch);
                _sequence = 0;
                full = true;
            }
            if (!full && _hostOrder.Count == 0) return;

            List<HostProjectile> spawns = _sendSpawns, updates = _sendUpdates, removes = _sendRemoves;
            List<PulseRecord> pulses = _sendPulses;
            HashSet<int> spawnedIds = _sendSpawnedIds;
            spawns.Clear();
            updates.Clear();
            removes.Clear();
            pulses.Clear();
            spawnedIds.Clear();
            int spawnLimit = full ? MaxFullSpawns : MaxSpawnsPerMessage;
            int unchanged = 0;

            for (int i = 0; i < _hostOrder.Count; i++)
            {
                HostProjectile state = _hostOrder[i];
                bool needsSpawn = full ? !state.Removed : !state.SpawnSent;
                if (needsSpawn && spawns.Count < spawnLimit)
                {
                    spawns.Add(state);
                    spawnedIds.Add(state.Id);
                }
            }

            if (!full)
            {
                for (int i = 0; i < _hostOrder.Count; i++)
                {
                    HostProjectile state = _hostOrder[i];
                    if (state.Removed)
                    {
                        if ((state.SpawnSent || spawnedIds.Contains(state.Id)) && removes.Count < MaxRemovesPerMessage)
                            removes.Add(state);
                    }
                    else if (state.SpawnSent && !spawnedIds.Contains(state.Id) && updates.Count < MaxUpdatesPerMessage)
                    {
                        if (!state.HasSentPose || !Same(state.Sent, state.Current)) updates.Add(state);
                        else unchanged++;
                    }
                }
            }

            for (int i = 0; i < _hostOrder.Count && pulses.Count < MaxPulsesPerMessage; i++)
            {
                HostProjectile state = _hostOrder[i];
                if (!state.SpawnSent && !spawnedIds.Contains(state.Id)) continue;
                while (state.Pulses.Count > 0 && pulses.Count < MaxPulsesPerMessage)
                {
                    pulses.Add(state.Pulses.Dequeue());
                    _pendingPulseCount--;
                }
            }

            if (unchanged != 0) PerfStats.Count("projectile.skipped", unchanged);
            if (!full && spawns.Count == 0 && updates.Count == 0 && removes.Count == 0 && pulses.Count == 0) return;
            var message = new MessageWriter(MessageType.ProjectileVisuals)
                .Int(_epoch).Int(_sequence++).Byte((byte)(full ? 1 : 0))
                .Int(spawns.Count).Int(updates.Count).Int(removes.Count).Int(pulses.Count);
            for (int i = 0; i < spawns.Count; i++) WriteSpawn(message, spawns[i]);
            for (int i = 0; i < updates.Count; i++) WriteUpdate(message, updates[i]);
            for (int i = 0; i < removes.Count; i++) WriteRemove(message, removes[i]);
            for (int i = 0; i < pulses.Count; i++) WritePulse(message, pulses[i]);
            _session.SendToGuests(message.ToArray());
            PerfStats.Count(full ? "projectile.full" : "projectile.delta");
            PerfStats.Count("projectile.records", spawns.Count + updates.Count + removes.Count + pulses.Count);

            for (int i = 0; i < spawns.Count; i++)
            {
                HostProjectile state = spawns[i];
                state.SpawnSent = true;
                state.Sent = state.Current;
                state.HasSentPose = true;
            }
            for (int i = 0; i < updates.Count; i++)
            {
                HostProjectile state = updates[i];
                state.Sent = state.Current;
                state.HasSentPose = true;
            }
            for (int i = 0; i < removes.Count; i++) removes[i].RemoveSent = true;
            for (int i = _hostOrder.Count - 1; i >= 0; i--)
                if (_hostOrder[i].RemoveSent || (full && _hostOrder[i].Removed)) RemoveHostStateAt(i);

            // The scratch lists must not keep finished projectiles alive while the stream is idle.
            spawns.Clear();
            updates.Clear();
            removes.Clear();
            pulses.Clear();
            spawnedIds.Clear();

            if (full) _nextFullRefresh = now + FullRefreshInterval + (forced ? FullRefreshPhase : 0f);
        }

        private void CaptureHostPoses()
        {
            for (int i = 0; i < _hostOrder.Count; i++)
            {
                HostProjectile state = _hostOrder[i];
                if (state.Removed) continue;
                if (state.Object == null)
                {
                    MarkRemoved(state, 0f);
                    continue;
                }
                if (TryReadPose(state.Object.transform, out Pose pose)) state.Current = pose;
                else
                {
                    MarkRemoved(state, 0f);
                    LogHostSkip(state.Object, "its transform became invalid");
                }
            }
        }

        private static void WriteSpawn(MessageWriter message, HostProjectile state)
        {
            message.Int(state.Id).Guid(state.SourceId).Guid(state.AttackId).Guid(state.TargetId)
                .String(state.AttackName).String(state.AttackType).String(state.PrefabName)
                .String(state.AbilityName).Int(state.AbilityStringId).Int(state.AbilityOccurrence);
            WritePose(message, state.Current);
            message.Float(state.DestroyDelay);
        }

        private static void WriteUpdate(MessageWriter message, HostProjectile state)
        {
            message.Int(state.Id);
            WritePose(message, state.Current);
        }

        private static void WriteRemove(MessageWriter message, HostProjectile state)
        {
            message.Int(state.Id);
            WritePose(message, state.Current);
            message.Float(state.DestroyDelay);
        }

        private static void WritePulse(MessageWriter message, PulseRecord pulse)
        {
            message.Int(pulse.Id).Int(pulse.Sequence).Byte((byte)pulse.Phase).Byte((byte)pulse.Effect)
                .Byte((byte)(pulse.SuppressAudio ? 1 : 0));
            WritePose(message, pulse.Pose);
        }

        private static void WritePose(MessageWriter message, Pose pose)
        {
            message.Float(pose.Position.x).Float(pose.Position.y).Float(pose.Position.z)
                .Float(pose.Rotation.x).Float(pose.Rotation.y).Float(pose.Rotation.z).Float(pose.Rotation.w);
        }

        private static bool Same(Pose a, Pose b)
        {
            return (a.Position - b.Position).sqrMagnitude < 0.000001f
                && Mathf.Abs(1f - Mathf.Abs(Quaternion.Dot(a.Rotation, b.Rotation))) < 0.00001f;
        }

        private void RemoveHostStateAt(int index)
        {
            HostProjectile state = _hostOrder[index];
            _pendingPulseCount -= state.Pulses.Count;
            state.Pulses.Clear();
            _hostOrder.RemoveAt(index);
            _hostById.Remove(state.Id);
            if ((object)state.Object != null) _hostByObject.Remove(state.Object);
        }

        // ---- guest receive ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.ProjectileVisuals || !_session.IsGuest) return;
            long perf = PerfStats.Begin();
            try { ApplyMessage(ReadMessage(reader, _incoming)); }
            finally { PerfStats.End("projectile.apply", perf); }
        }

        private void ApplyMessage(VisualMessage message)
        {
            if (_guestEpoch == message.Epoch && message.Sequence <= _guestSequence) return;

            bool newEpoch = _guestEpoch != message.Epoch || (message.Sequence == 0 && _guestSequence > 0);
            _guestEpoch = message.Epoch;
            _guestSequence = message.Sequence;
            if (newEpoch) ClearGuestVisuals();
            if (!GameState.IsInGameSession || GameState.IsLoading)
            {
                ClearGuestVisuals();
                return;
            }

            PerfStats.Count(message.Full ? "projectile.full" : "projectile.delta");
            PerfStats.Count("projectile.records",
                message.Spawns.Count + message.Updates.Count + message.Removes.Count + message.Pulses.Count);
            int skipped = 0;

            HashSet<int> fullIds = _incomingFullIds;
            fullIds.Clear();
            if (message.Full)
                foreach (SpawnRecord spawn in message.Spawns) fullIds.Add(spawn.Id);

            HashSet<int> spawned = _incomingSpawned;
            spawned.Clear();
            foreach (SpawnRecord spawn in message.Spawns)
            {
                if (_guest.TryGetValue(spawn.Id, out GuestProjectile old))
                {
                    if (message.Full && !old.Removing && old.Object != null)
                    {
                        old.From = ReadLocalPose(old.Object.transform, spawn.Pose);
                        old.To = spawn.Pose;
                        old.Remaining = SlideSeconds;
                        _guestSkipLogged.Remove(spawn.Id);
                        continue;
                    }
                    DestroyGuestObject(old);
                    DestroyPulseEffectsFor(spawn.Id);
                    _guest.Remove(spawn.Id);
                }
                GuestProjectile visual = CreateGuestVisual(spawn);
                if (visual != null)
                {
                    _guest[spawn.Id] = visual;
                    spawned.Add(spawn.Id);
                    _guestSkipLogged.Remove(spawn.Id);
                }
                else skipped++;
            }
            foreach (UpdateRecord update in message.Updates)
            {
                if (!_guest.TryGetValue(update.Id, out GuestProjectile visual) || visual.Object == null || visual.Removing)
                {
                    skipped++;
                    continue;
                }
                visual.From = ReadLocalPose(visual.Object.transform, update.Pose);
                visual.To = update.Pose;
                visual.Remaining = SlideSeconds;
            }
            foreach (PulseRecord pulse in message.Pulses)
            {
                if (!_guest.TryGetValue(pulse.Id, out GuestProjectile visual) || visual.Object == null
                    || !PulseIsNewer(pulse.Sequence, visual.LastPulseSequence)) continue;
                visual.LastPulseSequence = pulse.Sequence;
                if (pulse.Effect != PulseEffectKind.None) CreateGuestPulseEffect(visual, pulse);
                if (_probe)
                    ProbeGuest("pulse-" + pulse.Phase.ToString().ToLowerInvariant(), visual.Spawn, visual,
                        "sequence=" + pulse.Sequence + ",effect=" + pulse.Effect);
            }
            foreach (RemoveRecord remove in message.Removes)
            {
                _guestSkipLogged.Remove(remove.Id);
                if (!_guest.TryGetValue(remove.Id, out GuestProjectile visual) || visual.Object == null) continue;
                float visibleFor = spawned.Contains(remove.Id) ? FastProjectileVisibleSeconds : 0f;
                visual.From = ReadLocalPose(visual.Object.transform, remove.Pose);
                visual.To = remove.Pose;
                visual.Remaining = visibleFor;
                if (visibleFor == 0f)
                {
                    visual.Object.transform.position = remove.Pose.Position;
                    visual.Object.transform.rotation = remove.Pose.Rotation;
                }
                visual.Removing = true;
                visual.Stopped = false;
                visual.StopAt = Time.unscaledTime + visibleFor;
                visual.DestroyAt = visual.StopAt + remove.DestroyDelay;
                ProbeGuest("remove", visual.Spawn, visual, "");
            }
            if (message.Full)
            {
                _finishedGuest.Clear();
                foreach (var pair in _guest)
                    if (!fullIds.Contains(pair.Key)) _finishedGuest.Add(pair.Key);
                foreach (int id in _finishedGuest)
                {
                    if (_guest.TryGetValue(id, out GuestProjectile stale))
                    {
                        ProbeGuest("destroy-full", stale.Spawn, stale, "absent from host full refresh");
                        DestroyGuestObject(stale);
                    }
                    DestroyPulseEffectsFor(id);
                    _guest.Remove(id);
                }
                if (_guestSkipLogged.Count > 0) _guestSkipLogged.RemoveWhere(id => !fullIds.Contains(id));
                _finishedGuest.Clear();
            }
            if (skipped != 0) PerfStats.Count("projectile.skipped", skipped);
        }

        private static VisualMessage ReadMessage(MessageReader reader, VisualMessage message)
        {
            message.Spawns.Clear();
            message.Updates.Clear();
            message.Removes.Clear();
            message.Pulses.Clear();
            int epoch = reader.Int();
            int sequence = reader.Int();
            byte full = reader.Byte();
            if (epoch < 0 || sequence < 0 || full > 1) throw new FormatException("invalid projectile stream header");
            int spawnCount = reader.Int();
            int updateCount = reader.Int();
            int removeCount = reader.Int();
            int pulseCount = reader.Int();
            if (spawnCount < 0 || spawnCount > MaxFullSpawns) throw new FormatException("projectile spawn count " + spawnCount);
            if (updateCount < 0 || updateCount > MaxUpdatesPerMessage) throw new FormatException("projectile update count " + updateCount);
            if (removeCount < 0 || removeCount > MaxRemovesPerMessage) throw new FormatException("projectile remove count " + removeCount);
            if (pulseCount < 0 || pulseCount > MaxPulsesPerMessage) throw new FormatException("projectile pulse count " + pulseCount);

            message.Epoch = epoch;
            message.Sequence = sequence;
            message.Full = full != 0;
            for (int i = 0; i < spawnCount; i++)
            {
                var spawn = new SpawnRecord
                {
                    Id = reader.Int(),
                    SourceId = reader.Guid(),
                    AttackId = reader.Guid(),
                    TargetId = reader.Guid(),
                    AttackName = ReadString(reader),
                    AttackType = ReadString(reader),
                    PrefabName = ReadString(reader),
                    AbilityName = ReadString(reader),
                    AbilityStringId = reader.Int(),
                    AbilityOccurrence = reader.Int(),
                };
                if (spawn.Id <= 0 || spawn.AbilityOccurrence < -1 || spawn.AbilityOccurrence > MaxAbilityOccurrence
                    || spawn.AbilityStringId < -1 || spawn.AbilityStringId > MaxStringId)
                    throw new FormatException("invalid projectile spawn identity");
                spawn.Pose = ReadPose(reader);
                spawn.DestroyDelay = ReadDelay(reader);
                message.Spawns.Add(spawn);
            }
            for (int i = 0; i < updateCount; i++)
            {
                int id = reader.Int();
                if (id <= 0) throw new FormatException("invalid projectile update id");
                message.Updates.Add(new UpdateRecord { Id = id, Pose = ReadPose(reader) });
            }
            for (int i = 0; i < removeCount; i++)
            {
                int id = reader.Int();
                if (id <= 0) throw new FormatException("invalid projectile remove id");
                message.Removes.Add(new RemoveRecord { Id = id, Pose = ReadPose(reader), DestroyDelay = ReadDelay(reader) });
            }
            for (int i = 0; i < pulseCount; i++)
            {
                int id = reader.Int();
                int pulseSequence = reader.Int();
                byte phase = reader.Byte();
                byte effect = reader.Byte();
                byte suppressAudio = reader.Byte();
                if (id <= 0 || pulseSequence < 0 || phase < (byte)PulsePhase.Impact || phase > (byte)PulsePhase.Sustained
                    || effect > (byte)PulseEffectKind.Pulse || suppressAudio > 1)
                    throw new FormatException("invalid projectile pulse");
                message.Pulses.Add(new PulseRecord
                {
                    Id = id,
                    Sequence = pulseSequence,
                    Phase = (PulsePhase)phase,
                    Effect = (PulseEffectKind)effect,
                    SuppressAudio = suppressAudio != 0,
                    Pose = ReadPose(reader),
                });
            }
            return message;
        }

        private static string ReadString(MessageReader reader)
        {
            string value = reader.String();
            if (value.Length > MaxStringLength) throw new FormatException("projectile identity string is too long");
            return NormalizeName(value);
        }

        private static float ReadDelay(MessageReader reader)
        {
            float delay = reader.Float();
            if (!IsFinite(delay) || delay < 0f || delay > MaxDestroyDelay) throw new FormatException("invalid projectile destroy delay");
            return delay;
        }

        private static Pose ReadPose(MessageReader reader)
        {
            var pose = new Pose
            {
                Position = new Vector3(reader.Float(), reader.Float(), reader.Float()),
                Rotation = new Quaternion(reader.Float(), reader.Float(), reader.Float(), reader.Float()),
            };
            if (!IsFinite(pose.Position) || Mathf.Abs(pose.Position.x) > MaxCoordinate
                || Mathf.Abs(pose.Position.y) > MaxCoordinate || Mathf.Abs(pose.Position.z) > MaxCoordinate
                || !IsFinite(pose.Rotation.x) || !IsFinite(pose.Rotation.y) || !IsFinite(pose.Rotation.z) || !IsFinite(pose.Rotation.w))
                throw new FormatException("invalid projectile transform");
            float magnitude = pose.Rotation.x * pose.Rotation.x + pose.Rotation.y * pose.Rotation.y
                + pose.Rotation.z * pose.Rotation.z + pose.Rotation.w * pose.Rotation.w;
            if (magnitude < 0.25f || magnitude > 2.25f) throw new FormatException("invalid projectile rotation");
            pose.Rotation = Normalize(pose.Rotation);
            return pose;
        }

        // ---- guest presentation ----

        private GuestProjectile CreateGuestVisual(SpawnRecord spawn)
        {
            AttackRanged sourceAttack = null;
            Projectile prefab = null;
            try { prefab = ResolvePrefab(spawn, out sourceAttack); }
            catch (Exception ex)
            {
                LogGuestSkip(spawn, "the local attack could not be inspected: " + ex.Message);
                return null;
            }
            if (prefab == null)
            {
                LogGuestSkip(spawn, "no matching local projectile prefab");
                return null;
            }

            Projectile clone = null;
            try
            {
                EnsureStaging();
                clone = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity, _staging.transform);
                if (clone == null) throw new InvalidOperationException("Unity returned no projectile instance");
                clone.gameObject.SetActive(false);
                MakeInert(clone.gameObject);
                clone.transform.position = spawn.Pose.Position;
                clone.transform.rotation = spawn.Pose.Rotation;
                clone.name = "Co-op projectile " + spawn.Id + " (" + spawn.PrefabName + ")";
                return new GuestProjectile
                {
                    Object = clone.gameObject,
                    SourceAttack = sourceAttack,
                    Spawn = spawn,
                    From = spawn.Pose,
                    To = spawn.Pose,
                    Pending = true,
                    DestroyAt = float.MaxValue,
                };
            }
            catch (Exception ex)
            {
                if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
                LogGuestSkip(spawn, "the prefab could not be made inert: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void MakeInert(GameObject clone)
        {
            foreach (Projectile projectile in clone.GetComponentsInChildren<Projectile>(true))
            {
                if (projectile == null) continue;
                projectile.OnImpactAttack = null;
                projectile.TargetObject = null;
                projectile.BounceObjects = null;
                projectile.enabled = false;
            }
            MonoBehaviour[] behaviours = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = behaviours.Length - 1; i >= 0; i--)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                behaviour.enabled = false;
                UnityEngine.Object.Destroy(behaviour);
            }
            foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null) continue;
                animator.enabled = false;
                UnityEngine.Object.Destroy(animator);
            }
            foreach (Joint joint in clone.GetComponentsInChildren<Joint>(true))
                if (joint != null) UnityEngine.Object.Destroy(joint);
            foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body == null) continue;
                body.useGravity = false;
                body.detectCollisions = false;
                body.isKinematic = true;
                body.constraints = RigidbodyConstraints.FreezeAll;
                UnityEngine.Object.Destroy(body);
            }
            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                collider.enabled = false;
                UnityEngine.Object.Destroy(collider);
            }
        }

        private static bool HasGameplayComponents(GameObject clone)
        {
            return HasAny(clone, s_behaviourScratch) || HasAny(clone, s_bodyScratch) || HasAny(clone, s_colliderScratch)
                || HasAny(clone, s_jointScratch) || HasAny(clone, s_animatorScratch);
        }

        private static bool HasAny<T>(GameObject clone, List<T> scratch)
        {
            clone.GetComponentsInChildren(true, scratch);
            bool any = scratch.Count != 0;
            scratch.Clear();
            return any;
        }

        private void CreateGuestPulseEffect(GuestProjectile visual, PulseRecord pulse)
        {
            if (_guestPulseEffects.Count >= MaxGuestPulseEffects) return;
            AttackPulsedAOE attack = visual.SourceAttack as AttackPulsedAOE;
            if (attack == null) return;

            GameObject prefab = pulse.Effect == PulseEffectKind.Pulse ? attack.PulseEffect : attack.ImpactEffect;
            GameObject clone = null;
            try
            {
                GenericAbility ability = attack.AbilityOrigin;
                if (ability != null) prefab = ability.CheckForReplacedParticleFX(prefab);
                if (prefab == null) return;

                EnsureStaging();
                clone = UnityEngine.Object.Instantiate(prefab, pulse.Pose.Position, pulse.Pose.Rotation, _staging.transform);
                if (clone == null) return;
                clone.gameObject.SetActive(false);
                MakeInert(clone);
                clone.transform.localScale = Vector3.one;
                clone.transform.position = pulse.Pose.Position;
                clone.transform.rotation = pulse.Pose.Rotation;
                clone.name = "Co-op pulse " + pulse.Id + ":" + pulse.Sequence + " (" + prefab.name + ")";
                if (pulse.SuppressAudio)
                    foreach (AudioSource audio in clone.GetComponentsInChildren<AudioSource>(true))
                        if (audio != null) audio.mute = true;
                _guestPulseEffects.Add(new GuestPulseEffect
                {
                    ProjectileId = pulse.Id,
                    Object = clone,
                    Pending = true,
                    SuppressAudio = pulse.SuppressAudio,
                    DestroyAt = Time.unscaledTime + PulseEffectLifetime(clone),
                });
            }
            catch (Exception ex)
            {
                if (clone != null) UnityEngine.Object.Destroy(clone);
                LogGuestSkip(visual.Spawn, "a pulse effect could not be made inert: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static float PulseEffectLifetime(GameObject effect)
        {
            float lifetime = 0f;
            foreach (ParticleSystem particles in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles == null) continue;
                ParticleSystem.MainModule main = particles.main;
                float duration = main.loop ? DefaultPulseEffectSeconds
                    : main.duration + Mathf.Max(0f, main.startDelay.constant) + Mathf.Max(0f, main.startLifetime.constant);
                if (IsFinite(duration)) lifetime = Mathf.Max(lifetime, duration);
            }
            foreach (AudioSource audio in effect.GetComponentsInChildren<AudioSource>(true))
                if (audio != null && audio.clip != null && IsFinite(audio.clip.length)) lifetime = Mathf.Max(lifetime, audio.clip.length);
            if (lifetime <= 0f) lifetime = DefaultPulseEffectSeconds;
            return Mathf.Clamp(lifetime, 0.1f, MaxPulseEffectSeconds);
        }

        private void UpdateGuestPulseEffects(float now)
        {
            for (int i = _guestPulseEffects.Count - 1; i >= 0; i--)
            {
                GuestPulseEffect effect = _guestPulseEffects[i];
                if (effect.Object == null || now >= effect.DestroyAt)
                {
                    DestroyGuestPulseEffect(effect);
                    _guestPulseEffects.RemoveAt(i);
                    continue;
                }
                if (!effect.Pending) continue;
                if (HasGameplayComponents(effect.Object)) continue;
                try
                {
                    effect.Object.transform.SetParent(null, true);
                    effect.Object.SetActive(true);
                    effect.Pending = false;
                    PlayPulseEffect(effect);
                }
                catch (Exception)
                {
                    DestroyGuestPulseEffect(effect);
                    _guestPulseEffects.RemoveAt(i);
                }
            }
        }

        private static void PlayPulseEffect(GuestPulseEffect effect)
        {
            foreach (ParticleSystem particles in effect.Object.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles == null) continue;
                ParticleSystem.EmissionModule emission = particles.emission;
                emission.enabled = true;
                ParticleSystem.MainModule main = particles.main;
                main.maxParticles = Mathf.Clamp(main.maxParticles, 1, MaxEffectParticles);
                if (!particles.isPlaying) particles.Play(true);
            }
            foreach (AudioSource audio in effect.Object.GetComponentsInChildren<AudioSource>(true))
            {
                if (audio == null || audio.clip == null) continue;
                if (effect.SuppressAudio)
                {
                    audio.mute = true;
                    continue;
                }
                if (!audio.playOnAwake && !audio.isPlaying) GlobalAudioPlayer.Play(audio);
            }
        }

        private void DestroyPulseEffectsFor(int projectileId)
        {
            for (int i = _guestPulseEffects.Count - 1; i >= 0; i--)
            {
                if (_guestPulseEffects[i].ProjectileId != projectileId) continue;
                DestroyGuestPulseEffect(_guestPulseEffects[i]);
                _guestPulseEffects.RemoveAt(i);
            }
        }

        private static void DestroyGuestPulseEffect(GuestPulseEffect effect)
        {
            if (effect == null || effect.Object == null) return;
            effect.Object.SetActive(false);
            UnityEngine.Object.Destroy(effect.Object);
            effect.Object = null;
        }

        private void UpdateGuest(float now)
        {
            if (_guest.Count == 0 && _guestPulseEffects.Count == 0) return;
            long perf = PerfStats.Begin();
            AdvanceGuest(now);
            PerfStats.End("projectile.update", perf);
        }

        private void AdvanceGuest(float now)
        {
            UpdateGuestPulseEffects(now);
            _finishedGuest.Clear();
            foreach (var pair in _guest)
            {
                GuestProjectile visual = pair.Value;
                if (visual.Object == null)
                {
                    _finishedGuest.Add(pair.Key);
                    continue;
                }
                if (visual.Pending)
                {
                    if (HasGameplayComponents(visual.Object))
                    {
                        if (now >= visual.DestroyAt) _finishedGuest.Add(pair.Key);
                        continue;
                    }
                    try
                    {
                        visual.Object.transform.SetParent(null, true);
                        visual.Object.SetActive(true);
                        visual.Pending = false;
                        ProbeGuest("spawn", visual.Spawn, visual, "");
                    }
                    catch (Exception ex)
                    {
                        LogGuestSkip(visual.Spawn, "the prefab could not be activated safely: " + ex.GetType().Name + ": " + ex.Message);
                        _finishedGuest.Add(pair.Key);
                        continue;
                    }
                }
                if (visual.Removing)
                {
                    if (!visual.Stopped)
                    {
                        AdvanceVisual(visual);
                        if (now >= visual.StopAt)
                        {
                            visual.Object.transform.position = visual.To.Position;
                            visual.Object.transform.rotation = visual.To.Rotation;
                            visual.Remaining = 0f;
                            StopVisuals(visual.Object);
                            visual.Stopped = true;
                        }
                    }
                    if (now >= visual.DestroyAt) _finishedGuest.Add(pair.Key);
                    continue;
                }
                AdvanceVisual(visual);
            }
            foreach (int id in _finishedGuest)
            {
                bool keepPulseEffects = false;
                if (_guest.TryGetValue(id, out GuestProjectile visual))
                {
                    keepPulseEffects = visual.Removing;
                    ProbeGuest("destroy", visual.Spawn, visual, "");
                    DestroyGuestObject(visual);
                }
                if (!keepPulseEffects) DestroyPulseEffectsFor(id);
                _guest.Remove(id);
                _guestSkipLogged.Remove(id);
            }
        }

        private static void AdvanceVisual(GuestProjectile visual)
        {
            if (visual.Remaining <= 0f) return;
            float dt = Time.unscaledDeltaTime;
            float share = visual.Remaining <= dt ? 1f : dt / visual.Remaining;
            visual.Remaining = Mathf.Max(0f, visual.Remaining - dt);
            Transform t = visual.Object.transform;
            t.position = Vector3.Lerp(visual.From.Position, visual.To.Position, share);
            t.rotation = Quaternion.Slerp(visual.From.Rotation, visual.To.Rotation, share);
        }

        private static void StopVisuals(GameObject obj)
        {
            foreach (MeshRenderer renderer in obj.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer != null) renderer.enabled = false;
            foreach (SkinnedMeshRenderer renderer in obj.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer != null) renderer.enabled = false;
            foreach (ParticleSystem particles in obj.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (particles == null) continue;
                ParticleSystem.EmissionModule emission = particles.emission;
                emission.enabled = false;
            }
        }

        private void EnsureStaging()
        {
            if (_staging != null) return;
            _staging = new GameObject("PoECoop inactive projectile staging");
            _staging.hideFlags = HideFlags.HideAndDontSave;
            _staging.SetActive(false);
        }

        private void DestroyStaging()
        {
            if (_staging == null) return;
            UnityEngine.Object.Destroy(_staging);
            _staging = null;
        }

        // ---- prefab and entity lookup ----

        private Projectile ResolvePrefab(SpawnRecord spawn, out AttackRanged matchedAttack)
        {
            matchedAttack = null;
            List<AttackRanged> candidates = _prefabCandidates;
            candidates.Clear();
            try { return ResolvePrefab(spawn, candidates, ref matchedAttack); }
            finally { candidates.Clear(); }
        }

        private Projectile ResolvePrefab(SpawnRecord spawn, List<AttackRanged> candidates, ref AttackRanged matchedAttack)
        {
            GameObject attackObject = Resolve(spawn.AttackId);
            if (attackObject != null) AddMatching(candidates, attackObject.GetComponentsInChildren<AttackRanged>(true), spawn);
            Projectile direct = UniquePrefab(candidates, ref matchedAttack);
            if (direct != null) return direct;

            GameObject source = Resolve(spawn.SourceId);
            if (source != null && spawn.HasAbility)
            {
                GenericAbility ability = FindAbility(source, spawn);
                if (ability != null)
                {
                    candidates.Clear();
                    AddMatching(candidates, ability.GetComponentsInChildren<AttackRanged>(true), spawn);
                    Projectile fromAbility = UniquePrefab(candidates, ref matchedAttack);
                    if (fromAbility != null) return fromAbility;
                }
            }
            if (source != null && !spawn.HasAbility)
            {
                candidates.Clear();
                AddMatching(candidates, source.GetComponentsInChildren<AttackRanged>(true), spawn);
                Equipment equipment = source.GetComponent<Equipment>();
                if (equipment != null)
                {
                    if (equipment.PrimaryAttack is AttackRanged primary) AddMatching(candidates, primary, spawn);
                    if (equipment.SecondaryAttack is AttackRanged secondary) AddMatching(candidates, secondary, spawn);
                }
                Projectile fromSource = UniquePrefab(candidates, ref matchedAttack);
                if (fromSource != null) return fromSource;
            }

            return ScanForPrefab(spawn, candidates, ref matchedAttack);
        }

        /// <summary>
        /// Last resort: search every loaded attack. The outcome is remembered briefly per
        /// attack identity so a volley, or a refresh full of projectiles that cannot be
        /// resolved, costs one scene search instead of one per projectile.
        /// </summary>
        private Projectile ScanForPrefab(SpawnRecord spawn, List<AttackRanged> candidates, ref AttackRanged matchedAttack)
        {
            var key = new PrefabScanKey
            {
                AttackName = spawn.AttackName,
                AttackType = spawn.AttackType,
                PrefabName = spawn.PrefabName,
                AbilityName = spawn.HasAbility ? spawn.AbilityName : "",
                AbilityStringId = spawn.HasAbility ? spawn.AbilityStringId : -1,
                HasAbility = spawn.HasAbility,
            };
            float now = Time.unscaledTime;
            candidates.Clear();
            if (_prefabScans.TryGetValue(key, out PrefabScanResult cached) && now < cached.Until)
            {
                if ((object)cached.Attack == null) return null;
                AddMatching(candidates, cached.Attack, spawn);
                if (candidates.Count == 1) return UniquePrefab(candidates, ref matchedAttack);
                candidates.Clear();
            }

            PerfStats.Count("projectile.scan");
            AddMatching(candidates, UnityEngine.Object.FindObjectsOfType<AttackRanged>(), spawn);
            Projectile prefab = UniquePrefab(candidates, ref matchedAttack);
            if (_prefabScans.Count >= MaxPrefabScanCache && !_prefabScans.ContainsKey(key)) _prefabScans.Clear();
            _prefabScans[key] = new PrefabScanResult { Attack = prefab != null ? matchedAttack : null, Until = now + PrefabScanCacheSeconds };
            return prefab;
        }

        private static void AddMatching(List<AttackRanged> candidates, AttackRanged[] attacks, SpawnRecord spawn)
        {
            foreach (AttackRanged attack in attacks) AddMatching(candidates, attack, spawn);
        }

        private static void AddMatching(List<AttackRanged> candidates, AttackRanged attack, SpawnRecord spawn)
        {
            if (attack == null || attack.ProjectilePrefab == null) return;
            if (!NameEquals(attack.gameObject.name, spawn.AttackName)) return;
            if (!string.Equals(attack.GetType().FullName, spawn.AttackType, StringComparison.Ordinal)) return;
            if (!NameEquals(attack.ProjectilePrefab.name, spawn.PrefabName)) return;
            if (spawn.HasAbility)
            {
                GenericAbility ability = attack.AbilityOrigin;
                if (ability != null && (!NameEquals(ability.gameObject.name, spawn.AbilityName)
                    || ability.DisplayName.StringID != spawn.AbilityStringId)) return;
            }
            if (!candidates.Contains(attack)) candidates.Add(attack);
        }

        private static Projectile UniquePrefab(List<AttackRanged> candidates, ref AttackRanged matchedAttack)
        {
            Projectile result = null;
            foreach (AttackRanged attack in candidates)
            {
                if (result == null)
                {
                    result = attack.ProjectilePrefab;
                    matchedAttack = attack;
                }
                else if (result != attack.ProjectilePrefab)
                {
                    matchedAttack = null;
                    return null;
                }
            }
            return result;
        }

        private static GenericAbility FindAbility(GameObject source, SpawnRecord spawn)
        {
            int occurrence = 0;
            foreach (GenericAbility ability in source.GetComponentsInChildren<GenericAbility>(true))
            {
                if (ability == null || !IsAbility(ability, spawn.AbilityName, spawn.AbilityStringId)) continue;
                if (occurrence++ == spawn.AbilityOccurrence) return ability;
            }
            return null;
        }

        private static int AbilityOccurrence(GameObject source, GenericAbility wanted)
        {
            if (source == null) return 0;
            string name = NormalizeName(wanted.gameObject.name);
            int stringId = wanted.DisplayName.StringID;
            int occurrence = 0;
            foreach (GenericAbility ability in source.GetComponentsInChildren<GenericAbility>(true))
            {
                if (ability == null || !IsAbility(ability, name, stringId)) continue;
                if (ability == wanted) return occurrence;
                occurrence++;
            }
            return 0;
        }

        /// <summary>Same test as name-then-string-id, but the integer rejects most abilities before a name is copied out of Unity.</summary>
        private static bool IsAbility(GenericAbility ability, string normalizedName, int stringId)
        {
            DatabaseString display = ability.DisplayName;
            if (display != null && display.StringID != stringId) return false;
            return NameEquals(ability.gameObject.name, normalizedName) && ability.DisplayName.StringID == stringId;
        }

        private static Guid IdOf(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            Guid id = EntityRegistry.NetworkId(obj);
            if (id != Guid.Empty) return id;
            InstanceID instance = obj.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static GameObject Resolve(Guid id)
        {
            if (id == Guid.Empty) return null;
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj : InstanceID.GetObjectByID(id);
        }

        // ---- lifecycle and shared helpers ----

        public void Update(float now)
        {
            if (_session.IsHost)
            {
                if (!_wasHost)
                {
                    ResetHostStream();
                    ClearGuestVisuals();
                    DestroyStaging();
                }
                _wasHost = true;
                _wasGuest = false;
                UpdateHost(now);
                return;
            }
            if (_session.IsGuest)
            {
                if (!_wasGuest)
                {
                    ClearHost();
                    ClearGuestVisuals();
                    _guestEpoch = -1;
                    _guestSequence = -1;
                }
                _wasGuest = true;
                _wasHost = false;
                UpdateGuest(now);
                return;
            }
            if (_wasHost || _wasGuest || _hostOrder.Count > 0 || _guest.Count > 0)
            {
                ClearHost();
                ClearGuestVisuals();
                DestroyStaging();
                _guestEpoch = -1;
                _guestSequence = -1;
            }
            _wasHost = false;
            _wasGuest = false;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) _nextFullRefresh = 0f;
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            if (_session.IsHost) ResetHostStream();
            else
            {
                ClearHost();
                _guestEpoch = -1;
                _guestSequence = -1;
            }
            ClearGuestVisuals();
            DestroyStaging();
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private void ResetHostStream()
        {
            ClearHost();
            _epoch = NextEpoch(_epoch);
            _sequence = 0;
            _nextHostId = 1;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private void ClearHost()
        {
            _hostByObject.Clear();
            _hostById.Clear();
            _hostOrder.Clear();
            _pendingPulseCount = 0;
            _captureFrames.Clear();
            _capture = default(CaptureFrame);
        }

        private void ClearGuestVisuals()
        {
            foreach (var pair in _guest) DestroyGuestObject(pair.Value);
            foreach (GuestPulseEffect effect in _guestPulseEffects) DestroyGuestPulseEffect(effect);
            _guestPulseEffects.Clear();
            _guest.Clear();
            _guestSkipLogged.Clear();
            _finishedGuest.Clear();
            _prefabScans.Clear();
        }

        private static void DestroyGuestObject(GuestProjectile visual)
        {
            if (visual == null || visual.Object == null) return;
            visual.Object.SetActive(false);
            UnityEngine.Object.Destroy(visual.Object);
            visual.Object = null;
        }

        private void ProbeHost(string phase, HostProjectile state)
        {
            if (!_probe) return;
            AttackFirearm firearm = state.Attack as AttackFirearm;
            string data = "{\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                + "\",\"role\":\"host\",\"phase\":\"" + phase + "\",\"id\":" + state.Id
                + ",\"projectile\":\"" + ProbeJson(state.Object != null ? state.Object.name : "") + "\""
                + ",\"source\":\"" + state.SourceId + "\",\"attack\":\"" + ProbeJson(state.AttackName) + "\""
                + ",\"target\":\"" + state.TargetId + "\",\"prefab\":\"" + ProbeJson(state.PrefabName) + "\""
                + ",\"activeProjectiles\":" + (state.Attack != null ? state.Attack.ActiveProjectileCount : -1)
                + ",\"remainingShots\":" + (firearm != null ? firearm.RemainingShots : -1)
                + ",\"clipSize\":" + (firearm != null ? firearm.ClipSize : -1)
                + ",\"destroyDelay\":" + ProbeNumber(state.DestroyDelay)
                + ",\"pose\":" + ProbePose(state.Current) + "}";
            _session.Note("TEST projectile-visual " + data);
        }

        private void ProbeGuest(string phase, SpawnRecord spawn, GuestProjectile visual, string reason)
        {
            if (!_probe || spawn == null || visual == null) return;
            AttackFirearm firearm = visual.SourceAttack as AttackFirearm;
            Pose pose = visual.Object != null && TryReadPose(visual.Object.transform, out Pose current) ? current : visual.To;
            string data = "{\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                + "\",\"role\":\"guest\",\"phase\":\"" + phase + "\",\"id\":" + spawn.Id
                + ",\"source\":\"" + spawn.SourceId + "\",\"attack\":\"" + ProbeJson(spawn.AttackName) + "\""
                + ",\"target\":\"" + spawn.TargetId + "\",\"prefab\":\"" + ProbeJson(spawn.PrefabName) + "\""
                + ",\"resolvedAttack\":\"" + ProbeJson(visual.SourceAttack != null ? visual.SourceAttack.gameObject.name : "") + "\""
                + ",\"activeProjectiles\":" + (visual.SourceAttack != null ? visual.SourceAttack.ActiveProjectileCount : -1)
                + ",\"remainingShots\":" + (firearm != null ? firearm.RemainingShots : -1)
                + ",\"clipSize\":" + (firearm != null ? firearm.ClipSize : -1)
                + ",\"scripts\":" + (visual.Object != null ? visual.Object.GetComponentsInChildren<MonoBehaviour>(true).Length : -1)
                + ",\"rigidbodies\":" + (visual.Object != null ? visual.Object.GetComponentsInChildren<Rigidbody>(true).Length : -1)
                + ",\"colliders\":" + (visual.Object != null ? visual.Object.GetComponentsInChildren<Collider>(true).Length : -1)
                + ",\"destroyDelay\":" + ProbeNumber(spawn.DestroyDelay)
                + ",\"pose\":" + ProbePose(pose)
                + ",\"reason\":\"" + ProbeJson(reason) + "\"}";
            _session.Note("TEST projectile-visual " + data);
        }

        private static string ProbePose(Pose pose)
        {
            return "{\"position\":[" + ProbeNumber(pose.Position.x) + "," + ProbeNumber(pose.Position.y) + "," + ProbeNumber(pose.Position.z)
                + "],\"rotation\":[" + ProbeNumber(pose.Rotation.x) + "," + ProbeNumber(pose.Rotation.y) + ","
                + ProbeNumber(pose.Rotation.z) + "," + ProbeNumber(pose.Rotation.w) + "]}";
        }

        private static string ProbeNumber(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string ProbeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private void LogGuestSkip(SpawnRecord spawn, string why)
        {
            if (!_guestSkipLogged.Contains(spawn.Id) && _guestSkipLogged.Count >= MaxTracked) return;
            if (!_guestSkipLogged.Add(spawn.Id)) return;
            Plugin.Log?.LogWarning("Skipping host projectile " + spawn.Id + " (" + spawn.PrefabName + "): " + why);
        }

        private static int NextEpoch(int epoch) => epoch == int.MaxValue ? 0 : epoch + 1;
        private static float SafeDelay(float delay) => IsFinite(delay) ? Mathf.Clamp(delay, 0f, MaxDestroyDelay) : 0f;
        private static string Clean(string value) => Protocol.Clean(value ?? "", MaxStringLength);

        private static string NormalizeName(string value)
        {
            value = Clean(value);
            const string clone = "(Clone)";
            if (value.EndsWith(clone, StringComparison.Ordinal))
                value = value.Substring(0, value.Length - clone.Length).TrimEnd();
            return value;
        }

        private static bool NameEquals(string left, string normalizedRight)
        {
            return string.Equals(NormalizeName(left), normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PulseIsNewer(int sequence, int previous)
        {
            if (previous < 0 || sequence == previous) return previous < 0;
            long distance = sequence - previous;
            if (distance < 0) distance += (long)int.MaxValue + 1L;
            return distance > 0 && distance <= int.MaxValue / 2;
        }

        private static bool TryReadPose(Transform transform, out Pose pose)
        {
            return TryPose(transform.position, transform.rotation, out pose);
        }

        private static bool TryPose(Vector3 position, Quaternion rotation, out Pose pose)
        {
            pose = new Pose { Position = position, Rotation = rotation };
            if (!IsFinite(pose.Position) || Mathf.Abs(pose.Position.x) > MaxCoordinate
                || Mathf.Abs(pose.Position.y) > MaxCoordinate || Mathf.Abs(pose.Position.z) > MaxCoordinate
                || !IsFinite(pose.Rotation.x) || !IsFinite(pose.Rotation.y) || !IsFinite(pose.Rotation.z) || !IsFinite(pose.Rotation.w))
                return false;
            float magnitude = pose.Rotation.x * pose.Rotation.x + pose.Rotation.y * pose.Rotation.y
                + pose.Rotation.z * pose.Rotation.z + pose.Rotation.w * pose.Rotation.w;
            if (magnitude < 0.25f || magnitude > 2.25f) return false;
            pose.Rotation = Normalize(pose.Rotation);
            return true;
        }

        private static Pose ReadLocalPose(Transform transform, Pose fallback)
        {
            return TryReadPose(transform, out Pose pose) ? pose : fallback;
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude);
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
