using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Reconciles the host's complete, ordered effect list for each target. Guest
    /// effects are real StatusEffect objects, but are inserted without ApplyEffect
    /// or ClearEffect so they are data for the UI, never a second simulation.
    /// A manifest carries a target's structure (identity and every field that does not
    /// advance with time) followed by its timer values; while the structure is unchanged
    /// the host sends only the timer values that moved.
    /// </summary>
    public sealed class StatusEffectSync : IDisposable
    {
        private const float SendInterval = 0.25f;
        private const float FullRefreshInterval = 3f;
        private const float GuestRetryInterval = 0.25f;
        private const float MaxGuestRetryInterval = 1f;
        /// <summary>Keeps the first periodic refresh after a forced one out of phase with the other snapshot streams.</summary>
        private const float ForcedRefreshPhase = 1.35f;
        /// <summary>Manifest body header: tag, effect count, structure revision, structure byte count.</summary>
        private const int TargetHeaderBytes = 13;
        private const int StructureRevisionOffset = 5;
        private const int StructureLengthOffset = 9;
        /// <summary>Time active, interval timer and interval count of one effect.</summary>
        private const int TimerBytesPerEffect = 12;
        /// <summary>First byte of a values-only target body; a manifest body starts with the message type.</summary>
        private const byte ValuesBodyTag = 0xF0;
        private const int ValuesHeaderBytes = 9;
        private const int MaxValuesEntryBytes = 14;
        private const int MaxTargets = 512;
        private const int MaxTargetsPerMessage = 128;
        private const int MaxEffectsPerTarget = 256;
        private const int MaxMessageBytes = 512 * 1024;
        private const int MaxTargetBytes = 400 * 1024;
        private const int MaxStringLength = 512;
        private const float MaxMagnitude = 1000000000f;
        private const int MaxCounter = 1000000;

        private const byte FullDelta = 0;
        private const byte FullSingle = 1;
        private const byte FullBegin = 2;
        private const byte FullContinuation = 3;
        private const byte FullEnd = 4;

        private enum OriginKind : byte { None, Affliction, Ability, Equipment, Phrase }
        private enum TemplateKind : byte { Inline, Origin, Attack, ItemMod, PhraseData, AbilityMod }

        private static readonly AccessTools.FieldRef<CharacterStats, List<StatusEffect>> EffectList =
            AccessTools.FieldRefAccess<CharacterStats, List<StatusEffect>>("m_statusEffects");
        private static readonly AccessTools.FieldRef<CharacterStats, bool> UpdateTracker =
            AccessTools.FieldRefAccess<CharacterStats, bool>("m_updateTracker");
        private static readonly AccessTools.FieldRef<StatusEffect, GameObject> EffectTarget =
            AccessTools.FieldRefAccess<StatusEffect, GameObject>("m_target");
        private static readonly AccessTools.FieldRef<StatusEffect, CharacterStats> EffectTargetStats =
            AccessTools.FieldRefAccess<StatusEffect, CharacterStats>("m_targetStats");
        private static readonly AccessTools.FieldRef<StatusEffect, GameObject> EffectOwner =
            AccessTools.FieldRefAccess<StatusEffect, GameObject>("m_owner");
        private static readonly AccessTools.FieldRef<StatusEffect, CharacterStats> EffectOwnerStats =
            AccessTools.FieldRefAccess<StatusEffect, CharacterStats>("m_ownerStats");
        private static readonly AccessTools.FieldRef<StatusEffect, float> BaseDuration =
            AccessTools.FieldRefAccess<StatusEffect, float>("<m_duration>k__BackingField");
        private static readonly AccessTools.FieldRef<StatusEffect, float> TemporaryDuration =
            AccessTools.FieldRefAccess<StatusEffect, float>("m_tempDurationAdjust");
        private static readonly AccessTools.FieldRef<StatusEffect, bool> TacticalClear =
            AccessTools.FieldRefAccess<StatusEffect, bool>("m_TacticalClear");
        private static readonly AccessTools.FieldRef<StatusEffect, string> AfflictionKeyword =
            AccessTools.FieldRefAccess<StatusEffect, string>("m_afflictionKeyword");

        private static readonly MethodInfo ShallowClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AddedEvent = typeof(CharacterStats).GetField("OnAddStatusEffect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RemovedEvent = typeof(CharacterStats).GetField("OnClearStatusEffect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo CleanedUpProperty = typeof(StatusEffectParams).GetProperty("IsCleanedUp");
        private static readonly PropertyInfo MergedValueProperty = typeof(StatusEffectParams).GetProperty("MergedValue");
        private static readonly Action<StatusEffectParams, bool> SetCleanedUp = CreateSetter<bool>(CleanedUpProperty);
        private static readonly Action<StatusEffectParams, float> SetMergedValue = CreateSetter<float>(MergedValueProperty);
        private static readonly StatusEffectTrigger DefaultTrigger = new StatusEffectTrigger();
        private static readonly StatusEffectLevelScaling DefaultScaling = new StatusEffectLevelScaling();
        private static readonly byte[] EmptyTargetBody = { (byte)MessageType.StatusEffects, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        private static readonly HashSet<int> AbilityTypes = EnumValues(typeof(GenericAbility.AbilityType));
        private static readonly HashSet<int> ApplyTypes = EnumValues(typeof(StatusEffect.ApplyType));
        private static readonly HashSet<int> ModifiedStats = EnumValues(typeof(StatusEffect.ModifiedStat));
        private static readonly HashSet<int> DamageTypes = EnumValues(typeof(DamagePacket.DamageType));
        private static readonly HashSet<int> IntervalRates = EnumValues(typeof(StatusEffectParams.IntervalRateType));
        private static readonly HashSet<int> Races = EnumValues(typeof(CharacterStats.Race));
        private static readonly HashSet<int> Classes = EnumValues(typeof(CharacterStats.Class));
        private static readonly HashSet<int> AttributeTypes = EnumValues(typeof(CharacterStats.AttributeScoreType));
        private static readonly HashSet<int> DefenseTypes = EnumValues(typeof(CharacterStats.DefenseType));
        private static readonly HashSet<int> EquipmentSlots = EnumValues(typeof(Equippable.EquipmentSlot));
        private static readonly HashSet<int> TriggerTypes = EnumValues(typeof(StatusEffectTrigger.TriggerType));

        // Lookups that are stable for one host sample or one guest reconciliation pass.
        // They are cleared at both ends of the pass, so nothing outlives the frame.
        private static class Children<T> where T : Component
        {
            internal static readonly Dictionary<int, T[]> ByOwner = new Dictionary<int, T[]>();
        }
        private static readonly Dictionary<int, string> s_passNames = new Dictionary<int, string>();
        private static readonly Dictionary<int, Guid> s_passIds = new Dictionary<int, Guid>();
        private static readonly Dictionary<int, Phrase[]> s_passKnownPhrases = new Dictionary<int, Phrase[]>();
        private static readonly List<Affliction> s_passAfflictions = new List<Affliction>();
        private static bool s_passAfflictionsBuilt;
        private static FieldInfo[] s_afflictionFields;

        [StructLayout(LayoutKind.Explicit)]
        private struct GuidBits
        {
            [FieldOffset(0)] public Guid Value;
            [FieldOffset(0)] public long Low;
            [FieldOffset(8)] public long High;
        }

        private static int s_applyingHostStatus;
        internal static bool ApplyingHostStatus => s_applyingHostStatus != 0;

        private readonly Session _session;
        private readonly Action<PlayerInfo> _playerJoined;
        private readonly Dictionary<Guid, HostTarget> _sent = new Dictionary<Guid, HostTarget>();
        private readonly HashSet<Guid> _current = new HashSet<Guid>();
        private readonly List<Outgoing> _changed = new List<Outgoing>();
        private readonly List<Guid> _missing = new List<Guid>();
        private readonly List<StatusEffect> _captureEffects = new List<StatusEffect>();
        private readonly MemoryStream _valuesStream = new MemoryStream(1024);
        private readonly BinaryWriter _valuesWriter;
        private int _structureRevision;
        private readonly Dictionary<int, string> _sortNames = new Dictionary<int, string>();
        private const float SetCheckInterval = 30f;
        private static readonly List<CharacterStats> s_enabled = new List<CharacterStats>();
        private static readonly HashSet<int> s_enabledIds = new HashSet<int>();
        private static bool s_enabledSeeded;
        private static bool s_enabledDrifted;
        private readonly HashSet<int> _scratchInstances = new HashSet<int>();
        private CharacterStats[] _targets = new CharacterStats[64];
        private float _nextSetCheck;
        private string[] _sortKeys = new string[64];
        private readonly MemoryStream _captureStream = new MemoryStream(4096);
        private readonly BinaryWriter _captureWriter;
        private readonly List<int> _effectEnds = new List<int>();
        private readonly MemoryStream _messageStream = new MemoryStream(4096);
        private readonly BinaryWriter _messageWriter;
        private readonly HashSet<uint> _scratchIds = new HashSet<uint>();
        private readonly List<uint> _scratchAbsent = new List<uint>();
        private readonly OriginReferences _origins = new OriginReferences();
        private readonly HashSet<string> _hostWarnings = new HashSet<string>();
        private readonly Dictionary<Guid, GuestTarget> _guestTargets = new Dictionary<Guid, GuestTarget>();
        private readonly HashSet<string> _guestWarnings = new HashSet<string>();
        private float _nextSend;
        private float _nextFullRefresh;
        private float _nextGuestRetry;
        private int _epoch;
        private int _revision;
        private int _guestEpoch = int.MinValue;
        private int _guestRevision;
        private Dictionary<Guid, Incoming> _fullStaging;

        /// <summary>What the guests hold for one target: the last manifest and the timer values sent since.</summary>
        private sealed class HostTarget
        {
            public CharacterStats Stats;
            public byte[] Body;
            public int StructureRevision;
            /// <summary>Hash of the cheap structural inputs sampled when the structure was last serialised.</summary>
            public ulong Signature;
            /// <summary>The effects of the manifest, in manifest order.</summary>
            public StatusEffect[] Effects = new StatusEffect[8];
            public float[] TimeActive = new float[8];
            public float[] IntervalTimer = new float[8];
            public uint[] IntervalCount = new uint[8];
            public int Count;
            /// <summary>The whole live list at that time, including effects the manifest had to omit.</summary>
            public StatusEffect[] Live = new StatusEffect[8];
            public int LiveCount;
        }

        /// <summary>One target body to send: a manifest array, or a values-only span of the values buffer when Body is null.</summary>
        private struct Outgoing
        {
            public Guid Id;
            public byte[] Body;
            public int Offset;
            public int Length;
        }

        private sealed class GuestTarget
        {
            public List<EffectRecord> Records = new List<EffectRecord>();
            public readonly Dictionary<uint, MirrorEffect> Effects = new Dictionary<uint, MirrorEffect>();
            /// <summary>The host's encoded manifest that Records was parsed from, with later timer values written in.</summary>
            public byte[] Raw;
            public bool Dirty = true;
            public bool Initialized;
            /// <summary>An identical manifest arrived for an applied target; check that the live list still matches.</summary>
            public bool Verify;
            public GameObject AppliedObject;
            public CharacterStats AppliedStats;
            public int Failures;
            public float NextRetry;
        }

        /// <summary>
        /// One target of a received message. Records is null for a values-only body and while a
        /// manifest has the structure the target already holds.
        /// </summary>
        private sealed class Incoming
        {
            public byte[] Raw;
            public List<EffectRecord> Records;
            public bool Values;
        }

        private sealed class MirrorEffect
        {
            public StatusEffect Effect;
            public bool WarningLogged;
        }

        private sealed class ApplyScope : IDisposable
        {
            public ApplyScope() { s_applyingHostStatus++; }
            public void Dispose() { s_applyingHostStatus--; }
        }

        public StatusEffectSync(Session session)
        {
            _session = session;
            _captureWriter = new BinaryWriter(_captureStream, Encoding.UTF8);
            _messageWriter = new BinaryWriter(_messageStream, Encoding.UTF8);
            _valuesWriter = new BinaryWriter(_valuesStream, Encoding.UTF8);
            session.GameMessage += OnGameMessage;
            _playerJoined = _ => _nextFullRefresh = 0f;
            session.PlayerJoined += _playerJoined;
            GameState.OnLevelLoaded += OnLevelLoaded;
            GameState.OnLevelUnload += OnLevelLoaded;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= _playerJoined;
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelLoaded;
            if (_guestTargets.Count > 0) ClearMirroredEffects();
            _guestTargets.Clear();
            _fullStaging = null;
            _sent.Clear();
            _current.Clear();
            _changed.Clear();
            _sortNames.Clear();
            ClearPassLookups();
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _sent.Clear();
            _current.Clear();
            _changed.Clear();
            _sortNames.Clear();
            _hostWarnings.Clear();
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            _nextSetCheck = 0f;
            if (_session.IsHost)
            {
                _epoch = unchecked(_epoch + 1);
                _revision = 0;
            }
            if (_session.IsGuest)
            {
                ResetGuest();
                ScrubAllEffects();
            }
        }

        public void Update(float now)
        {
            if (_session.IsHost)
            {
                UpdateHost(now);
                return;
            }
            if (!_session.IsGuest)
            {
                if (_guestTargets.Count > 0) ResetGuest();
                return;
            }
            if (GameState.IsLoading || now < _nextGuestRetry) return;
            _nextGuestRetry = now + GuestRetryInterval;
            if (_guestTargets.Count == 0) return;

            long started = PerfStats.Begin();
            ClearPassLookups();
            try
            {
                foreach (var pair in _guestTargets)
                {
                    GuestTarget target = pair.Value;
                    if (!target.Dirty)
                    {
                        if (!target.Verify) continue;
                        target.Verify = false;
                        if (StillApplied(pair.Key, target))
                        {
                            PerfStats.Count("status.skipped");
                            continue;
                        }
                    }
                    else if (now < target.NextRetry) continue;

                    bool effectFailure = ReconcileTarget(pair.Key, target);
                    PerfStats.Count("status.records");
                    if (!target.Dirty || !effectFailure)
                    {
                        // An unresolved target costs two dictionary lookups, so it keeps the base cadence.
                        target.Failures = 0;
                        target.NextRetry = 0f;
                        continue;
                    }
                    // Effects whose owner or origin stays unresolved repeat the whole origin search;
                    // back off 0.25 s, 0.5 s, then 1 s until the host sends this target again.
                    if (target.Failures < 3) target.Failures++;
                    target.NextRetry = now + Math.Min(MaxGuestRetryInterval, GuestRetryInterval * (1 << (target.Failures - 1))) - 0.05f;
                    PerfStats.Count("status.retry");
                }
            }
            finally
            {
                ClearPassLookups();
                PerfStats.End("status.update", started);
            }
        }

        // ---- host sampling and wire output ----

        private void UpdateHost(float now)
        {
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            long started = PerfStats.Begin();
            bool full = now >= _nextFullRefresh;
            if (full)
            {
                // A forced refresh (join, level change) is followed by a longer first gap so the
                // periodic refreshes of the snapshot streams do not land in the same frame.
                bool forced = _nextFullRefresh <= 0f;
                _nextFullRefresh = now + FullRefreshInterval + (forced ? ForcedRefreshPhase : 0f);
                _sortNames.Clear();
            }

            _current.Clear();
            _changed.Clear();
            _valuesStream.SetLength(0);
            ClearPassLookups();
            bool completed = false;
            try
            {
                int count = CollectTargets(now, full);
                CharacterStats[] all = _targets;
                SortByName(all, count);
                int manifests = 0;
                int values = 0;
                int unchanged = 0;
                for (int i = 0; i < count && _current.Count < MaxTargets; i++)
                {
                    CharacterStats stats = all[i];
                    if (stats == null || !stats.isActiveAndEnabled) continue;
                    Guid id = GetId(stats.gameObject);
                    if (id == Guid.Empty || !_current.Add(id)) continue;
                    HostTarget target;
                    _sent.TryGetValue(id, out target);
                    switch (CaptureTarget(id, stats, target, full))
                    {
                        case Capture.Manifest: manifests++; break;
                        case Capture.Values: values++; break;
                        default: unchanged++; break;
                    }
                }
                if (count > MaxTargets) WarnHostOnce("targets", "Too many status-effect targets; only " + MaxTargets + " are synchronized.");

                // A full manifest drops absent targets on the guest by itself; a delta names them.
                _missing.Clear();
                foreach (Guid known in _sent.Keys)
                    if (!_current.Contains(known)) _missing.Add(known);
                for (int i = 0; i < _missing.Count; i++)
                {
                    _sent.Remove(_missing[i]);
                    if (full) continue;
                    _changed.Add(new Outgoing { Id = _missing[i], Body = EmptyTargetBody, Length = EmptyTargetBody.Length });
                    manifests++;
                }

                SendSnapshots(_changed, full);
                completed = true;

                if (full) PerfStats.Count("status.full");
                else if (_changed.Count > 0) PerfStats.Count("status.delta");
                PerfStats.Count("status.records", manifests);
                PerfStats.Count("status.values", values);
                PerfStats.Count("status.skipped", unchanged);
            }
            finally
            {
                if (!completed)
                {
                    // What the guests hold is unknown after a failed sample; start again from a full manifest.
                    _sent.Clear();
                    _nextFullRefresh = 0f;
                }
                _current.Clear();
                _changed.Clear();
                _missing.Clear();
                _captureEffects.Clear();
                Array.Clear(_targets, 0, _targets.Length);
                ClearPassLookups();
                PerfStats.End("status.capture", started);
            }
        }

        /// <summary>Orders targets by name as before, reading each native name once per full refresh instead of once per comparison.</summary>
        private void SortByName(CharacterStats[] all, int count)
        {
            if (_sortKeys.Length < count) _sortKeys = new string[Math.Max(count, _sortKeys.Length * 2)];
            for (int i = 0; i < count; i++)
            {
                int key = all[i].GetInstanceID();
                string name;
                if (!_sortNames.TryGetValue(key, out name)) _sortNames.Add(key, name = all[i].name);
                _sortKeys[i] = name;
            }
            Array.Sort(_sortKeys, all, 0, count, StringComparer.Ordinal);
            Array.Clear(_sortKeys, 0, count);
        }

        // ---- host target set ----
        //
        // Searching the scene for every CharacterStats costs several milliseconds, and the sample runs
        // four times a second. The enabled components are tracked from their own OnEnable and
        // OnDisable instead; a search still seeds the set, repeats after every level change and
        // every SetCheckInterval, and repairs the set if the two ever disagree.

        internal static void TrackEnabled(CharacterStats stats)
        {
            if (s_enabledIds.Add(stats.GetInstanceID())) s_enabled.Add(stats);
        }

        internal static void TrackDisabled(CharacterStats stats)
        {
            if (!s_enabledIds.Remove(stats.GetInstanceID())) return;
            for (int i = s_enabled.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(s_enabled[i], stats)) continue;
                s_enabled[i] = s_enabled[s_enabled.Count - 1];
                s_enabled.RemoveAt(s_enabled.Count - 1);
                return;
            }
        }

        /// <summary>Fills _targets with the enabled CharacterStats and returns how many there are.</summary>
        private int CollectTargets(float now, bool full)
        {
            // Once the hooks have been caught missing a change, every full refresh searches again.
            if (!s_enabledSeeded || now >= _nextSetCheck || s_enabledDrifted && full)
            {
                _nextSetCheck = now + SetCheckInterval;
                CheckEnabledSet();
            }
            int count = 0;
            for (int i = s_enabled.Count - 1; i >= 0; i--)
            {
                CharacterStats stats = s_enabled[i];
                if (stats == null)
                {
                    // Destroyed without a disable call reaching the hook.
                    s_enabledIds.Remove(stats.GetInstanceID());
                    s_enabled[i] = s_enabled[s_enabled.Count - 1];
                    s_enabled.RemoveAt(s_enabled.Count - 1);
                    continue;
                }
                if (count == _targets.Length) Array.Resize(ref _targets, _targets.Length * 2);
                _targets[count++] = stats;
            }
            return count;
        }

        private void CheckEnabledSet()
        {
            long started = PerfStats.Begin();
            CharacterStats[] found = UnityEngine.Object.FindObjectsOfType<CharacterStats>();
            int wrong = 0;
            _scratchInstances.Clear();
            for (int i = 0; i < found.Length; i++)
            {
                CharacterStats stats = found[i];
                if (stats == null || !stats.isActiveAndEnabled) continue;
                _scratchInstances.Add(stats.GetInstanceID());
                if (!s_enabledIds.Add(stats.GetInstanceID())) continue;
                s_enabled.Add(stats);
                wrong++;
            }
            for (int i = s_enabled.Count - 1; i >= 0; i--)
            {
                CharacterStats stats = s_enabled[i];
                if (stats != null && _scratchInstances.Contains(stats.GetInstanceID())) continue;
                s_enabledIds.Remove(stats.GetInstanceID());
                s_enabled[i] = s_enabled[s_enabled.Count - 1];
                s_enabled.RemoveAt(s_enabled.Count - 1);
                if (stats != null) wrong++;
            }
            _scratchInstances.Clear();
            if (s_enabledSeeded && wrong > 0)
            {
                s_enabledDrifted = true;
                PerfStats.Count("status.set_drift", wrong);
                WarnHostOnce("target-set", "The tracked status-effect target set missed " + wrong + " change(s); searching the scene at every refresh from now on.");
            }
            s_enabledSeeded = true;
            PerfStats.End("status.scan", started);
        }

        private enum Capture { Unchanged, Values, Manifest }

        /// <summary>
        /// Queues what the guests need for one target. Between full refreshes a target whose cheap
        /// structural inputs are unchanged is not serialised at all: only its moved timer values are
        /// queued. Otherwise the structure is serialised and compared with the manifest last sent;
        /// a changed structure, and every target of a full refresh, is queued as a complete manifest.
        /// </summary>
        private Capture CaptureTarget(Guid id, CharacterStats stats, HostTarget target, bool full)
        {
            if (!full && target != null && ReferenceEquals(target.Stats, stats) && SameLiveEffects(target, stats)
                && target.Signature == ComputeSignature(stats))
            {
                int queued = QueueValues(id, target);
                if (queued >= 0) return queued > 0 ? Capture.Values : Capture.Unchanged;
                // A timer left its bound; the full encoding below rejects that effect as it always did.
            }

            IList<StatusEffect> effects = stats.ActiveStatusEffects;
            MemoryStream stream = _captureStream;
            BinaryWriter writer = _captureWriter;
            stream.SetLength(0);
            _effectEnds.Clear();
            _captureEffects.Clear();
            writer.Write((byte)MessageType.StatusEffects);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (int i = 0; i < effects.Count && _effectEnds.Count < MaxEffectsPerTarget; i++)
            {
                StatusEffect effect = effects[i];
                if (effect == null || effect.Params == null) continue;
                string error;
                if (TryWriteEffect(writer, stream, effect, out error))
                {
                    _effectEnds.Add((int)stream.Length);
                    _captureEffects.Add(effect);
                }
                else
                {
                    string key = id + ":" + effect.EffectID + ":" + error;
                    if (_hostWarnings.Add(key))
                        Plugin.Log?.LogWarning("Unsupported status effect " + Describe(effect) + " on " + stats.name + ": " + error);
                }
            }
            if (effects.Count > MaxEffectsPerTarget)
                WarnHostOnce(id + ":count", "Status effect list on " + stats.name + " exceeded " + MaxEffectsPerTarget + "; extra effects were omitted.");

            while (stream.Length + (long)_effectEnds.Count * TimerBytesPerEffect > MaxTargetBytes)
            {
                if (_effectEnds.Count == 0) throw new InvalidOperationException("empty status target exceeded its size bound");
                _effectEnds.RemoveAt(_effectEnds.Count - 1);
                _captureEffects.RemoveAt(_captureEffects.Count - 1);
                stream.SetLength(_effectEnds.Count > 0 ? _effectEnds[_effectEnds.Count - 1] : TargetHeaderBytes);
                WarnHostOnce(id + ":bytes", "Status effect list on " + stats.name + " exceeded the per-target byte bound; trailing effects were omitted.");
            }

            int count = _effectEnds.Count;
            int structureEnd = (int)stream.Length;
            stream.Position = 1;
            writer.Write(count);
            stream.Position = StructureLengthOffset;
            writer.Write(structureEnd - TargetHeaderBytes);
            writer.Flush();
            stream.Position = structureEnd;

            bool sameStructure = target != null && SameStructure(target.Body, stream.GetBuffer(), structureEnd);
            if (target == null) _sent.Add(id, target = new HostTarget());
            target.Stats = stats;
            if (target.Effects.Length < count)
            {
                // The sent timer values are only read again while the structure, and so the count, is unchanged.
                int capacity = Math.Max(count, target.Effects.Length * 2);
                target.Effects = new StatusEffect[capacity];
                target.TimeActive = new float[capacity];
                target.IntervalTimer = new float[capacity];
                target.IntervalCount = new uint[capacity];
            }
            else if (target.Count > count) Array.Clear(target.Effects, count, target.Count - count);
            for (int i = 0; i < count; i++) target.Effects[i] = _captureEffects[i];
            target.Count = count;
            StoreLiveEffects(target, stats);
            target.Signature = ComputeSignature(stats);

            if (sameStructure && !full)
                return QueueValues(id, target) > 0 ? Capture.Values : Capture.Unchanged;

            if (!sameStructure)
            {
                _structureRevision = unchecked(_structureRevision + 1);
                if (_structureRevision == 0) _structureRevision = 1;
                target.StructureRevision = _structureRevision;
            }
            stream.Position = StructureRevisionOffset;
            writer.Write(target.StructureRevision);
            writer.Flush();
            stream.Position = structureEnd;
            for (int i = 0; i < count; i++)
            {
                StatusEffect effect = target.Effects[i];
                writer.Write(target.TimeActive[i] = effect.m_timeActive);
                writer.Write(target.IntervalTimer[i] = effect.m_intervalTimer);
                writer.Write(target.IntervalCount[i] = effect.m_intervalCount);
            }
            writer.Flush();

            // A manifest identical to the one last sent (a refresh of a target without running timers) reuses its array.
            int length = (int)stream.Length;
            byte[] previous = target.Body;
            bool identical = previous != null && previous.Length == length;
            if (identical)
            {
                byte[] buffer = stream.GetBuffer();
                int i = 0;
                while (i < length && buffer[i] == previous[i]) i++;
                identical = i == length;
            }
            if (!identical) target.Body = stream.ToArray();
            _changed.Add(new Outgoing { Id = id, Body = target.Body, Length = target.Body.Length });
            return Capture.Manifest;
        }

        /// <summary>
        /// Queues a values-only body with every timer value of the target that differs from what was
        /// last sent. Returns 1 when one was queued, 0 when nothing moved and -1, with nothing queued,
        /// when a moved timer is not a bounded number.
        /// </summary>
        private int QueueValues(Guid id, HostTarget target)
        {
            StatusEffect[] effects = target.Effects;
            int entries = 0;
            for (int i = 0; i < target.Count; i++)
            {
                StatusEffect effect = effects[i];
                float timeActive = effect.m_timeActive;
                float intervalTimer = effect.m_intervalTimer;
                if (timeActive == target.TimeActive[i] && intervalTimer == target.IntervalTimer[i]
                    && effect.m_intervalCount == target.IntervalCount[i]) continue;
                if (!IsBounded(timeActive) || !IsBounded(intervalTimer)) return -1;
                entries++;
            }
            if (entries == 0) return 0;

            MemoryStream stream = _valuesStream;
            BinaryWriter writer = _valuesWriter;
            int start = (int)stream.Length;
            writer.Write(ValuesBodyTag);
            writer.Write(target.StructureRevision);
            writer.Write(entries);
            for (int i = 0; i < target.Count; i++)
            {
                StatusEffect effect = effects[i];
                float timeActive = effect.m_timeActive;
                float intervalTimer = effect.m_intervalTimer;
                uint intervalCount = effect.m_intervalCount;
                int mask = 0;
                if (timeActive != target.TimeActive[i]) mask |= 1;
                if (intervalTimer != target.IntervalTimer[i]) mask |= 2;
                if (intervalCount != target.IntervalCount[i]) mask |= 4;
                if (mask == 0) continue;
                writer.Write((byte)i);
                writer.Write((byte)mask);
                if ((mask & 1) != 0) writer.Write(target.TimeActive[i] = timeActive);
                if ((mask & 2) != 0) writer.Write(target.IntervalTimer[i] = intervalTimer);
                if ((mask & 4) != 0) writer.Write(target.IntervalCount[i] = intervalCount);
            }
            writer.Flush();
            _changed.Add(new Outgoing { Id = id, Offset = start, Length = (int)stream.Length - start });
            return 1;
        }

        /// <summary>True while the target's live list holds the same effect objects, in order, as when its structure was serialised.</summary>
        private static bool SameLiveEffects(HostTarget target, CharacterStats stats)
        {
            List<StatusEffect> live = EffectList(stats);
            if (live.Count != target.LiveCount) return false;
            StatusEffect[] known = target.Live;
            for (int i = 0; i < live.Count; i++)
                if (!ReferenceEquals(live[i], known[i])) return false;
            return true;
        }

        private static void StoreLiveEffects(HostTarget target, CharacterStats stats)
        {
            List<StatusEffect> live = EffectList(stats);
            if (target.Live.Length < live.Count) target.Live = new StatusEffect[Math.Max(live.Count, target.Live.Length * 2)];
            else if (target.LiveCount > live.Count) Array.Clear(target.Live, live.Count, target.LiveCount - live.Count);
            live.CopyTo(target.Live);
            target.LiveCount = live.Count;
        }

        /// <summary>
        /// Hashes every input of an effect's structure that can be read without searching: the same
        /// fields the manifest encodes, with objects by identity and without the three timers. What it
        /// cannot see (an origin's name, ordinal or display string, a template index) changes only
        /// together with one of these inputs or is picked up by the next full refresh.
        /// </summary>
        private static ulong ComputeSignature(CharacterStats stats)
        {
            List<StatusEffect> effects = EffectList(stats);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < effects.Count; i++)
            {
                StatusEffect effect = effects[i];
                StatusEffectParams p = effect != null ? effect.Params : null;
                if (p == null)
                {
                    Mix(ref hash, 0);
                    continue;
                }
                Mix(ref hash, RuntimeHelpers.GetHashCode(p));
                Mix(ref hash, unchecked((int)effect.EffectID));
                MixId(ref hash, GetId(effect.Owner));
                MixId(ref hash, GetId(effect.Source));
                MixId(ref hash, GetId(effect.ExtraObject));
                MixObject(ref hash, effect.AfflictionOrigin);
                MixObject(ref hash, effect.AbilityOrigin);
                MixObject(ref hash, effect.PhraseOrigin);
                Equippable equipment = effect.EquipmentOrigin;
                MixObject(ref hash, equipment);
                if (equipment != null) MixId(ref hash, GetId(equipment.gameObject));
                Mix(ref hash, (int)effect.AbilityType);
                Mix(ref hash, EffectFlags(effect));
                Mix(ref hash, unchecked((int)effect.m_suspensionCount));
                Mix(ref hash, BaseDuration(effect));
                Mix(ref hash, effect.m_durationOverride);
                Mix(ref hash, TemporaryDuration(effect));
                Mix(ref hash, effect.UnadjustedDurationAdd);
                Mix(ref hash, effect.Scale);
                Mix(ref hash, effect.m_durationScale);
                Mix(ref hash, effect.m_religiousScale);
                Mix(ref hash, unchecked((int)effect.m_generalCounter));
                Mix(ref hash, effect.m_triggerCount);
                Mix(ref hash, effect.m_numRestCycles);
                Mix(ref hash, effect.m_stackingKey);
                Mix(ref hash, effect.BundleId);
                Mix(ref hash, (int)effect.Slot);
                Mix(ref hash, effect.m_damageToReapply);
                Mix(ref hash, effect.m_damageToAbsorb);
                Mix(ref hash, effect.IncreasePerTick);
                Mix(ref hash, effect.DurationAfterBreak);
                Mix(ref hash, effect.FriendlyRadius);
                MixReference(ref hash, AfflictionKeyword(effect));
                if (p.AffectsStat == StatusEffect.ModifiedStat.StealSpell) Mix(ref hash, effect.Spells != null ? effect.Spells.Count : -1);

                Mix(ref hash, ParamFlags(p));
                Mix(ref hash, (int)p.Apply);
                Mix(ref hash, (int)p.AffectsStat);
                Mix(ref hash, (int)p.DmgType);
                Mix(ref hash, p.Value);
                Mix(ref hash, p.ExtraValue);
                Mix(ref hash, p.TacticalMultiplier);
                Mix(ref hash, p.MergedValue);
                Mix(ref hash, p.Duration);
                Mix(ref hash, p.MaxRestCycles);
                Mix(ref hash, (int)p.IntervalRate);
                Mix(ref hash, (int)p.RaceType);
                Mix(ref hash, (int)p.ClassType);
                Mix(ref hash, (int)p.AttributeType);
                Mix(ref hash, (int)p.DefenseType);
                MixReference(ref hash, p.Tag);
                MixReference(ref hash, p.Keyword);
                MixReference(ref hash, p.Description);

                StatusEffectTrigger trigger = p.TriggerAdjustment ?? DefaultTrigger;
                Mix(ref hash, (int)trigger.Type);
                Mix(ref hash, trigger.TriggerValue);
                Mix(ref hash, trigger.ValueAdjustment);
                Mix(ref hash, trigger.DurationAdjustment);
                Mix(ref hash, trigger.RadiusAdjustment);
                Mix(ref hash, trigger.MaxTriggerCount);
                Mix(ref hash, (trigger.RemoveEffectAtMax ? 1 : 0) | (trigger.ResetTriggerOnEffectPulse ? 2 : 0) | (trigger.ResetTriggerOnEffectEnd ? 4 : 0));

                StatusEffectLevelScaling scaling = p.LevelScaling ?? DefaultScaling;
                Mix(ref hash, scaling.BaseLevel);
                Mix(ref hash, scaling.LevelIncrement);
                Mix(ref hash, scaling.MaxLevel);
                Mix(ref hash, scaling.ValueAdjustment);
                Mix(ref hash, scaling.ExtraValueAdjustment);
                Mix(ref hash, scaling.DurationAdjustment);
            }
            return hash;
        }

        private static void Mix(ref ulong hash, int value)
        {
            hash = (hash ^ unchecked((uint)value)) * 1099511628211UL;
        }

        private static void Mix(ref ulong hash, float value)
        {
            hash = (hash ^ WireBits.FromFloat(value)) * 1099511628211UL;
        }

        private static void MixId(ref ulong hash, Guid value)
        {
            var bits = new GuidBits { Value = value };
            hash = (hash ^ unchecked((ulong)bits.Low)) * 1099511628211UL;
            hash = (hash ^ unchecked((ulong)bits.High)) * 1099511628211UL;
        }

        private static void MixObject(ref ulong hash, UnityEngine.Object value)
        {
            Mix(ref hash, value != null ? value.GetInstanceID() : 0);
        }

        private static void MixReference(ref ulong hash, object value)
        {
            Mix(ref hash, value != null ? RuntimeHelpers.GetHashCode(value) : 0);
        }

        /// <summary>True when a serialised structure (count, byte count and effect bytes) equals the one in a held manifest body.</summary>
        private static bool SameStructure(byte[] held, byte[] candidate, int structureEnd)
        {
            if (held == null || structureEnd < TargetHeaderBytes || held.Length < structureEnd || candidate.Length < structureEnd) return false;
            for (int i = 1; i < StructureRevisionOffset; i++) if (held[i] != candidate[i]) return false;
            for (int i = StructureLengthOffset; i < structureEnd; i++) if (held[i] != candidate[i]) return false;
            return true;
        }

        private static bool IsBounded(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxMagnitude;
        }

        /// <summary>Appends one effect to the target body; a rejected effect leaves the body as it was.</summary>
        private static bool TryWriteEffect(BinaryWriter writer, MemoryStream stream, StatusEffect effect, out string error)
        {
            error = null;
            if (effect.Params.AffectsStat == StatusEffect.ModifiedStat.StealSpell && effect.Spells != null && effect.Spells.Count > 0)
            {
                error = "materialized stolen-spell lists have no presentation proxy";
                return false;
            }
            writer.Flush();
            long start = stream.Length;
            try
            {
                WriteEffect(writer, effect);
                writer.Flush();
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is NullReferenceException)
            {
                writer.Flush();
                stream.SetLength(start);
                error = ex.Message;
                return false;
            }
        }

        private static void WriteEffect(BinaryWriter writer, StatusEffect effect)
        {
            StatusEffectParams p = effect.Params;
            OriginKind originKind;
            string originName, originTag;
            int displayId, originOrdinal, parameterIndex, templateOrdinal;
            Guid originObject;
            TemplateKind templateKind;
            DescribeOrigin(effect, out originKind, out originName, out originTag, out displayId, out originOrdinal,
                out originObject, out templateKind, out templateOrdinal, out parameterIndex);

            writer.Write(unchecked((int)effect.EffectID));
            WriteGuid(writer, GetId(effect.Owner));
            WriteGuid(writer, GetId(effect.Source));
            WriteGuid(writer, GetId(effect.ExtraObject));
            writer.Write((byte)originKind);
            WriteString(writer, originName, 128);
            WriteString(writer, originTag, 128);
            writer.Write(displayId);
            writer.Write(originOrdinal);
            WriteGuid(writer, originObject);
            writer.Write((byte)templateKind);
            writer.Write(templateOrdinal);
            writer.Write(parameterIndex);
            writer.Write((int)effect.AbilityType);

            writer.Write(EffectFlags(effect));
            writer.Write(unchecked((int)effect.m_suspensionCount));
            WriteFloat(writer, BaseDuration(effect));
            WriteFloat(writer, effect.m_durationOverride);
            // The timers travel after the structure; an effect whose timers cannot be sent is still rejected here.
            RequireBounded(effect.m_timeActive);
            RequireBounded(effect.m_intervalTimer);
            WriteFloat(writer, TemporaryDuration(effect));
            WriteFloat(writer, effect.UnadjustedDurationAdd);
            WriteFloat(writer, effect.Scale);
            WriteFloat(writer, effect.m_durationScale);
            WriteFloat(writer, effect.m_religiousScale);
            writer.Write(unchecked((int)effect.m_generalCounter));
            writer.Write(effect.m_triggerCount);
            writer.Write(effect.m_numRestCycles);
            writer.Write(effect.m_stackingKey);
            writer.Write(effect.BundleId);
            writer.Write((int)effect.Slot);
            WriteFloat(writer, effect.m_damageToReapply);
            WriteFloat(writer, effect.m_damageToAbsorb);
            WriteFloat(writer, effect.IncreasePerTick);
            WriteFloat(writer, effect.DurationAfterBreak);
            WriteFloat(writer, effect.FriendlyRadius);
            WriteString(writer, AfflictionKeyword(effect), 128);

            writer.Write(ParamFlags(p));
            writer.Write((int)p.Apply);
            writer.Write((int)p.AffectsStat);
            writer.Write((int)p.DmgType);
            WriteFloat(writer, p.Value);
            WriteFloat(writer, p.ExtraValue);
            WriteFloat(writer, p.TacticalMultiplier);
            WriteFloat(writer, p.MergedValue);
            WriteFloat(writer, p.Duration);
            writer.Write(p.MaxRestCycles);
            writer.Write((int)p.IntervalRate);
            writer.Write((int)p.RaceType);
            writer.Write((int)p.ClassType);
            writer.Write((int)p.AttributeType);
            writer.Write((int)p.DefenseType);
            WriteString(writer, p.Tag, 128);
            WriteString(writer, p.Keyword, 128);
            WriteString(writer, p.Description, MaxStringLength);
            WriteString(writer, effect.BundleName, MaxStringLength);

            StatusEffectTrigger trigger = p.TriggerAdjustment ?? DefaultTrigger;
            writer.Write((int)trigger.Type);
            WriteFloat(writer, trigger.TriggerValue);
            WriteFloat(writer, trigger.ValueAdjustment);
            WriteFloat(writer, trigger.DurationAdjustment);
            WriteFloat(writer, trigger.RadiusAdjustment);
            writer.Write(trigger.MaxTriggerCount);
            writer.Write(trigger.RemoveEffectAtMax);
            writer.Write(trigger.ResetTriggerOnEffectPulse);
            writer.Write(trigger.ResetTriggerOnEffectEnd);

            StatusEffectLevelScaling scaling = p.LevelScaling ?? DefaultScaling;
            writer.Write(scaling.BaseLevel);
            writer.Write(scaling.LevelIncrement);
            writer.Write(scaling.MaxLevel);
            WriteFloat(writer, scaling.ValueAdjustment);
            WriteFloat(writer, scaling.ExtraValueAdjustment);
            WriteFloat(writer, scaling.DurationAdjustment);
        }

        private static int EffectFlags(StatusEffect effect)
        {
            int flags = 0;
            if (effect.Applied) flags |= 1;
            if (effect.m_deleteOnClear) flags |= 2;
            if (effect.m_effect_is_on_main_target) flags |= 4;
            if (effect.RemovingEffect) flags |= 8;
            if (effect.IsFromAura) flags |= 16;
            if (effect.m_forceStackable) flags |= 32;
            if (effect.m_needsDurationCalculated) flags |= 64;
            if (TacticalClear(effect)) flags |= 128;
            if (effect.TicksAfterBreak) flags |= 256;
            if (effect.IsSuppressed) flags |= 512;
            return flags;
        }

        private static int ParamFlags(StatusEffectParams p)
        {
            int flags = 0;
            if (p.Persistent) flags |= 1;
            if (p.IsHostile) flags |= 2;
            if (p.KeepOnDeath) flags |= 4;
            if (p.OneHitUse) flags |= 8;
            if (p.LastsUntilCombatEnds) flags |= 16;
            if (p.LastsUntilRest) flags |= 32;
            if (p.HideFromUi) flags |= 64;
            if (p.HideFromPortrait) flags |= 128;
            if (p.ChecksReligion) flags |= 256;
            if (p.DontHideFromLog) flags |= 512;
            if (p.IgnoreAbilityDeactivation) flags |= 1024;
            if (p.DoNotScalePlayer) flags |= 2048;
            if (p.IsCleanedUp) flags |= 4096;
            return flags;
        }

        private static void DescribeOrigin(StatusEffect effect, out OriginKind kind, out string name, out string tag,
            out int displayId, out int ordinal, out Guid objectId, out TemplateKind templateKind,
            out int templateOrdinal, out int parameterIndex)
        {
            kind = OriginKind.None;
            name = tag = "";
            displayId = -1;
            ordinal = templateOrdinal = 0;
            parameterIndex = -1;
            objectId = Guid.Empty;
            templateKind = TemplateKind.Inline;
            MonoBehaviour origin = effect.Origin;
            if (origin is Affliction)
            {
                var affliction = (Affliction)origin;
                kind = OriginKind.Affliction;
                name = NormalizedName(affliction);
                tag = affliction.Tag;
                displayId = affliction.DisplayName.StringID;
                FindArray(affliction.StatusEffects, effect.Params, out parameterIndex);
                if (parameterIndex >= 0) templateKind = TemplateKind.Origin;
            }
            else if (origin is Phrase)
            {
                var phrase = (Phrase)origin;
                kind = OriginKind.Phrase;
                name = NormalizedName(phrase);
                tag = phrase.Tag;
                displayId = phrase.DisplayName.StringID;
                ordinal = FindPhraseOrdinal(effect.Owner, phrase);
                FindArray(phrase.StatusEffects, effect.Params, out parameterIndex);
                if (parameterIndex >= 0) templateKind = TemplateKind.Origin;
                else
                {
                    templateOrdinal = FindPhraseData(phrase, effect.Params);
                    if (templateOrdinal >= 0)
                    {
                        templateKind = TemplateKind.PhraseData;
                        parameterIndex = templateOrdinal;
                    }
                }
            }
            else if (origin is Equippable)
            {
                var equipment = (Equippable)origin;
                kind = OriginKind.Equipment;
                name = NormalizedName(equipment);
                displayId = equipment.DisplayName.StringID;
                objectId = GetId(equipment.gameObject);
                ordinal = FindComponentOrdinal(effect.Owner, equipment);
                FindArray(equipment.StatusEffects, effect.Params, out parameterIndex);
                if (parameterIndex >= 0) templateKind = TemplateKind.Origin;
                else
                {
                    for (int i = 0; i < equipment.AttachedItemMods.Count && parameterIndex < 0; i++)
                    {
                        FindArray(equipment.AttachedItemMods[i].Mod.StatusEffectsOnEquip, effect.Params, out parameterIndex);
                        if (parameterIndex >= 0)
                        {
                            templateKind = TemplateKind.ItemMod;
                            templateOrdinal = i;
                        }
                    }
                }
            }
            else if (origin is GenericAbility)
            {
                var ability = (GenericAbility)origin;
                kind = OriginKind.Ability;
                name = NormalizedName(ability);
                displayId = ability.DisplayName.StringID;
                ordinal = FindComponentOrdinal(effect.Owner, ability);
                FindArray(ability.StatusEffects, effect.Params, out parameterIndex);
                if (parameterIndex >= 0) templateKind = TemplateKind.Origin;
                else if (ability.Attack != null)
                {
                    parameterIndex = ability.Attack.StatusEffects.IndexOf(effect.Params);
                    if (parameterIndex >= 0) templateKind = TemplateKind.Attack;
                }
                if (parameterIndex < 0)
                {
                    for (int i = 0; i < ability.AbilityMods.Count && parameterIndex < 0; i++)
                    {
                        FindArray(ability.AbilityMods[i].StatusEffects, effect.Params, out parameterIndex);
                        if (parameterIndex >= 0)
                        {
                            templateKind = TemplateKind.AbilityMod;
                            templateOrdinal = i;
                        }
                    }
                }
            }
        }

        private void SendSnapshots(List<Outgoing> snapshots, bool full)
        {
            byte[] valuesBuffer = _valuesStream.GetBuffer();
            int chunkCount = 0;
            for (int start = 0; start < snapshots.Count; start = ChunkEnd(snapshots, start)) chunkCount++;
            if (chunkCount == 0)
            {
                if (!full) return;
                chunkCount = 1;
            }

            int next = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                byte mode = FullDelta;
                if (full)
                {
                    if (chunkCount == 1) mode = FullSingle;
                    else if (i == 0) mode = FullBegin;
                    else if (i == chunkCount - 1) mode = FullEnd;
                    else mode = FullContinuation;
                }
                int end = next < snapshots.Count ? ChunkEnd(snapshots, next) : next;
                MemoryStream stream = _messageStream;
                BinaryWriter writer = _messageWriter;
                stream.SetLength(0);
                writer.Write((byte)MessageType.StatusEffects);
                writer.Write(_epoch);
                writer.Write(_revision);
                writer.Write(mode);
                writer.Write(end - next);
                for (int j = next; j < end; j++)
                {
                    Outgoing body = snapshots[j];
                    WriteGuid(writer, body.Id);
                    writer.Write(body.Length);
                    writer.Write(body.Body ?? valuesBuffer, body.Offset, body.Length);
                }
                writer.Flush();
                byte[] message = stream.ToArray();
                if (message.Length > TcpTransport.MaxMessageSize) throw new InvalidOperationException("status effect message exceeds transport bound");
                _session.SendToGuests(message);
                _revision = unchecked(_revision + 1);
                next = end;
            }
        }

        /// <summary>Returns the exclusive end of the message chunk that starts at <paramref name="start"/>.</summary>
        private static int ChunkEnd(List<Outgoing> snapshots, int start)
        {
            int size = 14;
            int end = start;
            while (end < snapshots.Count)
            {
                int recordSize = 20 + snapshots[end].Length;
                if (end > start && (end - start >= MaxTargetsPerMessage || size + recordSize > MaxMessageBytes - 14)) break;
                size += recordSize;
                end++;
            }
            return end;
        }

        // ---- guest parsing and reconciliation ----

        private sealed class EffectRecord
        {
            public uint Id;
            public Guid Owner, Source, ExtraObject, OriginObject;
            public OriginKind OriginKind;
            public string OriginName, OriginTag;
            public int DisplayId, OriginOrdinal, TemplateOrdinal, ParameterIndex, AbilityType;
            public TemplateKind TemplateKind;
            public int Flags, SuspensionCount;
            public float Duration, DurationOverride, TimeActive, TemporaryDuration, UnadjustedDurationAdd;
            public float Scale, DurationScale, ReligiousScale, IntervalTimer;
            public uint IntervalCount, GeneralCounter;
            public int TriggerCount, RestCycles, StackingKey, BundleId, Slot;
            public float DamageToReapply, DamageToAbsorb, IncreasePerTick, DurationAfterBreak, FriendlyRadius;
            public string AfflictionKeyword;
            public int ParamFlags, Apply, Stat, DamageType;
            public float Value, ExtraValue, TacticalMultiplier, MergedValue, ParamDuration;
            public int MaxRestCycles, IntervalRate, Race, Class, Attribute, Defense;
            public string Tag, Keyword, Description, BundleName;
            public int TriggerType, MaxTriggerCount;
            public float TriggerValue, TriggerValueAdjustment, TriggerDurationAdjustment, TriggerRadiusAdjustment;
            public bool TriggerRemoveAtMax, TriggerResetOnPulse, TriggerResetOnEnd;
            public int ScalingBase, ScalingIncrement, ScalingMax;
            public float ScalingValue, ScalingExtraValue, ScalingDuration;
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.StatusEffects || !_session.IsGuest) return;
            long started = PerfStats.Begin();
            try
            {
                HandleSnapshot(reader);
            }
            finally
            {
                PerfStats.End("status.apply", started);
            }
        }

        private void HandleSnapshot(MessageReader reader)
        {
            int epoch = reader.Int();
            int revision = reader.Int();
            byte full = reader.Byte();
            if (full > FullEnd) throw new FormatException("status full mode " + full);
            int targetCount = reader.Int();
            if (targetCount < 0 || targetCount > MaxTargetsPerMessage) throw new FormatException("status target count " + targetCount);

            var parsed = new Dictionary<Guid, Incoming>(targetCount);
            for (int i = 0; i < targetCount; i++)
            {
                Guid targetId = ReadGuid(reader);
                if (targetId == Guid.Empty || parsed.ContainsKey(targetId)) throw new FormatException("duplicate or empty status target");
                byte[] raw = reader.Bytes();
                var incoming = new Incoming { Raw = raw };
                GuestTarget existing;
                _guestTargets.TryGetValue(targetId, out existing);
                if (raw.Length > 0 && raw[0] == ValuesBodyTag)
                {
                    if (full != FullDelta) throw new FormatException("status values outside a delta");
                    if (raw.Length < ValuesHeaderBytes || raw.Length > ValuesHeaderBytes + MaxEffectsPerTarget * MaxValuesEntryBytes)
                        throw new FormatException("status values size " + raw.Length);
                    incoming.Values = true;
                    ReadValues(raw, existing, false);
                }
                else
                {
                    if (raw.Length < TargetHeaderBytes || raw.Length > MaxTargetBytes) throw new FormatException("status target size " + raw.Length);
                    // A manifest that is byte-identical to the one this target already holds was
                    // validated when it first arrived; it is neither parsed nor allocated again.
                    // One with the same structure has only its timer values read.
                    if (existing != null && existing.Raw != null && BytesEqual(existing.Raw, raw)) incoming.Raw = existing.Raw;
                    else if (existing != null && HoldsStructure(existing, raw)) ReadTimers(raw, existing, false);
                    else incoming.Records = ParseTarget(raw);
                }
                parsed.Add(targetId, incoming);
            }
            reader.Finish();

            bool startsFull = full == FullSingle || full == FullBegin;
            if (_guestEpoch == int.MinValue || epoch != _guestEpoch)
            {
                ResetGuest();
                ScrubAllEffects();
                _guestEpoch = epoch;
                _guestRevision = revision;
            }
            else if (startsFull)
            {
                _guestRevision = revision;
                _fullStaging = null;
            }
            else
            {
                if (revision != unchecked(_guestRevision + 1)) return;
                _guestRevision = revision;
            }

            switch (full)
            {
                case FullDelta:
                    _fullStaging = null;
                    foreach (var pair in parsed) MergeTarget(pair.Key, pair.Value);
                    break;
                case FullSingle:
                    MergeFullManifest(parsed);
                    break;
                case FullBegin:
                    _fullStaging = parsed;
                    break;
                case FullContinuation:
                    if (_fullStaging == null) return;
                    foreach (var pair in parsed)
                        if (!_fullStaging.ContainsKey(pair.Key)) _fullStaging.Add(pair.Key, pair.Value);
                        else throw new FormatException("status target repeated across full chunks");
                    break;
                case FullEnd:
                    if (_fullStaging == null) return;
                    foreach (var pair in parsed)
                        if (!_fullStaging.ContainsKey(pair.Key)) _fullStaging.Add(pair.Key, pair.Value);
                        else throw new FormatException("status target repeated across full chunks");
                    var completed = _fullStaging;
                    _fullStaging = null;
                    MergeFullManifest(completed);
                    break;
            }
            _nextGuestRetry = 0f;
        }

        /// <summary>
        /// Parses and validates one manifest body: tag, effect count, structure revision, structure
        /// byte count, the effects' structure, then one timer triple per effect, nothing after.
        /// </summary>
        private List<EffectRecord> ParseTarget(byte[] raw)
        {
            var reader = new MessageReader(raw);
            if (reader.Type != MessageType.StatusEffects) throw new FormatException("status target tag " + (int)reader.Type);
            int effectCount = reader.Int();
            if (effectCount < 0 || effectCount > MaxEffectsPerTarget) throw new FormatException("status effect count " + effectCount);
            reader.Int();
            int structureLength = reader.Int();
            int timerBytes = effectCount * TimerBytesPerEffect;
            if (structureLength < 0 || structureLength != raw.Length - TargetHeaderBytes - timerBytes)
                throw new FormatException("status structure length " + structureLength);
            var records = new List<EffectRecord>(effectCount);
            _scratchIds.Clear();
            for (int j = 0; j < effectCount; j++)
            {
                EffectRecord record = ReadEffect(reader);
                if (!_scratchIds.Add(record.Id)) throw new FormatException("duplicate host EffectID " + record.Id);
                records.Add(record);
            }
            if (reader.Remaining != timerBytes) throw new FormatException("status structure does not end at its length");
            for (int j = 0; j < effectCount; j++)
            {
                EffectRecord record = records[j];
                record.TimeActive = ReadFloat(reader);
                record.IntervalTimer = ReadFloat(reader);
                record.IntervalCount = unchecked((uint)reader.Int());
                if (record.IntervalCount > MaxCounter) throw new FormatException("status interval count outside its bound");
            }
            reader.Finish();
            return records;
        }

        /// <summary>True when a received manifest body has exactly the structure of the manifest the target holds.</summary>
        private static bool HoldsStructure(GuestTarget target, byte[] raw)
        {
            byte[] held = target.Raw;
            return held != null && raw.Length == held.Length && raw[0] == held[0]
                && SameStructure(held, raw, held.Length - target.Records.Count * TimerBytesPerEffect);
        }

        /// <summary>
        /// Reads the timer triples of a manifest body whose structure the target already holds,
        /// validating them and, when <paramref name="apply"/> is set, writing them to the records and
        /// to the effects that are already mirrored.
        /// </summary>
        private static void ReadTimers(byte[] raw, GuestTarget target, bool apply)
        {
            List<EffectRecord> records = target.Records;
            int at = raw.Length - records.Count * TimerBytesPerEffect;
            for (int i = 0; i < records.Count; i++, at += TimerBytesPerEffect)
            {
                float timeActive = WireBits.ToFloat(unchecked((uint)WireInt(raw, at)));
                float intervalTimer = WireBits.ToFloat(unchecked((uint)WireInt(raw, at + 4)));
                uint intervalCount = unchecked((uint)WireInt(raw, at + 8));
                if (!IsBounded(timeActive) || !IsBounded(intervalTimer) || intervalCount > MaxCounter)
                    throw new FormatException("status timer outside its bound");
                if (!apply) continue;
                EffectRecord record = records[i];
                record.TimeActive = timeActive;
                record.IntervalTimer = intervalTimer;
                record.IntervalCount = intervalCount;
                MirrorEffect mirror;
                if (!target.Effects.TryGetValue(record.Id, out mirror) || mirror.Effect == null) continue;
                mirror.Effect.m_timeActive = timeActive;
                mirror.Effect.m_intervalTimer = intervalTimer;
                mirror.Effect.m_intervalCount = intervalCount;
            }
        }

        /// <summary>
        /// Reads a values-only body: tag, structure revision, entry count, then per entry the effect's
        /// index in the manifest, a mask and the masked timer values. Always validates the whole body.
        /// With <paramref name="apply"/> set, and only when the revision is the one of the manifest the
        /// target holds, writes the values to the records, the held manifest and the mirrored effects;
        /// identities, origins and the live list are not touched. Returns whether the revision matched.
        /// </summary>
        private static bool ReadValues(byte[] raw, GuestTarget target, bool apply)
        {
            var reader = new MessageReader(raw);
            int revision = reader.Int();
            int entries = reader.Int();
            if (entries < 1 || entries > MaxEffectsPerTarget) throw new FormatException("status values count " + entries);
            byte[] held = target != null ? target.Raw : null;
            bool matches = held != null && WireInt(held, StructureRevisionOffset) == revision;
            int limit = matches ? target.Records.Count : MaxEffectsPerTarget;
            int timers = matches ? held.Length - limit * TimerBytesPerEffect : 0;
            int previous = -1;
            for (int i = 0; i < entries; i++)
            {
                int index = reader.Byte();
                int mask = reader.Byte();
                if (index <= previous || index >= limit || mask < 1 || mask > 7) throw new FormatException("status values entry " + index + "/" + mask);
                previous = index;
                float timeActive = (mask & 1) != 0 ? ReadFloat(reader) : 0f;
                float intervalTimer = (mask & 2) != 0 ? ReadFloat(reader) : 0f;
                uint intervalCount = (mask & 4) != 0 ? unchecked((uint)reader.Int()) : 0u;
                if (intervalCount > MaxCounter) throw new FormatException("status interval count outside its bound");
                if (!apply || !matches) continue;

                EffectRecord record = target.Records[index];
                MirrorEffect mirror;
                StatusEffect effect = target.Effects.TryGetValue(record.Id, out mirror) ? mirror.Effect : null;
                int at = timers + index * TimerBytesPerEffect;
                if ((mask & 1) != 0)
                {
                    record.TimeActive = timeActive;
                    PutWireInt(held, at, unchecked((int)WireBits.FromFloat(timeActive)));
                    if (effect != null) effect.m_timeActive = timeActive;
                }
                if ((mask & 2) != 0)
                {
                    record.IntervalTimer = intervalTimer;
                    PutWireInt(held, at + 4, unchecked((int)WireBits.FromFloat(intervalTimer)));
                    if (effect != null) effect.m_intervalTimer = intervalTimer;
                }
                if ((mask & 4) != 0)
                {
                    record.IntervalCount = intervalCount;
                    PutWireInt(held, at + 8, unchecked((int)intervalCount));
                    if (effect != null) effect.m_intervalCount = intervalCount;
                }
            }
            reader.Finish();
            return matches;
        }

        private static int WireInt(byte[] data, int at)
        {
            return data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24;
        }

        private static void PutWireInt(byte[] data, int at, int value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
            data[at + 2] = (byte)(value >> 16);
            data[at + 3] = (byte)(value >> 24);
        }

        private static EffectRecord ReadEffect(MessageReader reader)
        {
            var r = new EffectRecord
            {
                Id = unchecked((uint)reader.Int()),
                Owner = ReadGuid(reader),
                Source = ReadGuid(reader),
                ExtraObject = ReadGuid(reader),
                OriginKind = (OriginKind)reader.Byte(),
                OriginName = ReadString(reader, 128),
                OriginTag = ReadString(reader, 128),
                DisplayId = reader.Int(),
                OriginOrdinal = reader.Int(),
                OriginObject = ReadGuid(reader),
                TemplateKind = (TemplateKind)reader.Byte(),
                TemplateOrdinal = reader.Int(),
                ParameterIndex = reader.Int(),
                AbilityType = reader.Int(),
                Flags = reader.Int(),
                SuspensionCount = reader.Int(),
                Duration = ReadFloat(reader),
                DurationOverride = ReadFloat(reader),
                TemporaryDuration = ReadFloat(reader),
                UnadjustedDurationAdd = ReadFloat(reader),
                Scale = ReadFloat(reader),
                DurationScale = ReadFloat(reader),
                ReligiousScale = ReadFloat(reader),
                GeneralCounter = unchecked((uint)reader.Int()),
                TriggerCount = reader.Int(),
                RestCycles = reader.Int(),
                StackingKey = reader.Int(),
                BundleId = reader.Int(),
                Slot = reader.Int(),
                DamageToReapply = ReadFloat(reader),
                DamageToAbsorb = ReadFloat(reader),
                IncreasePerTick = ReadFloat(reader),
                DurationAfterBreak = ReadFloat(reader),
                FriendlyRadius = ReadFloat(reader),
                AfflictionKeyword = ReadString(reader, 128),
                ParamFlags = reader.Int(),
                Apply = reader.Int(),
                Stat = reader.Int(),
                DamageType = reader.Int(),
                Value = ReadFloat(reader),
                ExtraValue = ReadFloat(reader),
                TacticalMultiplier = ReadFloat(reader),
                MergedValue = ReadFloat(reader),
                ParamDuration = ReadFloat(reader),
                MaxRestCycles = reader.Int(),
                IntervalRate = reader.Int(),
                Race = reader.Int(),
                Class = reader.Int(),
                Attribute = reader.Int(),
                Defense = reader.Int(),
                Tag = ReadString(reader, 128),
                Keyword = ReadString(reader, 128),
                Description = ReadString(reader, MaxStringLength),
                BundleName = ReadString(reader, MaxStringLength),
                TriggerType = reader.Int(),
                TriggerValue = ReadFloat(reader),
                TriggerValueAdjustment = ReadFloat(reader),
                TriggerDurationAdjustment = ReadFloat(reader),
                TriggerRadiusAdjustment = ReadFloat(reader),
                MaxTriggerCount = reader.Int(),
                TriggerRemoveAtMax = ReadBool(reader),
                TriggerResetOnPulse = ReadBool(reader),
                TriggerResetOnEnd = ReadBool(reader),
                ScalingBase = reader.Int(),
                ScalingIncrement = reader.Int(),
                ScalingMax = reader.Int(),
                ScalingValue = ReadFloat(reader),
                ScalingExtraValue = ReadFloat(reader),
                ScalingDuration = ReadFloat(reader),
            };
            ValidateRecord(r);
            return r;
        }

        private static void ValidateRecord(EffectRecord r)
        {
            if (r.OriginKind > OriginKind.Phrase || r.TemplateKind > TemplateKind.AbilityMod)
                throw new FormatException("unknown status origin or template");
            if (r.Flags < 0 || r.Flags > 1023 || r.ParamFlags < 0 || r.ParamFlags > 8191 || r.DisplayId < -1 || r.DisplayId > 10000000)
                throw new FormatException("status flags or display ID outside their bound");
            if (r.OriginKind == OriginKind.Ability && r.OriginObject != Guid.Empty)
                throw new FormatException("ability instances must not use GUIDs");
            if (r.OriginOrdinal < 0 || r.OriginOrdinal > 4096 || r.TemplateOrdinal < 0 || r.TemplateOrdinal > 4096
                || r.ParameterIndex < -1 || r.ParameterIndex > 4096 || r.SuspensionCount < 0 || r.SuspensionCount > 4096
                || r.GeneralCounter > MaxCounter || r.TriggerCount < 0 || r.TriggerCount > MaxCounter
                || r.RestCycles < 0 || r.RestCycles > MaxCounter || r.MaxRestCycles < 0 || r.MaxRestCycles > MaxCounter
                || r.MaxTriggerCount < 0 || r.MaxTriggerCount > MaxCounter || r.ScalingBase < 0 || r.ScalingBase > MaxCounter
                || r.ScalingIncrement < 0 || r.ScalingIncrement > MaxCounter || r.ScalingMax < 0)
                throw new FormatException("status ordinal or counter outside its bound");
            if (!AbilityTypes.Contains(r.AbilityType)
                || !ApplyTypes.Contains(r.Apply)
                || !ModifiedStats.Contains(r.Stat)
                || !DamageTypes.Contains(r.DamageType)
                || !IntervalRates.Contains(r.IntervalRate)
                || !Races.Contains(r.Race)
                || !Classes.Contains(r.Class)
                || !AttributeTypes.Contains(r.Attribute)
                || !DefenseTypes.Contains(r.Defense)
                || !EquipmentSlots.Contains(r.Slot)
                || !TriggerTypes.Contains(r.TriggerType))
                throw new FormatException("invalid status enumeration");
            if ((r.TemplateKind == TemplateKind.Inline) != (r.ParameterIndex < 0))
                throw new FormatException("inconsistent status parameter template");
        }

        private void MergeTarget(Guid id, Incoming incoming)
        {
            GuestTarget existing;
            _guestTargets.TryGetValue(id, out existing);
            if (incoming.Values)
            {
                // Values for an unknown target or another structure are dropped; the next manifest
                // for the target (a structural change or the periodic refresh) carries every value.
                if (existing != null && ReadValues(incoming.Raw, existing, true)) PerfStats.Count("status.values");
                else PerfStats.Count("status.stale");
                return;
            }
            if (existing != null)
            {
                if (incoming.Records == null && existing.Raw != null)
                {
                    bool identical = BytesEqual(existing.Raw, incoming.Raw);
                    if (identical || HoldsStructure(existing, incoming.Raw))
                    {
                        // Same structure again (a periodic full refresh): only the timer values can
                        // differ, and they are written in place. A pending target retries now, as it
                        // always did; an applied one only has its live list checked.
                        if (!identical)
                        {
                            ReadTimers(incoming.Raw, existing, true);
                            existing.Raw = incoming.Raw;
                        }
                        if (existing.Dirty) existing.NextRetry = 0f;
                        else existing.Verify = true;
                        return;
                    }
                }
                existing.Records = incoming.Records ?? ParseTarget(incoming.Raw);
                existing.Raw = incoming.Raw;
                existing.Dirty = true;
                existing.Verify = false;
                existing.Failures = 0;
                existing.NextRetry = 0f;
            }
            else
            {
                _guestTargets.Add(id, new GuestTarget
                {
                    Records = incoming.Records ?? ParseTarget(incoming.Raw),
                    Raw = incoming.Raw,
                });
            }
        }

        private void MergeFullManifest(Dictionary<Guid, Incoming> manifest)
        {
            var missing = new List<Guid>();
            foreach (var pair in _guestTargets)
                if (!manifest.ContainsKey(pair.Key)) missing.Add(pair.Key);
            for (int i = 0; i < missing.Count; i++)
            {
                RemoveTargetEffects(_guestTargets[missing[i]]);
                _guestTargets.Remove(missing[i]);
            }
            foreach (var pair in manifest) MergeTarget(pair.Key, pair.Value);
        }

        /// <summary>
        /// Cheap check for an applied target whose manifest arrived unchanged: the same object still
        /// resolves, its live list is exactly the mirrored effects in host order, and every owner,
        /// source, extra object and origin the manifest names is still alive. Anything else falls
        /// back to the full reconciliation.
        /// </summary>
        private bool StillApplied(Guid id, GuestTarget target)
        {
            if (!target.Initialized || target.AppliedStats == null || target.AppliedObject == null) return false;
            if (Resolve(id) != target.AppliedObject) return false;
            List<StatusEffect> list = EffectList(target.AppliedStats);
            List<EffectRecord> records = target.Records;
            if (list.Count != records.Count || target.Effects.Count != records.Count) return false;
            for (int i = 0; i < records.Count; i++)
            {
                EffectRecord record = records[i];
                MirrorEffect mirror;
                if (!target.Effects.TryGetValue(record.Id, out mirror) || mirror.Effect == null) return false;
                StatusEffect effect = mirror.Effect;
                if (!ReferenceEquals(list[i], effect) || effect.EffectID != record.Id) return false;
                if (record.Owner != Guid.Empty && EffectOwner(effect) == null) return false;
                if (record.Source != Guid.Empty && effect.Source == null) return false;
                if (record.ExtraObject != Guid.Empty && effect.ExtraObject == null) return false;
                if (record.OriginKind != OriginKind.None && effect.Origin == null) return false;
            }
            UpdateTracker(target.AppliedStats) = true;
            return true;
        }

        /// <summary>Returns true when the target stays dirty because one of its effects could not be presented.</summary>
        private bool ReconcileTarget(Guid id, GuestTarget target)
        {
            target.Dirty = false;
            target.Verify = false;
            target.AppliedObject = null;
            target.AppliedStats = null;
            GameObject targetObject = Resolve(id);
            if (targetObject == null) { target.Dirty = true; return false; }
            CharacterStats stats = targetObject.GetComponent<CharacterStats>();
            if (stats == null) { target.Dirty = true; return false; }

            using (new ApplyScope())
            {
                List<StatusEffect> list = EffectList(stats);
                if (!target.Initialized)
                {
                    var mirrored = new HashSet<StatusEffect>();
                    foreach (MirrorEffect mirror in target.Effects.Values)
                        if (mirror.Effect != null) mirrored.Add(mirror.Effect);
                    for (int i = list.Count - 1; i >= 0; i--)
                        if (!mirrored.Contains(list[i]))
                        {
                            StatusEffect removed = list[i];
                            list.RemoveAt(i);
                            NotifyRemoved(stats, removed);
                        }
                    target.Initialized = true;
                }

                HashSet<uint> present = _scratchIds;
                present.Clear();
                for (int i = 0; i < target.Records.Count; i++) present.Add(target.Records[i].Id);
                List<uint> absent = _scratchAbsent;
                absent.Clear();
                foreach (uint idKey in target.Effects.Keys) if (!present.Contains(idKey)) absent.Add(idKey);
                for (int i = 0; i < absent.Count; i++)
                {
                    MirrorEffect removed = target.Effects[absent[i]];
                    if (removed.Effect != null)
                    {
                        list.Remove(removed.Effect);
                        NotifyRemoved(stats, removed.Effect);
                    }
                    target.Effects.Remove(absent[i]);
                }

                for (int i = 0; i < target.Records.Count; i++)
                {
                    EffectRecord record = target.Records[i];
                    MirrorEffect mirror;
                    if (!target.Effects.TryGetValue(record.Id, out mirror))
                        target.Effects.Add(record.Id, mirror = new MirrorEffect());
                    if (mirror.Effect == null)
                    {
                        StatusEffect created;
                        string error;
                        if (!TryCreateEffect(record, targetObject, stats, out created, out error))
                        {
                            if (!mirror.WarningLogged)
                            {
                                mirror.WarningLogged = true;
                                WarnGuestOnce(id + ":" + record.Id + ":" + error,
                                    "Cannot present host status effect " + record.Id + " on " + stats.name + ": " + error);
                            }
                            target.Dirty = true;
                            continue;
                        }
                        mirror.Effect = created;
                        mirror.WarningLogged = false;
                    }
                    else if (!UpdateEffectIdentity(mirror.Effect, record, out string identityError))
                    {
                        target.Dirty = true;
                        if (!mirror.WarningLogged)
                        {
                            mirror.WarningLogged = true;
                            WarnGuestOnce(id + ":" + record.Id + ":" + identityError,
                                "Host status origin is not available yet for " + record.Id + " on " + stats.name + ": " + identityError);
                        }
                        continue;
                    }
                    mirror.WarningLogged = false;
                    UpdateEffectValues(mirror.Effect, record);

                    if (i < list.Count && ReferenceEquals(list[i], mirror.Effect)) continue;
                    int desiredIndex = Math.Min(i, list.Count);
                    int currentIndex = list.IndexOf(mirror.Effect);
                    if (currentIndex != desiredIndex)
                    {
                        bool isNew = currentIndex < 0;
                        if (!isNew) list.RemoveAt(currentIndex);
                        desiredIndex = Math.Min(i, list.Count);
                        list.Insert(desiredIndex, mirror.Effect);
                        if (isNew) NotifyAdded(stats, mirror.Effect);
                    }
                }
                UpdateTracker(stats) = true;
            }
            target.AppliedObject = targetObject;
            target.AppliedStats = stats;
            return target.Dirty;
        }

        private bool TryCreateEffect(EffectRecord record, GameObject target, CharacterStats stats, out StatusEffect effect, out string error)
        {
            effect = null;
            OriginReferences origins;
            if (!ResolveOrigins(record, out origins, out error)) return false;
            StatusEffectParams parameters;
            if (!ResolveParameters(record, origins, out parameters, out error)) return false;

            effect = new StatusEffect();
            effect.Params = parameters;
            EffectTarget(effect) = target;
            EffectTargetStats(effect) = stats;
            SetOrigins(effect, origins);
            UpdateEffectValues(effect, record);
            return true;
        }

        private sealed class OriginReferences
        {
            public GameObject Owner, Source, ExtraObject;
            public Affliction Affliction;
            public GenericAbility Ability;
            public Equippable Equipment;
            public Phrase Phrase;
            public MonoBehaviour Origin => (MonoBehaviour)Affliction ?? (MonoBehaviour)Ability ?? (MonoBehaviour)Equipment ?? Phrase;
        }

        private bool UpdateEffectIdentity(StatusEffect effect, EffectRecord record, out string error)
        {
            OriginReferences origins;
            if (!ResolveOrigins(record, out origins, out error)) return false;
            SetOrigins(effect, origins);
            return true;
        }

        private static void SetOrigins(StatusEffect effect, OriginReferences origins)
        {
            EffectOwner(effect) = origins.Owner;
            EffectOwnerStats(effect) = origins.Owner != null ? origins.Owner.GetComponent<CharacterStats>() : null;
            effect.Source = origins.Source;
            effect.ExtraObject = origins.ExtraObject;
            effect.AfflictionOrigin = origins.Affliction;
            effect.AbilityOrigin = origins.Ability;
            effect.EquipmentOrigin = origins.Equipment;
            effect.PhraseOrigin = origins.Phrase;
        }

        private bool ResolveOrigins(EffectRecord record, out OriginReferences origins, out string error)
        {
            // Consumed by the caller before the next effect is resolved, so one instance serves all.
            origins = _origins;
            origins.Owner = Resolve(record.Owner);
            origins.Source = Resolve(record.Source);
            origins.ExtraObject = Resolve(record.ExtraObject);
            origins.Affliction = null;
            origins.Ability = null;
            origins.Equipment = null;
            origins.Phrase = null;
            if (record.Owner != Guid.Empty && origins.Owner == null) { error = "owner entity is unresolved"; return false; }

            switch (record.OriginKind)
            {
                case OriginKind.None:
                    error = null;
                    return true;
                case OriginKind.Affliction:
                    origins.Affliction = FindAffliction(record);
                    break;
                case OriginKind.Ability:
                    origins.Ability = FindAbility(origins.Owner, record);
                    break;
                case OriginKind.Equipment:
                    GameObject equipmentObject = Resolve(record.OriginObject);
                    if (equipmentObject != null) origins.Equipment = equipmentObject.GetComponent<Equippable>();
                    if (origins.Equipment == null) origins.Equipment = FindComponent<Equippable>(origins.Owner, record);
                    break;
                case OriginKind.Phrase:
                    origins.Phrase = FindPhrase(origins.Owner, record);
                    break;
            }
            if (origins.Origin == null)
            {
                error = record.OriginKind + " origin '" + record.OriginName + "' (" + record.DisplayId + ") occurrence " + record.OriginOrdinal + " is unresolved";
                return false;
            }
            error = null;
            return true;
        }

        private static bool ResolveParameters(EffectRecord record, OriginReferences origins, out StatusEffectParams parameters, out string error)
        {
            StatusEffectParams source = null;
            Phrase.PhraseData phraseData = null;
            if (record.TemplateKind != TemplateKind.Inline)
            {
                if (record.ParameterIndex < 0) { parameters = null; error = "negative parameter index"; return false; }
                switch (record.TemplateKind)
                {
                    case TemplateKind.Origin:
                        if (origins.Affliction != null) source = At(origins.Affliction.StatusEffects, record.ParameterIndex);
                        else if (origins.Ability != null) source = At(origins.Ability.StatusEffects, record.ParameterIndex);
                        else if (origins.Equipment != null) source = At(origins.Equipment.StatusEffects, record.ParameterIndex);
                        else if (origins.Phrase != null) source = At(origins.Phrase.StatusEffects, record.ParameterIndex);
                        break;
                    case TemplateKind.Attack:
                        if (origins.Ability != null && origins.Ability.Attack != null)
                            source = origins.Ability.Attack.StatusEffects.Count > record.ParameterIndex ? origins.Ability.Attack.StatusEffects[record.ParameterIndex] : null;
                        break;
                    case TemplateKind.ItemMod:
                        if (origins.Equipment != null && record.TemplateOrdinal < origins.Equipment.AttachedItemMods.Count
                            && origins.Equipment.AttachedItemMods[record.TemplateOrdinal] != null
                            && origins.Equipment.AttachedItemMods[record.TemplateOrdinal].Mod != null)
                            source = At(origins.Equipment.AttachedItemMods[record.TemplateOrdinal].Mod.StatusEffectsOnEquip, record.ParameterIndex);
                        break;
                    case TemplateKind.AbilityMod:
                        if (origins.Ability != null && record.TemplateOrdinal < origins.Ability.AbilityMods.Count
                            && origins.Ability.AbilityMods[record.TemplateOrdinal] != null)
                            source = At(origins.Ability.AbilityMods[record.TemplateOrdinal].StatusEffects, record.ParameterIndex);
                        break;
                    case TemplateKind.PhraseData:
                        if (origins.Phrase != null && origins.Phrase.m_phraseData != null && record.ParameterIndex < origins.Phrase.m_phraseData.Length)
                            phraseData = origins.Phrase.m_phraseData[record.ParameterIndex];
                        break;
                }
                if (source == null && phraseData == null)
                {
                    parameters = null;
                    error = record.TemplateKind + " parameter " + record.ParameterIndex + " (ordinal " + record.TemplateOrdinal + ") is unresolved";
                    return false;
                }
                if (source != null && (int)source.AffectsStat != record.Stat)
                {
                    parameters = null;
                    error = "parameter template stat does not match the host recipe";
                    return false;
                }
            }

            StatusEffect.ModifiedStat stat = (StatusEffect.ModifiedStat)record.Stat;
            bool needsTemplate = StatusEffect.UsesTrapParam(stat) || StatusEffect.UsesEquippableParam(stat)
                || StatusEffect.UsesConsumableParam(stat) || StatusEffect.UsesAttackParam(stat)
                || StatusEffect.UsesAbilityParam(stat) || StatusEffect.UsesAfflictionParam(stat);
            bool abilityAttackTemplate = stat == StatusEffect.ModifiedStat.ApplyAttackEffects
                && origins.Ability != null && origins.Ability.Attack != null;
            if (source == null && phraseData == null && needsTemplate && !abilityAttackTemplate)
            {
                parameters = null;
                error = "inline recipe for " + stat + " lacks required prefab parameters";
                return false;
            }

            if (source != null) parameters = (StatusEffectParams)ShallowClone.Invoke(source, null);
            else if (phraseData != null) parameters = new StatusEffectParams(phraseData, record.ParamDuration);
            else parameters = new StatusEffectParams();
            if (abilityAttackTemplate) parameters.AttackPrefab = origins.Ability.Attack;
            if (parameters.TriggerAdjustment != null) parameters.TriggerAdjustment = (StatusEffectTrigger)ShallowClone.Invoke(parameters.TriggerAdjustment, null);
            else parameters.TriggerAdjustment = new StatusEffectTrigger();
            if (parameters.LevelScaling != null) parameters.LevelScaling = (StatusEffectLevelScaling)ShallowClone.Invoke(parameters.LevelScaling, null);
            else parameters.LevelScaling = new StatusEffectLevelScaling();
            parameters.OnStartVisualEffect = null;
            parameters.OnAppliedVisualEffect = null;
            parameters.OnStopVisualEffect = null;
            parameters.OnAuraVisualEffect = null;
            parameters.OnTriggerVisualEffect = null;
            error = null;
            return true;
        }

        private static void UpdateEffectValues(StatusEffect effect, EffectRecord r)
        {
            effect.EffectID = r.Id;
            effect.AbilityType = (GenericAbility.AbilityType)r.AbilityType;
            effect.Applied = (r.Flags & 1) != 0;
            effect.m_deleteOnClear = (r.Flags & 2) != 0;
            effect.m_effect_is_on_main_target = (r.Flags & 4) != 0;
            effect.RemovingEffect = (r.Flags & 8) != 0;
            effect.IsFromAura = (r.Flags & 16) != 0;
            effect.m_forceStackable = (r.Flags & 32) != 0;
            effect.m_needsDurationCalculated = (r.Flags & 64) != 0;
            TacticalClear(effect) = (r.Flags & 128) != 0;
            effect.TicksAfterBreak = (r.Flags & 256) != 0;
            effect.m_suppressed = (r.Flags & 512) != 0;
            effect.m_suspensionCount = unchecked((uint)r.SuspensionCount);
            BaseDuration(effect) = r.Duration;
            effect.m_durationOverride = r.DurationOverride;
            effect.m_timeActive = r.TimeActive;
            TemporaryDuration(effect) = r.TemporaryDuration;
            effect.UnadjustedDurationAdd = r.UnadjustedDurationAdd;
            effect.Scale = r.Scale;
            effect.m_durationScale = r.DurationScale;
            effect.m_religiousScale = r.ReligiousScale;
            effect.m_intervalTimer = r.IntervalTimer;
            effect.m_intervalCount = r.IntervalCount;
            effect.m_generalCounter = r.GeneralCounter;
            effect.m_triggerCount = r.TriggerCount;
            effect.m_numRestCycles = r.RestCycles;
            effect.m_stackingKey = r.StackingKey;
            effect.BundleId = r.BundleId;
            effect.Slot = (Equippable.EquipmentSlot)r.Slot;
            effect.m_damageToReapply = r.DamageToReapply;
            effect.m_damageToAbsorb = r.DamageToAbsorb;
            effect.IncreasePerTick = r.IncreasePerTick;
            effect.DurationAfterBreak = r.DurationAfterBreak;
            effect.FriendlyRadius = r.FriendlyRadius;
            AfflictionKeyword(effect) = r.AfflictionKeyword;

            StatusEffectParams p = effect.Params;
            p.Persistent = (r.ParamFlags & 1) != 0;
            p.IsHostile = (r.ParamFlags & 2) != 0;
            p.KeepOnDeath = (r.ParamFlags & 4) != 0;
            p.OneHitUse = (r.ParamFlags & 8) != 0;
            p.LastsUntilCombatEnds = (r.ParamFlags & 16) != 0;
            p.LastsUntilRest = (r.ParamFlags & 32) != 0;
            p.HideFromUi = (r.ParamFlags & 64) != 0;
            p.HideFromPortrait = (r.ParamFlags & 128) != 0;
            p.ChecksReligion = (r.ParamFlags & 256) != 0;
            p.DontHideFromLog = (r.ParamFlags & 512) != 0;
            p.IgnoreAbilityDeactivation = (r.ParamFlags & 1024) != 0;
            p.DoNotScalePlayer = (r.ParamFlags & 2048) != 0;
            if (SetCleanedUp != null) SetCleanedUp(p, (r.ParamFlags & 4096) != 0);
            else CleanedUpProperty.SetValue(p, (r.ParamFlags & 4096) != 0, null);
            if (SetMergedValue != null) SetMergedValue(p, r.MergedValue);
            else MergedValueProperty.SetValue(p, r.MergedValue, null);
            p.Apply = (StatusEffect.ApplyType)r.Apply;
            p.AffectsStat = (StatusEffect.ModifiedStat)r.Stat;
            p.DmgType = (DamagePacket.DamageType)r.DamageType;
            p.Value = r.Value;
            p.ExtraValue = r.ExtraValue;
            p.TacticalMultiplier = r.TacticalMultiplier;
            p.Duration = r.ParamDuration;
            p.MaxRestCycles = r.MaxRestCycles;
            p.IntervalRate = (StatusEffectParams.IntervalRateType)r.IntervalRate;
            p.RaceType = (CharacterStats.Race)r.Race;
            p.ClassType = (CharacterStats.Class)r.Class;
            p.AttributeType = (CharacterStats.AttributeScoreType)r.Attribute;
            p.DefenseType = (CharacterStats.DefenseType)r.Defense;
            p.Tag = r.Tag;
            p.Keyword = r.Keyword;
            p.Description = r.Description;

            p.TriggerAdjustment.Type = (StatusEffectTrigger.TriggerType)r.TriggerType;
            p.TriggerAdjustment.TriggerValue = r.TriggerValue;
            p.TriggerAdjustment.ValueAdjustment = r.TriggerValueAdjustment;
            p.TriggerAdjustment.DurationAdjustment = r.TriggerDurationAdjustment;
            p.TriggerAdjustment.RadiusAdjustment = r.TriggerRadiusAdjustment;
            p.TriggerAdjustment.MaxTriggerCount = r.MaxTriggerCount;
            p.TriggerAdjustment.RemoveEffectAtMax = r.TriggerRemoveAtMax;
            p.TriggerAdjustment.ResetTriggerOnEffectPulse = r.TriggerResetOnPulse;
            p.TriggerAdjustment.ResetTriggerOnEffectEnd = r.TriggerResetOnEnd;
            p.LevelScaling.BaseLevel = r.ScalingBase;
            p.LevelScaling.LevelIncrement = r.ScalingIncrement;
            p.LevelScaling.MaxLevel = r.ScalingMax;
            p.LevelScaling.ValueAdjustment = r.ScalingValue;
            p.LevelScaling.ExtraValueAdjustment = r.ScalingExtraValue;
            p.LevelScaling.DurationAdjustment = r.ScalingDuration;
        }

        private void RemoveTargetEffects(GuestTarget target)
        {
            foreach (MirrorEffect mirror in target.Effects.Values)
            {
                if (mirror.Effect == null) continue;
                GameObject go = mirror.Effect.Target;
                CharacterStats stats = go != null ? go.GetComponent<CharacterStats>() : null;
                if (stats == null) continue;
                using (new ApplyScope())
                {
                    EffectList(stats).Remove(mirror.Effect);
                    UpdateTracker(stats) = true;
                    NotifyRemoved(stats, mirror.Effect);
                }
            }
            target.Effects.Clear();
        }

        private void ClearMirroredEffects()
        {
            foreach (GuestTarget target in _guestTargets.Values) RemoveTargetEffects(target);
        }

        private void ResetGuest()
        {
            ClearMirroredEffects();
            _guestTargets.Clear();
            _fullStaging = null;
            _guestWarnings.Clear();
            _guestEpoch = int.MinValue;
            _guestRevision = 0;
            _nextGuestRetry = 0f;
        }

        private static void ScrubAllEffects()
        {
            CharacterStats[] all = UnityEngine.Object.FindObjectsOfType<CharacterStats>();
            for (int i = 0; i < all.Length; i++)
            {
                CharacterStats stats = all[i];
                if (stats == null) continue;
                using (new ApplyScope())
                {
                    List<StatusEffect> list = EffectList(stats);
                    for (int j = list.Count - 1; j >= 0; j--)
                    {
                        StatusEffect removed = list[j];
                        list.RemoveAt(j);
                        NotifyRemoved(stats, removed);
                    }
                    UpdateTracker(stats) = true;
                }
            }
        }

        // ---- local origin and parameter lookup ----

        private static Affliction FindAffliction(EffectRecord record)
        {
            GameObject registered = Resolve(record.OriginObject);
            if (registered != null)
            {
                Affliction byId = registered.GetComponent<Affliction>();
                if (byId != null && OriginMatches(record, byId, record.DisplayId)) return byId;
            }
            if (AfflictionData.Instance == null) return null;
            Affliction best = null;
            int matches = 0;
            List<Affliction> candidates = AfflictionCandidates();
            for (int i = 0; i < candidates.Count; i++) ConsiderAffliction(candidates[i], record, ref best, ref matches);
            return best;
        }

        /// <summary>The affliction prefabs of AfflictionData in their original search order, gathered once per pass.</summary>
        private static List<Affliction> AfflictionCandidates()
        {
            if (s_passAfflictionsBuilt) return s_passAfflictions;
            s_passAfflictionsBuilt = true;
            s_passAfflictions.Clear();
            AfflictionData data = AfflictionData.Instance;
            if (s_afflictionFields == null) s_afflictionFields = typeof(AfflictionData).GetFields(BindingFlags.Instance | BindingFlags.Public);
            FieldInfo[] fields = s_afflictionFields;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(Affliction))
                {
                    var single = fields[i].GetValue(data) as Affliction;
                    if (single != null) s_passAfflictions.Add(single);
                }
                else if (fields[i].FieldType == typeof(Affliction[]))
                {
                    var array = fields[i].GetValue(data) as Affliction[];
                    if (array != null) for (int j = 0; j < array.Length; j++) if (array[j] != null) s_passAfflictions.Add(array[j]);
                }
            }
            if (data.AfflictionsForScripts != null)
                for (int i = 0; i < data.AfflictionsForScripts.Count; i++)
                    if (data.AfflictionsForScripts[i] != null) s_passAfflictions.Add(data.AfflictionsForScripts[i]);
            return s_passAfflictions;
        }

        private static void ConsiderAffliction(Affliction candidate, EffectRecord record, ref Affliction best, ref int matches)
        {
            if (candidate == null || !OriginMatches(record, candidate, candidate.DisplayName.StringID)) return;
            if (!string.Equals(record.OriginTag, candidate.Tag ?? "", StringComparison.Ordinal)) return;
            best = candidate;
            matches++;
        }

        private static GenericAbility FindAbility(GameObject owner, EffectRecord record)
        {
            return FindComponent<GenericAbility>(owner, record);
        }

        private static T FindComponent<T>(GameObject owner, EffectRecord record) where T : Component
        {
            if (owner == null) return null;
            T[] components = ChildrenOf<T>(owner);
            int occurrence = 0;
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                int displayId = -1;
                if (component is GenericAbility) displayId = ((GenericAbility)(object)component).DisplayName.StringID;
                else if (component is Equippable) displayId = ((Equippable)(object)component).DisplayName.StringID;
                if (!OriginMatches(record, component, displayId)) continue;
                if (occurrence++ == record.OriginOrdinal) return component;
            }
            return null;
        }

        private static Phrase FindPhrase(GameObject owner, EffectRecord record)
        {
            if (owner == null) return null;
            Phrase[] phrases = ChildrenOf<Phrase>(owner);
            int occurrence = 0;
            for (int i = 0; i < phrases.Length; i++)
            {
                if (!OriginMatches(record, phrases[i], phrases[i].DisplayName.StringID)) continue;
                if (!string.Equals(record.OriginTag, phrases[i].Tag ?? "", StringComparison.Ordinal)) continue;
                if (occurrence++ == record.OriginOrdinal) return phrases[i];
            }
            ChanterTrait chanter = owner.GetComponent<ChanterTrait>();
            if (chanter != null)
            {
                phrases = KnownPhrasesOf(owner, chanter);
                for (int i = 0; i < phrases.Length; i++)
                {
                    if (phrases[i] == null || !OriginMatches(record, phrases[i], phrases[i].DisplayName.StringID)) continue;
                    if (!string.Equals(record.OriginTag, phrases[i].Tag ?? "", StringComparison.Ordinal)) continue;
                    if (occurrence++ == record.OriginOrdinal) return phrases[i];
                }
            }
            return null;
        }

        private static bool OriginMatches(EffectRecord record, UnityEngine.Object candidate, int displayId)
        {
            return NormalizedName(candidate) == record.OriginName && displayId == record.DisplayId;
        }

        private static int FindComponentOrdinal<T>(GameObject owner, T wanted) where T : Component
        {
            if (owner == null || wanted == null) return 0;
            int displayId = -1;
            if (wanted is GenericAbility) displayId = ((GenericAbility)(object)wanted).DisplayName.StringID;
            else if (wanted is Equippable) displayId = ((Equippable)(object)wanted).DisplayName.StringID;
            string name = NormalizedName(wanted);
            T[] components = ChildrenOf<T>(owner);
            int ordinal = 0;
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                int candidateId = -1;
                if (component is GenericAbility) candidateId = ((GenericAbility)(object)component).DisplayName.StringID;
                else if (component is Equippable) candidateId = ((Equippable)(object)component).DisplayName.StringID;
                if (NormalizedName(component) != name || candidateId != displayId) continue;
                if (component == wanted) return ordinal;
                ordinal++;
            }
            return 0;
        }

        private static int FindPhraseOrdinal(GameObject owner, Phrase wanted)
        {
            if (owner == null || wanted == null) return 0;
            string name = NormalizedName(wanted);
            int displayId = wanted.DisplayName.StringID;
            int ordinal = 0;
            Phrase[] phrases = ChildrenOf<Phrase>(owner);
            for (int i = 0; i < phrases.Length; i++)
            {
                if (NormalizedName(phrases[i]) != name || phrases[i].DisplayName.StringID != displayId || phrases[i].Tag != wanted.Tag) continue;
                if (phrases[i] == wanted) return ordinal;
                ordinal++;
            }
            ChanterTrait chanter = owner.GetComponent<ChanterTrait>();
            if (chanter != null)
            {
                phrases = KnownPhrasesOf(owner, chanter);
                for (int i = 0; i < phrases.Length; i++)
                {
                    if (phrases[i] == null || NormalizedName(phrases[i]) != name || phrases[i].DisplayName.StringID != displayId || phrases[i].Tag != wanted.Tag) continue;
                    if (phrases[i] == wanted) return ordinal;
                    ordinal++;
                }
            }
            return 0;
        }

        private static int FindPhraseData(Phrase phrase, StatusEffectParams wanted)
        {
            if (phrase.m_phraseData == null) return -1;
            for (int i = 0; i < phrase.m_phraseData.Length; i++)
            {
                Phrase.PhraseData data = phrase.m_phraseData[i];
                if (data.AffectsStat == wanted.AffectsStat && data.Apply == wanted.Apply && data.DmgType == wanted.DmgType
                    && data.Value == wanted.Value && data.ExtraValue == wanted.ExtraValue && data.IntervalRate == wanted.IntervalRate
                    && data.IsHostile == wanted.IsHostile && data.TrapPrefab == wanted.TrapPrefab && data.AfflictionPrefab == wanted.AfflictionPrefab)
                    return i;
            }
            return -1;
        }

        private static void FindArray(StatusEffectParams[] array, StatusEffectParams wanted, out int index)
        {
            index = -1;
            if (array == null) return;
            for (int i = 0; i < array.Length; i++) if (ReferenceEquals(array[i], wanted)) { index = i; return; }
        }

        private static StatusEffectParams At(StatusEffectParams[] array, int index)
        {
            return array != null && index >= 0 && index < array.Length ? array[index] : null;
        }

        // ---- UI-only notifications, identity, and bounded primitives ----

        private static void NotifyAdded(CharacterStats stats, StatusEffect effect)
        {
            NotifyUi(AddedEvent, stats, new object[] { stats.gameObject, effect, false });
        }

        private static void NotifyRemoved(CharacterStats stats, StatusEffect effect)
        {
            NotifyUi(RemovedEvent, stats, new object[] { stats.gameObject, effect });
        }

        private static void NotifyUi(FieldInfo eventField, CharacterStats stats, object[] args)
        {
            if (eventField == null) return;
            Delegate handlers = eventField.GetValue(stats) as Delegate;
            if (handlers == null) return;
            Delegate[] invocation = handlers.GetInvocationList();
            for (int i = 0; i < invocation.Length; i++)
            {
                object target = invocation[i].Target;
                if (target == null || !target.GetType().Name.StartsWith("UI", StringComparison.Ordinal)) continue;
                try { invocation[i].DynamicInvoke(args); }
                catch (Exception ex) { Plugin.Log?.LogWarning("Status UI refresh failed: " + ex.Message); }
            }
        }

        private static Guid GetId(GameObject go)
        {
            if (go == null) return Guid.Empty;
            int key = go.GetInstanceID();
            Guid id;
            if (s_passIds.TryGetValue(key, out id)) return id;
            id = EntityRegistry.NetworkId(go);
            if (id == Guid.Empty)
            {
                InstanceID instance = go.GetComponent<InstanceID>();
                if (instance != null) id = instance.Guid;
            }
            s_passIds.Add(key, id);
            return id;
        }

        private static T[] ChildrenOf<T>(GameObject owner) where T : Component
        {
            int key = owner.GetInstanceID();
            T[] components;
            if (!Children<T>.ByOwner.TryGetValue(key, out components))
                Children<T>.ByOwner.Add(key, components = owner.GetComponentsInChildren<T>(true));
            return components;
        }

        private static Phrase[] KnownPhrasesOf(GameObject owner, ChanterTrait chanter)
        {
            int key = owner.GetInstanceID();
            Phrase[] phrases;
            if (!s_passKnownPhrases.TryGetValue(key, out phrases))
                s_passKnownPhrases.Add(key, phrases = chanter.GetKnownPhrases());
            return phrases;
        }

        private static void ClearPassLookups()
        {
            Children<GenericAbility>.ByOwner.Clear();
            Children<Equippable>.ByOwner.Clear();
            Children<Phrase>.ByOwner.Clear();
            s_passKnownPhrases.Clear();
            s_passNames.Clear();
            s_passIds.Clear();
            s_passAfflictions.Clear();
            s_passAfflictionsBuilt = false;
        }

        private static GameObject Resolve(Guid id)
        {
            if (id == Guid.Empty) return null;
            GameObject go = EntityRegistry.ResolveObject(id);
            if (go != null) return go;
            return InstanceID.GetObjectByID(id);
        }

        /// <summary>The object's (for a component, its GameObject's) name without the clone suffix; read once per pass.</summary>
        private static string NormalizedName(UnityEngine.Object go)
        {
            if (go == null) return "";
            int key = go.GetInstanceID();
            string name;
            if (s_passNames.TryGetValue(key, out name)) return name;
            name = go.name ?? "";
            const string clone = "(Clone)";
            int index = name.IndexOf(clone, StringComparison.Ordinal);
            if (index >= 0) name = name.Remove(index, clone.Length);
            name = name.Trim();
            s_passNames.Add(key, name);
            return name;
        }

        private static string Describe(StatusEffect effect)
        {
            return "'" + effect.BundleName + "/" + effect.Params.AffectsStat + "' origin=" + effect.Origin;
        }

        private void WarnHostOnce(string key, string text)
        {
            if (_hostWarnings.Add(key)) Plugin.Log?.LogWarning(text);
        }

        private void WarnGuestOnce(string key, string text)
        {
            if (_guestWarnings.Add(key)) Plugin.Log?.LogWarning(text);
        }

        // A cleaned string has at most maxLength UTF-16 units, which is never more than
        // maxLength * 3 UTF-8 bytes, so the former maxLength * 4 byte bound cannot be exceeded.
        private static void WriteString(BinaryWriter writer, string value, int maxLength)
        {
            writer.Write(Clean(value, maxLength));
        }

        private static string ReadString(MessageReader reader, int maxLength)
        {
            string value = reader.String();
            if (value.Length > maxLength) throw new FormatException("status string exceeds its bound");
            return Clean(value, maxLength);
        }

        /// <summary>Protocol.Clean without its copy for text that is already clean, which is nearly all of it.</summary>
        private static string Clean(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length > maxLength || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]))
                return Protocol.Clean(text, maxLength);
            for (int i = 0; i < text.Length; i++)
                if (char.IsControl(text[i])) return Protocol.Clean(text, maxLength);
            return text;
        }

        private static void WriteGuid(BinaryWriter writer, Guid value)
        {
            // Same sixteen bytes as Guid.ToByteArray on the little-endian platforms the game runs on.
            var bits = new GuidBits { Value = value };
            writer.Write(bits.Low);
            writer.Write(bits.High);
        }

        private static Guid ReadGuid(MessageReader reader)
        {
            var bits = new GuidBits();
            bits.Low = reader.Long();
            bits.High = reader.Long();
            return bits.Value;
        }

        private static Action<StatusEffectParams, T> CreateSetter<T>(PropertyInfo property)
        {
            try
            {
                MethodInfo setter = property != null ? property.GetSetMethod(true) : null;
                return setter != null ? (Action<StatusEffectParams, T>)Delegate.CreateDelegate(typeof(Action<StatusEffectParams, T>), setter) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static HashSet<int> EnumValues(Type type)
        {
            var values = new HashSet<int>();
            foreach (object value in Enum.GetValues(type)) values.Add(Convert.ToInt32(value));
            return values;
        }

        private static void WriteFloat(BinaryWriter writer, float value)
        {
            RequireBounded(value);
            writer.Write(value);
        }

        private static void RequireBounded(float value)
        {
            if (!IsBounded(value)) throw new InvalidDataException("non-finite or unbounded status float " + value);
        }

        private static float ReadFloat(MessageReader reader)
        {
            float value = reader.Float();
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > MaxMagnitude)
                throw new FormatException("status float outside its bound");
            return value;
        }

        private static bool ReadBool(MessageReader reader)
        {
            byte value = reader.Byte();
            if (value > 1) throw new FormatException("status boolean " + value);
            return value != 0;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    // Prefixes, so the set stays right when the game's own handler throws.
    [HarmonyPatch(typeof(CharacterStats), "OnEnable")]
    internal static class StatusTargetEnabledPatch
    {
        private static void Prefix(CharacterStats __instance) => StatusEffectSync.TrackEnabled(__instance);
    }

    [HarmonyPatch(typeof(CharacterStats), "OnDisable")]
    internal static class StatusTargetDisabledPatch
    {
        private static void Prefix(CharacterStats __instance) => StatusEffectSync.TrackDisabled(__instance);
    }
}
