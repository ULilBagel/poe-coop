using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// </summary>
    public sealed class StatusEffectSync : IDisposable
    {
        private const float SendInterval = 0.25f;
        private const float FullRefreshInterval = 3f;
        private const float GuestRetryInterval = 0.25f;
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

        private static int s_applyingHostStatus;
        internal static bool ApplyingHostStatus => s_applyingHostStatus != 0;

        private readonly Session _session;
        private readonly Action<PlayerInfo> _playerJoined;
        private readonly Dictionary<Guid, byte[]> _sent = new Dictionary<Guid, byte[]>();
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
        private Dictionary<Guid, GuestTarget> _fullStaging;

        private sealed class GuestTarget
        {
            public List<EffectRecord> Records = new List<EffectRecord>();
            public readonly Dictionary<uint, MirrorEffect> Effects = new Dictionary<uint, MirrorEffect>();
            public bool Dirty = true;
            public bool Initialized;
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
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _sent.Clear();
            _hostWarnings.Clear();
            _nextSend = 0f;
            _nextFullRefresh = 0f;
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
            foreach (var pair in _guestTargets)
                if (pair.Value.Dirty) ReconcileTarget(pair.Key, pair.Value);
        }

        // ---- host sampling and wire output ----

        private void UpdateHost(float now)
        {
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            bool full = now >= _nextFullRefresh;
            if (full) _nextFullRefresh = now + FullRefreshInterval;

            var current = new Dictionary<Guid, byte[]>();
            CharacterStats[] all = UnityEngine.Object.FindObjectsOfType<CharacterStats>();
            Array.Sort(all, (a, b) => string.CompareOrdinal(a.name, b.name));
            int accepted = 0;
            for (int i = 0; i < all.Length && accepted < MaxTargets; i++)
            {
                CharacterStats stats = all[i];
                if (stats == null || !stats.isActiveAndEnabled) continue;
                Guid id = GetId(stats.gameObject);
                if (id == Guid.Empty || current.ContainsKey(id)) continue;
                byte[] payload = BuildTargetPayload(id, stats);
                current.Add(id, payload);
                accepted++;
            }
            if (all.Length > MaxTargets) WarnHostOnce("targets", "Too many status-effect targets; only " + MaxTargets + " are synchronized.");

            var changed = new List<KeyValuePair<Guid, byte[]>>();
            foreach (var pair in current)
            {
                byte[] previous;
                if (full || !_sent.TryGetValue(pair.Key, out previous) || !BytesEqual(previous, pair.Value))
                    changed.Add(pair);
            }
            if (!full)
            {
                foreach (Guid missing in _sent.Keys)
                    if (!current.ContainsKey(missing)) changed.Add(new KeyValuePair<Guid, byte[]>(missing, EmptyTargetPayload(missing)));
            }

            SendSnapshots(changed, full);
            for (int i = 0; i < changed.Count; i++)
            {
                Guid id = changed[i].Key;
                if (current.ContainsKey(id)) _sent[id] = changed[i].Value;
                else _sent.Remove(id);
            }
        }

        private byte[] BuildTargetPayload(Guid id, CharacterStats stats)
        {
            IList<StatusEffect> effects = stats.ActiveStatusEffects;
            var encoded = new List<byte[]>(Math.Min(effects.Count, MaxEffectsPerTarget));
            for (int i = 0; i < effects.Count && encoded.Count < MaxEffectsPerTarget; i++)
            {
                StatusEffect effect = effects[i];
                if (effect == null || effect.Params == null) continue;
                byte[] data;
                string error;
                if (TryWriteEffect(effect, out data, out error)) encoded.Add(data);
                else WarnHostOnce(id + ":" + effect.EffectID + ":" + error,
                    "Unsupported status effect " + Describe(effect) + " on " + stats.name + ": " + error);
            }
            if (effects.Count > MaxEffectsPerTarget)
                WarnHostOnce(id + ":count", "Status effect list on " + stats.name + " exceeded " + MaxEffectsPerTarget + "; extra effects were omitted.");

            while (true)
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(id.ToByteArray());
                    writer.Write(encoded.Count);
                    for (int i = 0; i < encoded.Count; i++) writer.Write(encoded[i]);
                    writer.Flush();
                    byte[] payload = stream.ToArray();
                    if (payload.Length <= MaxTargetBytes) return payload;
                    if (encoded.Count == 0) throw new InvalidOperationException("empty status target exceeded its size bound");
                }
                encoded.RemoveAt(encoded.Count - 1);
                WarnHostOnce(id + ":bytes", "Status effect list on " + stats.name + " exceeded the per-target byte bound; trailing effects were omitted.");
            }
        }

        private static byte[] EmptyTargetPayload(Guid id)
        {
            using (var stream = new MemoryStream(20))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(id.ToByteArray());
                writer.Write(0);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private bool TryWriteEffect(StatusEffect effect, out byte[] data, out string error)
        {
            data = null;
            error = null;
            if (effect.Params.AffectsStat == StatusEffect.ModifiedStat.StealSpell && effect.Spells != null && effect.Spells.Count > 0)
            {
                error = "materialized stolen-spell lists have no presentation proxy";
                return false;
            }
            try
            {
                using (var stream = new MemoryStream(512))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    WriteEffect(writer, effect);
                    writer.Flush();
                    data = stream.ToArray();
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is NullReferenceException)
            {
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
            writer.Write(GetId(effect.Owner).ToByteArray());
            writer.Write(GetId(effect.Source).ToByteArray());
            writer.Write(GetId(effect.ExtraObject).ToByteArray());
            writer.Write((byte)originKind);
            WriteString(writer, originName, 128);
            WriteString(writer, originTag, 128);
            writer.Write(displayId);
            writer.Write(originOrdinal);
            writer.Write(originObject.ToByteArray());
            writer.Write((byte)templateKind);
            writer.Write(templateOrdinal);
            writer.Write(parameterIndex);
            writer.Write((int)effect.AbilityType);

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
            writer.Write(flags);
            writer.Write(unchecked((int)effect.m_suspensionCount));
            WriteFloat(writer, BaseDuration(effect));
            WriteFloat(writer, effect.m_durationOverride);
            WriteFloat(writer, effect.m_timeActive);
            WriteFloat(writer, TemporaryDuration(effect));
            WriteFloat(writer, effect.UnadjustedDurationAdd);
            WriteFloat(writer, effect.Scale);
            WriteFloat(writer, effect.m_durationScale);
            WriteFloat(writer, effect.m_religiousScale);
            WriteFloat(writer, effect.m_intervalTimer);
            writer.Write(unchecked((int)effect.m_intervalCount));
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

            int paramFlags = 0;
            if (p.Persistent) paramFlags |= 1;
            if (p.IsHostile) paramFlags |= 2;
            if (p.KeepOnDeath) paramFlags |= 4;
            if (p.OneHitUse) paramFlags |= 8;
            if (p.LastsUntilCombatEnds) paramFlags |= 16;
            if (p.LastsUntilRest) paramFlags |= 32;
            if (p.HideFromUi) paramFlags |= 64;
            if (p.HideFromPortrait) paramFlags |= 128;
            if (p.ChecksReligion) paramFlags |= 256;
            if (p.DontHideFromLog) paramFlags |= 512;
            if (p.IgnoreAbilityDeactivation) paramFlags |= 1024;
            if (p.DoNotScalePlayer) paramFlags |= 2048;
            if (p.IsCleanedUp) paramFlags |= 4096;
            writer.Write(paramFlags);
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

            StatusEffectTrigger trigger = p.TriggerAdjustment ?? new StatusEffectTrigger();
            writer.Write((int)trigger.Type);
            WriteFloat(writer, trigger.TriggerValue);
            WriteFloat(writer, trigger.ValueAdjustment);
            WriteFloat(writer, trigger.DurationAdjustment);
            WriteFloat(writer, trigger.RadiusAdjustment);
            writer.Write(trigger.MaxTriggerCount);
            writer.Write(trigger.RemoveEffectAtMax);
            writer.Write(trigger.ResetTriggerOnEffectPulse);
            writer.Write(trigger.ResetTriggerOnEffectEnd);

            StatusEffectLevelScaling scaling = p.LevelScaling ?? new StatusEffectLevelScaling();
            writer.Write(scaling.BaseLevel);
            writer.Write(scaling.LevelIncrement);
            writer.Write(scaling.MaxLevel);
            WriteFloat(writer, scaling.ValueAdjustment);
            WriteFloat(writer, scaling.ExtraValueAdjustment);
            WriteFloat(writer, scaling.DurationAdjustment);
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
                name = NormalizedName(affliction.gameObject);
                tag = affliction.Tag;
                displayId = affliction.DisplayName.StringID;
                FindArray(affliction.StatusEffects, effect.Params, out parameterIndex);
                if (parameterIndex >= 0) templateKind = TemplateKind.Origin;
            }
            else if (origin is Phrase)
            {
                var phrase = (Phrase)origin;
                kind = OriginKind.Phrase;
                name = NormalizedName(phrase.gameObject);
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
                name = NormalizedName(equipment.gameObject);
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
                name = NormalizedName(ability.gameObject);
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

        private void SendSnapshots(List<KeyValuePair<Guid, byte[]>> snapshots, bool full)
        {
            var chunks = new List<List<KeyValuePair<Guid, byte[]>>>();
            var sizes = new List<int>();
            List<KeyValuePair<Guid, byte[]>> chunk = null;
            int size = 0;
            for (int i = 0; i < snapshots.Count; i++)
            {
                int recordSize = snapshots[i].Value.Length;
                if (chunk == null || chunk.Count >= MaxTargetsPerMessage || size + recordSize > MaxMessageBytes - 14)
                {
                    chunk = new List<KeyValuePair<Guid, byte[]>>();
                    chunks.Add(chunk);
                    sizes.Add(0);
                    size = 14;
                }
                chunk.Add(snapshots[i]);
                size += recordSize;
                sizes[sizes.Count - 1] = size;
            }
            if (chunks.Count == 0)
            {
                if (!full) return;
                chunks.Add(new List<KeyValuePair<Guid, byte[]>>());
                sizes.Add(14);
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                byte mode = FullDelta;
                if (full)
                {
                    if (chunks.Count == 1) mode = FullSingle;
                    else if (i == 0) mode = FullBegin;
                    else if (i == chunks.Count - 1) mode = FullEnd;
                    else mode = FullContinuation;
                }
                using (var stream = new MemoryStream(sizes[i]))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write((byte)MessageType.StatusEffects);
                    writer.Write(_epoch);
                    writer.Write(_revision);
                    writer.Write(mode);
                    writer.Write(chunks[i].Count);
                    for (int j = 0; j < chunks[i].Count; j++) writer.Write(chunks[i][j].Value);
                    writer.Flush();
                    byte[] message = stream.ToArray();
                    if (message.Length > TcpTransport.MaxMessageSize) throw new InvalidOperationException("status effect message exceeds transport bound");
                    _session.SendToGuests(message);
                }
                _revision = unchecked(_revision + 1);
            }
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
            int epoch = reader.Int();
            int revision = reader.Int();
            byte full = reader.Byte();
            if (full > FullEnd) throw new FormatException("status full mode " + full);
            int targetCount = reader.Int();
            if (targetCount < 0 || targetCount > MaxTargetsPerMessage) throw new FormatException("status target count " + targetCount);

            var parsed = new Dictionary<Guid, GuestTarget>(targetCount);
            for (int i = 0; i < targetCount; i++)
            {
                Guid targetId = reader.Guid();
                if (targetId == Guid.Empty || parsed.ContainsKey(targetId)) throw new FormatException("duplicate or empty status target");
                int effectCount = reader.Int();
                if (effectCount < 0 || effectCount > MaxEffectsPerTarget) throw new FormatException("status effect count " + effectCount);
                var target = new GuestTarget();
                var ids = new HashSet<uint>();
                for (int j = 0; j < effectCount; j++)
                {
                    EffectRecord record = ReadEffect(reader);
                    if (!ids.Add(record.Id)) throw new FormatException("duplicate host EffectID " + record.Id);
                    target.Records.Add(record);
                }
                parsed.Add(targetId, target);
            }

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

        private static EffectRecord ReadEffect(MessageReader reader)
        {
            var r = new EffectRecord
            {
                Id = unchecked((uint)reader.Int()),
                Owner = reader.Guid(),
                Source = reader.Guid(),
                ExtraObject = reader.Guid(),
                OriginKind = (OriginKind)reader.Byte(),
                OriginName = ReadString(reader, 128),
                OriginTag = ReadString(reader, 128),
                DisplayId = reader.Int(),
                OriginOrdinal = reader.Int(),
                OriginObject = reader.Guid(),
                TemplateKind = (TemplateKind)reader.Byte(),
                TemplateOrdinal = reader.Int(),
                ParameterIndex = reader.Int(),
                AbilityType = reader.Int(),
                Flags = reader.Int(),
                SuspensionCount = reader.Int(),
                Duration = ReadFloat(reader),
                DurationOverride = ReadFloat(reader),
                TimeActive = ReadFloat(reader),
                TemporaryDuration = ReadFloat(reader),
                UnadjustedDurationAdd = ReadFloat(reader),
                Scale = ReadFloat(reader),
                DurationScale = ReadFloat(reader),
                ReligiousScale = ReadFloat(reader),
                IntervalTimer = ReadFloat(reader),
                IntervalCount = unchecked((uint)reader.Int()),
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
                || r.IntervalCount > MaxCounter || r.GeneralCounter > MaxCounter || r.TriggerCount < 0 || r.TriggerCount > MaxCounter
                || r.RestCycles < 0 || r.RestCycles > MaxCounter || r.MaxRestCycles < 0 || r.MaxRestCycles > MaxCounter
                || r.MaxTriggerCount < 0 || r.MaxTriggerCount > MaxCounter || r.ScalingBase < 0 || r.ScalingBase > MaxCounter
                || r.ScalingIncrement < 0 || r.ScalingIncrement > MaxCounter || r.ScalingMax < 0)
                throw new FormatException("status ordinal or counter outside its bound");
            if (!Enum.IsDefined(typeof(GenericAbility.AbilityType), r.AbilityType)
                || !Enum.IsDefined(typeof(StatusEffect.ApplyType), r.Apply)
                || !Enum.IsDefined(typeof(StatusEffect.ModifiedStat), r.Stat)
                || !Enum.IsDefined(typeof(DamagePacket.DamageType), r.DamageType)
                || !Enum.IsDefined(typeof(StatusEffectParams.IntervalRateType), r.IntervalRate)
                || !Enum.IsDefined(typeof(CharacterStats.Race), r.Race)
                || !Enum.IsDefined(typeof(CharacterStats.Class), r.Class)
                || !Enum.IsDefined(typeof(CharacterStats.AttributeScoreType), r.Attribute)
                || !Enum.IsDefined(typeof(CharacterStats.DefenseType), r.Defense)
                || !Enum.IsDefined(typeof(Equippable.EquipmentSlot), r.Slot)
                || !Enum.IsDefined(typeof(StatusEffectTrigger.TriggerType), r.TriggerType))
                throw new FormatException("invalid status enumeration");
            if ((r.TemplateKind == TemplateKind.Inline) != (r.ParameterIndex < 0))
                throw new FormatException("inconsistent status parameter template");
        }

        private void MergeTarget(Guid id, GuestTarget incoming)
        {
            GuestTarget existing;
            if (_guestTargets.TryGetValue(id, out existing))
            {
                existing.Records = incoming.Records;
                existing.Dirty = true;
            }
            else _guestTargets.Add(id, incoming);
        }

        private void MergeFullManifest(Dictionary<Guid, GuestTarget> manifest)
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

        private void ReconcileTarget(Guid id, GuestTarget target)
        {
            target.Dirty = false;
            GameObject targetObject = Resolve(id);
            if (targetObject == null) { target.Dirty = true; return; }
            CharacterStats stats = targetObject.GetComponent<CharacterStats>();
            if (stats == null) { target.Dirty = true; return; }

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

                var present = new HashSet<uint>();
                for (int i = 0; i < target.Records.Count; i++) present.Add(target.Records[i].Id);
                var absent = new List<uint>();
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
            origins = new OriginReferences
            {
                Owner = Resolve(record.Owner),
                Source = Resolve(record.Source),
                ExtraObject = Resolve(record.ExtraObject),
            };
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
            CleanedUpProperty.SetValue(p, (r.ParamFlags & 4096) != 0, null);
            MergedValueProperty.SetValue(p, r.MergedValue, null);
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
                if (byId != null && OriginMatches(record, byId.gameObject, record.DisplayId)) return byId;
            }
            if (AfflictionData.Instance == null) return null;
            Affliction best = null;
            int matches = 0;
            FieldInfo[] fields = typeof(AfflictionData).GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(Affliction)) ConsiderAffliction(fields[i].GetValue(AfflictionData.Instance) as Affliction, record, ref best, ref matches);
                else if (fields[i].FieldType == typeof(Affliction[]))
                {
                    var array = fields[i].GetValue(AfflictionData.Instance) as Affliction[];
                    if (array != null) for (int j = 0; j < array.Length; j++) ConsiderAffliction(array[j], record, ref best, ref matches);
                }
            }
            if (AfflictionData.Instance.AfflictionsForScripts != null)
                for (int i = 0; i < AfflictionData.Instance.AfflictionsForScripts.Count; i++)
                    ConsiderAffliction(AfflictionData.Instance.AfflictionsForScripts[i], record, ref best, ref matches);
            return best;
        }

        private static void ConsiderAffliction(Affliction candidate, EffectRecord record, ref Affliction best, ref int matches)
        {
            if (candidate == null || !OriginMatches(record, candidate.gameObject, candidate.DisplayName.StringID)) return;
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
            T[] components = owner.GetComponentsInChildren<T>(true);
            int occurrence = 0;
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                int displayId = -1;
                if (component is GenericAbility) displayId = ((GenericAbility)(object)component).DisplayName.StringID;
                else if (component is Equippable) displayId = ((Equippable)(object)component).DisplayName.StringID;
                if (!OriginMatches(record, component.gameObject, displayId)) continue;
                if (occurrence++ == record.OriginOrdinal) return component;
            }
            return null;
        }

        private static Phrase FindPhrase(GameObject owner, EffectRecord record)
        {
            if (owner == null) return null;
            Phrase[] phrases = owner.GetComponentsInChildren<Phrase>(true);
            int occurrence = 0;
            for (int i = 0; i < phrases.Length; i++)
            {
                if (!OriginMatches(record, phrases[i].gameObject, phrases[i].DisplayName.StringID)) continue;
                if (!string.Equals(record.OriginTag, phrases[i].Tag ?? "", StringComparison.Ordinal)) continue;
                if (occurrence++ == record.OriginOrdinal) return phrases[i];
            }
            ChanterTrait chanter = owner.GetComponent<ChanterTrait>();
            if (chanter != null)
            {
                phrases = chanter.GetKnownPhrases();
                for (int i = 0; i < phrases.Length; i++)
                {
                    if (phrases[i] == null || !OriginMatches(record, phrases[i].gameObject, phrases[i].DisplayName.StringID)) continue;
                    if (!string.Equals(record.OriginTag, phrases[i].Tag ?? "", StringComparison.Ordinal)) continue;
                    if (occurrence++ == record.OriginOrdinal) return phrases[i];
                }
            }
            return null;
        }

        private static bool OriginMatches(EffectRecord record, GameObject candidate, int displayId)
        {
            return NormalizedName(candidate) == record.OriginName && displayId == record.DisplayId;
        }

        private static int FindComponentOrdinal<T>(GameObject owner, T wanted) where T : Component
        {
            if (owner == null || wanted == null) return 0;
            int displayId = -1;
            if (wanted is GenericAbility) displayId = ((GenericAbility)(object)wanted).DisplayName.StringID;
            else if (wanted is Equippable) displayId = ((Equippable)(object)wanted).DisplayName.StringID;
            string name = NormalizedName(wanted.gameObject);
            T[] components = owner.GetComponentsInChildren<T>(true);
            int ordinal = 0;
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                int candidateId = -1;
                if (component is GenericAbility) candidateId = ((GenericAbility)(object)component).DisplayName.StringID;
                else if (component is Equippable) candidateId = ((Equippable)(object)component).DisplayName.StringID;
                if (NormalizedName(component.gameObject) != name || candidateId != displayId) continue;
                if (component == wanted) return ordinal;
                ordinal++;
            }
            return 0;
        }

        private static int FindPhraseOrdinal(GameObject owner, Phrase wanted)
        {
            if (owner == null || wanted == null) return 0;
            string name = NormalizedName(wanted.gameObject);
            int displayId = wanted.DisplayName.StringID;
            int ordinal = 0;
            Phrase[] phrases = owner.GetComponentsInChildren<Phrase>(true);
            for (int i = 0; i < phrases.Length; i++)
            {
                if (NormalizedName(phrases[i].gameObject) != name || phrases[i].DisplayName.StringID != displayId || phrases[i].Tag != wanted.Tag) continue;
                if (phrases[i] == wanted) return ordinal;
                ordinal++;
            }
            ChanterTrait chanter = owner.GetComponent<ChanterTrait>();
            if (chanter != null)
            {
                phrases = chanter.GetKnownPhrases();
                for (int i = 0; i < phrases.Length; i++)
                {
                    if (phrases[i] == null || NormalizedName(phrases[i].gameObject) != name || phrases[i].DisplayName.StringID != displayId || phrases[i].Tag != wanted.Tag) continue;
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
            Guid id = EntityRegistry.NetworkId(go);
            if (id != Guid.Empty) return id;
            InstanceID instance = go.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static GameObject Resolve(Guid id)
        {
            if (id == Guid.Empty) return null;
            GameObject go = EntityRegistry.ResolveObject(id);
            if (go != null) return go;
            return InstanceID.GetObjectByID(id);
        }

        private static string NormalizedName(GameObject go)
        {
            if (go == null) return "";
            string name = go.name ?? "";
            const string clone = "(Clone)";
            int index = name.IndexOf(clone, StringComparison.Ordinal);
            if (index >= 0) name = name.Remove(index, clone.Length);
            return name.Trim();
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

        private static void WriteString(BinaryWriter writer, string value, int maxLength)
        {
            value = Protocol.Clean(value, maxLength);
            while (Encoding.UTF8.GetByteCount(value) > maxLength * 4) value = value.Substring(0, value.Length - 1);
            writer.Write(value ?? "");
        }

        private static string ReadString(MessageReader reader, int maxLength)
        {
            string value = reader.String();
            if (value.Length > maxLength || Encoding.UTF8.GetByteCount(value) > maxLength * 4)
                throw new FormatException("status string exceeds its bound");
            return Protocol.Clean(value, maxLength);
        }

        private static void WriteFloat(BinaryWriter writer, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > MaxMagnitude)
                throw new InvalidDataException("non-finite or unbounded status float " + value);
            writer.Write(value);
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
}
