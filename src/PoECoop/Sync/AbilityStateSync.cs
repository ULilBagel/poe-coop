using System;
using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates the state behind party ability buttons and class resources. The
    /// host sends changed owners ten times a second and a full repair every two
    /// seconds. Ability instances are identified by owner, normalized prefab name,
    /// display string id and occurrence, never by their per-process Guid.
    ///
    /// Wire layout after the type byte: epoch, revision, full, owner count. Each
    /// owner has its id, a flags byte, recovery/grimoire timers, eight spell cast
    /// counts, seven modal cooldowns, optional focus/wounds/chanter resources,
    /// then ability records. With the ability-delta owner flag (never in a full
    /// message) only the records that changed since the owner was last sent
    /// follow, and the guest merges them into the list it already holds.
    /// An ability record is its key, cooldown counter, uses left, not-ready mask,
    /// per-encounter/modal timers, state flags and optional chant/spiritshift
    /// presentation timers. Optional chant references use the same ability key.
    /// </summary>
    public sealed class AbilityStateSync
    {
        private const float SendInterval = 0.1f;
        private const float FullRefreshInterval = 2f;
        // Keeps the periodic repair of this stream out of phase with the other streams after a forced refresh.
        private const float ForcedRefreshPhase = 1.65f;
        // Cached ability keys are re-derived at the repair cadence in case an ability was renamed in place.
        private const float IndexLifetime = FullRefreshInterval;
        private const float UnresolvedRetryInterval = 0.25f;
        private const int MaxCachedOwners = MaxOwners * 4;
        private const int MaxPooledOwners = MaxOwners * 4;
        private const int MaxOwners = 30;
        private const int MaxAbilitiesPerOwner = 192;
        private const int MaxTotalAbilities = 2048;
        private const int MaxNameLength = 96;
        private const int MaxDisplayStringId = 10000000;
        private const int MaxCounter = 1000000;
        private const int MaxPhraseIndex = 64;
        private const float MaxTimer = 86400f;
        private const float MaxResource = 1000000f;
        private const int SpellLevelCount = 8;
        private const int ModalCooldownCount = 7;
        private const int AllNotReadyBits = 0x1fffff;

        private const byte ResourceFocus = 1;
        private const byte ResourceWounds = 2;
        private const byte ResourceChanter = 4;
        private const byte ResourceAll = ResourceFocus | ResourceWounds | ResourceChanter;

        private const byte OwnerAbilityDelta = 1;

        private const byte AbilityActivated = 1;
        private const byte AbilityUiActivated = 2;
        private const byte AbilityApplied = 4;
        private const byte AbilityActivatedLaunching = 8;
        private const byte AbilityStatusEffectsNeeded = 16;
        private const byte AbilityStatusEffectsActivated = 32;
        private const byte AbilityAllFlags = 63;

        private const byte KindChant = 1;
        private const byte KindSpiritshift = 2;

        private static readonly AccessTools.FieldRef<CharacterStats, float> RecoveryTimer =
            AccessTools.FieldRefAccess<CharacterStats, float>("m_recoveryTimer");
        private static readonly AccessTools.FieldRef<CharacterStats, float> TotalRecoveryTime =
            AccessTools.FieldRefAccess<CharacterStats, float>("m_totalRecoveryTime");
        private static readonly AccessTools.FieldRef<CharacterStats, int[]> SpellCastCounts =
            AccessTools.FieldRefAccess<CharacterStats, int[]>("m_spellCastCount");
        private static readonly AccessTools.FieldRef<CharacterStats, float[]> ModalCooldownTimers =
            AccessTools.FieldRefAccess<CharacterStats, float[]>("m_modalCooldownTimer");

        private static readonly AccessTools.FieldRef<GenericAbility, int> CooldownCounter =
            AccessTools.FieldRefAccess<GenericAbility, int>("m_cooldownCounter");
        private static readonly AccessTools.FieldRef<GenericAbility, float> PerEncounterResetTimer =
            AccessTools.FieldRefAccess<GenericAbility, float>("m_perEncounterResetTimer");
        private static readonly AccessTools.FieldRef<GenericAbility, float> RezModalCooldown =
            AccessTools.FieldRefAccess<GenericAbility, float>("m_rez_modal_cooldown");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> Activated =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_activated");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> ActivatedLaunching =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_activatedLaunching");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> Applied =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_applied");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> UiActivated =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_UITriggered");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> StatusEffectsNeeded =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_statusEffectsNeeded");
        private static readonly AccessTools.FieldRef<GenericAbility, bool> StatusEffectsActivated =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_statusEffectsActivated");
        private static readonly AccessTools.FieldRef<GenericAbility, GenericAbility.NotReadyValue> NotReady =
            AccessTools.FieldRefAccess<GenericAbility, GenericAbility.NotReadyValue>("m_reason_not_ready");

        private static readonly AccessTools.FieldRef<FocusTrait, float> Focus =
            AccessTools.FieldRefAccess<FocusTrait, float>("m_focus");
        private static readonly AccessTools.FieldRef<FocusTrait, float> FocusResetTimer =
            AccessTools.FieldRefAccess<FocusTrait, float>("m_focusResetTimer");
        private static readonly AccessTools.FieldRef<WoundsTrait, float> WoundDamageBuffer =
            AccessTools.FieldRefAccess<WoundsTrait, float>("m_woundDamageBuffer");
        private static readonly AccessTools.FieldRef<WoundsTrait, float> WoundResetTimer =
            AccessTools.FieldRefAccess<WoundsTrait, float>("m_woundResetTimer");
        private static readonly AccessTools.FieldRef<ChanterTrait, int> PhraseCount =
            AccessTools.FieldRefAccess<ChanterTrait, int>("m_phraseCount");
        private static readonly AccessTools.FieldRef<ChanterTrait, int> PhraseCountMax =
            AccessTools.FieldRefAccess<ChanterTrait, int>("m_phraseCountMax");
        private static readonly AccessTools.FieldRef<ChanterTrait, Chant> CurrentChant =
            AccessTools.FieldRefAccess<ChanterTrait, Chant>("m_chant");
        private static readonly AccessTools.FieldRef<ChanterTrait, Chant> DesiredChant =
            AccessTools.FieldRefAccess<ChanterTrait, Chant>("m_lastChant");
        private static readonly AccessTools.FieldRef<Chant, float> ChantTimer =
            AccessTools.FieldRefAccess<Chant, float>("m_chantTimer");
        private static readonly AccessTools.FieldRef<Chant, float> ChantDelayTimer =
            AccessTools.FieldRefAccess<Chant, float>("m_delayTimer");
        private static readonly AccessTools.FieldRef<Chant, float> ChantStartupDelayTimer =
            AccessTools.FieldRefAccess<Chant, float>("m_startupDelayTimer");
        private static readonly AccessTools.FieldRef<Chant, int> ChantCurrentPhrase =
            AccessTools.FieldRefAccess<Chant, int>("m_currentPhrase");
        private static readonly AccessTools.FieldRef<Spiritshift, float> SpiritshiftActiveTimer =
            AccessTools.FieldRefAccess<Spiritshift, float>("m_activeTimer");
        private static readonly AccessTools.FieldRef<Spiritshift, float> SpiritshiftCommentDelay =
            AccessTools.FieldRefAccess<Spiritshift, float>("m_commentDelay");

        [ThreadStatic]
        private static int s_applyDepth;

        private readonly Session _session;
        private Dictionary<Guid, OwnerState> _sent = new Dictionary<Guid, OwnerState>();
        private Dictionary<Guid, OwnerState> _current = new Dictionary<Guid, OwnerState>();
        private readonly Dictionary<Guid, OwnerState> _pending = new Dictionary<Guid, OwnerState>();
        private readonly HashSet<Guid> _applied = new HashSet<Guid>();
        private readonly Dictionary<Guid, AbilityIndex> _indexes = new Dictionary<Guid, AbilityIndex>();
        private readonly Stack<OwnerState> _pool = new Stack<OwnerState>();
        private readonly List<OwnerState> _changed = new List<OwnerState>();
        private readonly List<OwnerState> _incoming = new List<OwnerState>();
        private readonly HashSet<Guid> _incomingIds = new HashSet<Guid>();
        private readonly HashSet<AbilityKey> _readKeys = new HashSet<AbilityKey>();
        private readonly List<Guid> _removed = new List<Guid>();
        private readonly Dictionary<AbilityKey, int> _occurrences = new Dictionary<AbilityKey, int>();
        private float _now, _nextSend, _nextFullRefresh, _retryUnresolvedAt;
        private int _epoch = 1, _revision;
        private int _lastEpoch = -1, _lastRevision = -1;
        private bool _wasHosting, _wasJoined, _refreshForced;

        public AbilityStateSync(Session session)
        {
            _session = session;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelLoaded;
            GameState.OnLevelUnload += OnLevelLoaded;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.FullStateRequested -= OnFullStateRequested;
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelLoaded;
            _sent.Clear();
            _current.Clear();
            _pending.Clear();
            _applied.Clear();
            _indexes.Clear();
            _pool.Clear();
        }

        internal static bool IsApplyingHostState => s_applyDepth > 0;

        internal static IDisposable HostApplyScope()
        {
            s_applyDepth++;
            return new ApplyScope();
        }

        private struct ApplyScope : IDisposable
        {
            public void Dispose() => s_applyDepth--;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            _nextSend = 0f;
            ForceFullRefresh();
        }

        // A guest that just became ready missed everything sent while it loaded, including the base of later deltas.
        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            _nextSend = 0f;
            ForceFullRefresh();
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _indexes.Clear();
            if (_session.IsHost)
            {
                AdvanceEpoch();
                ReleaseAll(_sent);
                _nextSend = 0f;
                ForceFullRefresh();
            }
            ResetGuest();
        }

        private void ForceFullRefresh()
        {
            _nextFullRefresh = 0f;
            _refreshForced = true;
        }

        private void ResetGuest()
        {
            _lastEpoch = -1;
            _lastRevision = -1;
            ReleaseAll(_pending);
            _applied.Clear();
            _retryUnresolvedAt = 0f;
        }

        private void AdvanceEpoch()
        {
            _epoch = _epoch == int.MaxValue ? 1 : _epoch + 1;
            _revision = 0;
        }

        public void Update(float now)
        {
            _now = now;
            if (_session.IsHost && !_wasHosting)
            {
                AdvanceEpoch();
                ReleaseAll(_sent);
                _indexes.Clear();
                ForceFullRefresh();
            }
            _wasHosting = _session.IsHost;
            if (_session.IsGuest && !_wasJoined)
            {
                ResetGuest();
                _indexes.Clear();
            }
            _wasJoined = _session.IsGuest;

            if (_session.IsHost) HostUpdate(now);
            else if (_session.IsGuest) GuestUpdate();
        }

        // ---- host ----

        private void HostUpdate(float now)
        {
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            long started = PerfStats.Begin();
            SendChanges(now);
            PerfStats.End("ability.capture", started);
        }

        private void SendChanges(float now)
        {
            Dictionary<Guid, OwnerState> current = CaptureOwners();
            bool full = now >= _nextFullRefresh || OwnerSetChanged(current);
            if (_revision == int.MaxValue)
            {
                AdvanceEpoch();
                full = true;
            }
            if (full)
            {
                _nextFullRefresh = now + FullRefreshInterval + (_refreshForced ? ForcedRefreshPhase : 0f);
                _refreshForced = false;
            }

            _changed.Clear();
            int skipped = 0;
            foreach (var pair in current)
            {
                if (full || !_sent.TryGetValue(pair.Key, out OwnerState last) || !Same(last, pair.Value))
                    _changed.Add(pair.Value);
                else
                    skipped += pair.Value.Abilities.Count;
            }
            if (!full && _changed.Count == 0)
            {
                PerfStats.Count("ability.skipped", skipped);
                PromoteCurrent();
                return;
            }

            _revision++;
            int records = 0;
            var message = new MessageWriter(MessageType.AbilityStates)
                .Int(_epoch).Int(_revision).Byte((byte)(full ? 1 : 0)).Int(_changed.Count);
            for (int i = 0; i < _changed.Count; i++)
            {
                OwnerState owner = _changed[i];
                // A delta owner whose ability list is unchanged only carries the records that differ from the last one sent.
                OwnerState last = null;
                if (!full && (!_sent.TryGetValue(owner.Id, out last) || !SameKeys(last, owner))) last = null;
                int written = WriteOwner(message, owner, last);
                records += written;
                skipped += owner.Abilities.Count - written;
            }
            _changed.Clear();
            byte[] data = message.ToArray();
            if (data.Length > TcpTransport.MaxMessageSize)
            {
                Plugin.Log.LogWarning("Ability state snapshot exceeded the message limit; waiting for the next revision");
                ReleaseAll(current);
                return;
            }
            _session.SendToGuests(data);
            PerfStats.Count(full ? "ability.full" : "ability.delta");
            PerfStats.Count("ability.records", records);
            PerfStats.Count("ability.skipped", skipped);
            PromoteCurrent();
        }

        /// <summary>Makes the captured states the last sent ones and recycles the previous set.</summary>
        private void PromoteCurrent()
        {
            ReleaseAll(_sent);
            Dictionary<Guid, OwnerState> previous = _sent;
            _sent = _current;
            _current = previous;
        }

        private bool OwnerSetChanged(Dictionary<Guid, OwnerState> current)
        {
            if (current.Count != _sent.Count) return true;
            foreach (Guid id in current.Keys)
                if (!_sent.ContainsKey(id)) return true;
            return false;
        }

        private Dictionary<Guid, OwnerState> CaptureOwners()
        {
            Dictionary<Guid, OwnerState> result = _current;
            ReleaseAll(result);
            int totalAbilities = 0;
            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            for (int i = 0; i < members.Length && result.Count < MaxOwners; i++)
            {
                PartyMemberAI member = members[i];
                if (member == null) continue;
                GameObject obj = member.gameObject;
                Guid id = NetworkId(obj);
                if (id == Guid.Empty || result.ContainsKey(id)) continue;
                CharacterStats stats = StatsFor(id, obj);
                if (stats == null) continue;
                result.Add(id, CaptureOwner(id, stats, ref totalAbilities));
            }
            foreach (Guid id in _sent.Keys)
            {
                if (result.Count >= MaxOwners) break;
                if (result.ContainsKey(id)) continue;
                GameObject obj = ResolveObject(id);
                CharacterStats stats = obj != null && obj.activeInHierarchy ? StatsFor(id, obj) : null;
                if (stats != null) result.Add(id, CaptureOwner(id, stats, ref totalAbilities));
            }
            return result;
        }

        private OwnerState CaptureOwner(Guid id, CharacterStats stats, ref int totalAbilities)
        {
            OwnerState owner = RentOwner();
            owner.Id = id;
            owner.Recovery = Timer(RecoveryTimer(stats));
            owner.TotalRecovery = Timer(TotalRecoveryTime(stats));
            owner.GrimoireCooldown = Timer(stats.CurrentGrimoireCooldown);
            int[] casts = SpellCastCounts(stats);
            for (int i = 0; i < SpellLevelCount; i++)
                owner.SpellCasts[i] = casts != null && i < casts.Length ? Counter(casts[i]) : 0;
            float[] modalTimers = ModalCooldownTimers(stats);
            for (int i = 0; i < ModalCooldownCount; i++)
                owner.ModalCooldowns[i] = modalTimers != null && i < modalTimers.Length ? Timer(modalTimers[i]) : 0f;

            AbilityIndex index = IndexOf(id, stats);
            FocusTrait focus = null;
            WoundsTrait wounds = null;
            ChanterTrait chanter = null;
            int scanned = 0;
            for (int i = 0; i < index.Abilities.Count && owner.Abilities.Count < MaxAbilitiesPerOwner && totalAbilities < MaxTotalAbilities; i++)
            {
                scanned = i + 1;
                AbilityKey key = index.Keys[i];
                if (key.Name == null) continue;
                GenericAbility ability = index.Abilities[i];
                if (focus == null) focus = ability as FocusTrait;
                if (wounds == null) wounds = ability as WoundsTrait;
                if (chanter == null) chanter = ability as ChanterTrait;
                if (key.Name.Length == 0) continue;
                owner.Abilities.Add(CaptureAbility(ability, key));
                totalAbilities++;
            }

            if (focus != null)
            {
                owner.Resources |= ResourceFocus;
                owner.Focus = Resource(Focus(focus));
                owner.FocusReset = Timer(FocusResetTimer(focus));
                owner.MaxFocusBonus = Resource(focus.MaxFocusBonus);
            }
            if (wounds != null)
            {
                owner.Resources |= ResourceWounds;
                owner.WoundBuffer = Resource(WoundDamageBuffer(wounds));
                owner.WoundReset = Timer(WoundResetTimer(wounds));
                if (wounds.StatusEffects != null && wounds.StatusEffects.Length > 0)
                    owner.WoundCount = Counter(stats.CountStatusEffects(wounds.StatusEffects[0].Tag));
            }
            if (chanter != null)
            {
                owner.Resources |= ResourceChanter;
                owner.Phrases = Counter(PhraseCount(chanter));
                owner.PhrasesMax = Counter(PhraseCountMax(chanter));
                Chant chant = chanter.Chant;
                if (chant != null && index.TryGetKey(chant, scanned, out AbilityKey current)) owner.CurrentChant = current;
                chant = chanter.DesiredChant;
                if (chant != null && index.TryGetKey(chant, scanned, out AbilityKey desired)) owner.DesiredChant = desired;
            }
            return owner;
        }

        private static AbilityState CaptureAbility(GenericAbility ability, AbilityKey key)
        {
            bool ready = ability.Ready;
            int uses = ability.UsesLeft();
            var state = new AbilityState
            {
                Key = key,
                CooldownCounter = Counter(CooldownCounter(ability)),
                UsesLeft = uses == int.MaxValue || uses > MaxCounter ? -1 : Counter(uses),
                NotReady = (int)(ready ? 0 : ability.WhyNotReady) & AllNotReadyBits,
                PerEncounterReset = Timer(PerEncounterResetTimer(ability)),
                ModalCooldown = Timer(RezModalCooldown(ability)),
            };
            if (Activated(ability)) state.Flags |= AbilityActivated;
            if (UiActivated(ability)) state.Flags |= AbilityUiActivated;
            if (Applied(ability)) state.Flags |= AbilityApplied;
            if (ActivatedLaunching(ability)) state.Flags |= AbilityActivatedLaunching;
            if (StatusEffectsNeeded(ability)) state.Flags |= AbilityStatusEffectsNeeded;
            if (StatusEffectsActivated(ability)) state.Flags |= AbilityStatusEffectsActivated;

            if (ability is Chant chant)
            {
                state.Kind = KindChant;
                state.ChantTimer = Timer(ChantTimer(chant));
                state.ChantDelay = Timer(ChantDelayTimer(chant));
                state.ChantStartupDelay = Timer(ChantStartupDelayTimer(chant));
                state.CurrentPhrase = Mathf.Clamp(ChantCurrentPhrase(chant), -1, MaxPhraseIndex);
            }
            else if (ability is Spiritshift spiritshift)
            {
                state.Kind = KindSpiritshift;
                state.SpiritshiftActive = Timer(SpiritshiftActiveTimer(spiritshift));
                state.SpiritshiftCommentDelay = Timer(SpiritshiftCommentDelay(spiritshift));
            }
            return state;
        }

        /// <summary>
        /// Writes one owner and returns the number of ability records written. With
        /// <paramref name="last"/> (same ability keys) only records that differ from it are
        /// written; the others keep their last sent value so small changes still add up.
        /// </summary>
        private static int WriteOwner(MessageWriter message, OwnerState owner, OwnerState last)
        {
            message.Guid(owner.Id).Byte(last != null ? OwnerAbilityDelta : (byte)0)
                .Float(owner.Recovery).Float(owner.TotalRecovery).Float(owner.GrimoireCooldown)
                .Byte(SpellLevelCount);
            for (int i = 0; i < SpellLevelCount; i++) message.Int(owner.SpellCasts[i]);
            message.Byte(ModalCooldownCount);
            for (int i = 0; i < ModalCooldownCount; i++) message.Float(owner.ModalCooldowns[i]);
            message.Byte(owner.Resources);
            if ((owner.Resources & ResourceFocus) != 0)
                message.Float(owner.Focus).Float(owner.FocusReset).Float(owner.MaxFocusBonus);
            if ((owner.Resources & ResourceWounds) != 0)
                message.Int(owner.WoundCount).Float(owner.WoundBuffer).Float(owner.WoundReset);
            if ((owner.Resources & ResourceChanter) != 0)
            {
                message.Int(owner.Phrases).Int(owner.PhrasesMax);
                WriteOptionalKey(message, owner.CurrentChant);
                WriteOptionalKey(message, owner.DesiredChant);
            }
            List<AbilityState> abilities = owner.Abilities;
            if (last == null)
            {
                message.Int(abilities.Count);
                for (int i = 0; i < abilities.Count; i++) WriteAbility(message, abilities[i]);
                return abilities.Count;
            }

            int changed = 0;
            for (int i = 0; i < abilities.Count; i++)
                if (!Same(last.Abilities[i], abilities[i])) changed++;
            message.Int(changed);
            for (int i = 0; i < abilities.Count; i++)
            {
                if (Same(last.Abilities[i], abilities[i])) abilities[i] = last.Abilities[i];
                else WriteAbility(message, abilities[i]);
            }
            return changed;
        }

        private static void WriteOptionalKey(MessageWriter message, AbilityKey? key)
        {
            message.Byte((byte)(key.HasValue ? 1 : 0));
            if (key.HasValue) WriteKey(message, key.Value);
        }

        private static void WriteKey(MessageWriter message, AbilityKey key)
        {
            message.String(key.Name).Int(key.DisplayId).Int(key.Occurrence);
        }

        private static void WriteAbility(MessageWriter message, AbilityState ability)
        {
            WriteKey(message, ability.Key);
            message.Int(ability.CooldownCounter).Int(ability.UsesLeft).Int(ability.NotReady)
                .Float(ability.PerEncounterReset).Float(ability.ModalCooldown).Byte(ability.Flags).Byte(ability.Kind);
            if (ability.Kind == KindChant)
                message.Float(ability.ChantTimer).Float(ability.ChantDelay).Float(ability.ChantStartupDelay).Int(ability.CurrentPhrase);
            else if (ability.Kind == KindSpiritshift)
                message.Float(ability.SpiritshiftActive).Float(ability.SpiritshiftCommentDelay);
        }

        // ---- guest ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.AbilityStates || !_session.IsGuest) return;
            long started = PerfStats.Begin();
            Receive(reader);
            PerfStats.End("ability.update", started);
        }

        private void Receive(MessageReader reader)
        {
            int epoch = reader.Int();
            int revision = reader.Int();
            if (epoch < 0 || revision < 0) throw new FormatException("ability stream revision");
            byte fullValue = reader.Byte();
            if (fullValue > 1) throw new FormatException("ability full flag " + fullValue);
            bool full = fullValue != 0;
            if (epoch == _lastEpoch && revision <= _lastRevision) return;

            int ownerCount = reader.Int();
            if (ownerCount < 0 || ownerCount > MaxOwners) throw new FormatException("ability owner count " + ownerCount);
            _incoming.Clear();
            _incomingIds.Clear();
            int totalAbilities = 0;
            for (int i = 0; i < ownerCount; i++)
            {
                OwnerState owner = ReadOwner(reader, full, ref totalAbilities);
                if (!_incomingIds.Add(owner.Id)) throw new FormatException("duplicate ability owner");
                _incoming.Add(owner);
            }

            if (epoch != _lastEpoch)
            {
                ReleaseAll(_pending);
                _applied.Clear();
            }
            else if (full)
            {
                // Owners missing from a full snapshot are gone; the others stay so unchanged state need not rebuild the UI.
                _removed.Clear();
                foreach (Guid id in _pending.Keys)
                    if (!_incomingIds.Contains(id)) _removed.Add(id);
                for (int i = 0; i < _removed.Count; i++)
                {
                    ReleaseOwner(_pending[_removed[i]]);
                    _pending.Remove(_removed[i]);
                }
                _removed.Clear();
                _applied.Clear();
            }
            _lastEpoch = epoch;
            _lastRevision = revision;
            for (int i = 0; i < _incoming.Count; i++)
            {
                OwnerState owner = _incoming[i];
                PerfStats.Count("ability.records", owner.Abilities.Count);
                if (!_pending.TryGetValue(owner.Id, out OwnerState known))
                {
                    owner.UiDirty = true;
                    _pending[owner.Id] = owner;
                }
                else if (owner.AbilityDelta)
                {
                    MergeDelta(known, owner);
                    ReleaseOwner(owner);
                }
                else
                {
                    owner.UiDirty = known.UiDirty || !SameForUi(known, owner);
                    _pending[owner.Id] = owner;
                    ReleaseOwner(known);
                }
                _applied.Remove(owner.Id);
            }
            PerfStats.Count(full ? "ability.full" : "ability.delta");
            _incoming.Clear();
            _incomingIds.Clear();
            _retryUnresolvedAt = 0f;
        }

        /// <summary>Folds a delta owner into the complete state already held for it.</summary>
        private static void MergeDelta(OwnerState known, OwnerState delta)
        {
            bool dirty = !SameHeaderForUi(known, delta);
            known.CopyHeader(delta);
            List<AbilityState> abilities = known.Abilities;
            int next = 0;
            for (int i = 0; i < delta.Abilities.Count; i++)
            {
                AbilityState state = delta.Abilities[i];
                int at = -1;
                // Delta records keep the order of the full list, so the search resumes after the previous match.
                for (int n = 0; n < abilities.Count; n++)
                {
                    int candidate = next + n;
                    if (candidate >= abilities.Count) candidate -= abilities.Count;
                    if (!abilities[candidate].Key.Equals(state.Key)) continue;
                    at = candidate;
                    break;
                }
                if (at >= 0)
                {
                    if (!dirty && !SameForUi(abilities[at], state)) dirty = true;
                    abilities[at] = state;
                    next = at + 1 < abilities.Count ? at + 1 : 0;
                }
                else if (abilities.Count < MaxAbilitiesPerOwner)
                {
                    abilities.Add(state);
                    dirty = true;
                }
            }
            if (dirty) known.UiDirty = true;
        }

        private OwnerState ReadOwner(MessageReader reader, bool full, ref int totalAbilities)
        {
            OwnerState owner = RentOwner();
            owner.Id = reader.Guid();
            byte ownerFlags = reader.Byte();
            if ((ownerFlags & ~OwnerAbilityDelta) != 0 || (full && ownerFlags != 0)) throw new FormatException("ability owner flags");
            owner.AbilityDelta = ownerFlags != 0;
            owner.Recovery = ReadTimer(reader);
            owner.TotalRecovery = ReadTimer(reader);
            owner.GrimoireCooldown = ReadTimer(reader);
            int spellCount = reader.Byte();
            if (spellCount != SpellLevelCount) throw new FormatException("spell level count " + spellCount);
            for (int i = 0; i < SpellLevelCount; i++) owner.SpellCasts[i] = ReadCounter(reader, false);
            int modalCount = reader.Byte();
            if (modalCount != ModalCooldownCount) throw new FormatException("modal cooldown count " + modalCount);
            for (int i = 0; i < ModalCooldownCount; i++) owner.ModalCooldowns[i] = ReadTimer(reader);

            owner.Resources = reader.Byte();
            if ((owner.Resources & ~ResourceAll) != 0) throw new FormatException("ability resource flags");
            if ((owner.Resources & ResourceFocus) != 0)
            {
                owner.Focus = ReadResource(reader);
                owner.FocusReset = ReadTimer(reader);
                owner.MaxFocusBonus = ReadResource(reader);
            }
            if ((owner.Resources & ResourceWounds) != 0)
            {
                owner.WoundCount = ReadCounter(reader, false);
                owner.WoundBuffer = ReadResource(reader);
                owner.WoundReset = ReadTimer(reader);
            }
            if ((owner.Resources & ResourceChanter) != 0)
            {
                owner.Phrases = ReadCounter(reader, false);
                owner.PhrasesMax = ReadCounter(reader, false);
                owner.CurrentChant = ReadOptionalKey(reader);
                owner.DesiredChant = ReadOptionalKey(reader);
            }

            int abilityCount = reader.Int();
            if (abilityCount < 0 || abilityCount > MaxAbilitiesPerOwner || totalAbilities + abilityCount > MaxTotalAbilities)
                throw new FormatException("ability record count " + abilityCount);
            totalAbilities += abilityCount;
            _readKeys.Clear();
            for (int i = 0; i < abilityCount; i++)
            {
                AbilityState ability = ReadAbility(reader);
                if (!_readKeys.Add(ability.Key)) throw new FormatException("duplicate ability key");
                owner.Abilities.Add(ability);
            }
            _readKeys.Clear();
            return owner;
        }

        private static AbilityKey? ReadOptionalKey(MessageReader reader)
        {
            byte present = reader.Byte();
            if (present > 1) throw new FormatException("optional ability key flag");
            return present != 0 ? ReadKey(reader) : (AbilityKey?)null;
        }

        private static AbilityKey ReadKey(MessageReader reader)
        {
            string name = Protocol.Clean(reader.String(), MaxNameLength);
            if (name.Length == 0) throw new FormatException("empty ability name");
            int displayId = reader.Int();
            if (displayId < -1 || displayId > MaxDisplayStringId) throw new FormatException("ability display string id");
            int occurrence = reader.Int();
            if (occurrence < 0 || occurrence >= MaxAbilitiesPerOwner) throw new FormatException("ability occurrence");
            return new AbilityKey(name, displayId, occurrence);
        }

        private static AbilityState ReadAbility(MessageReader reader)
        {
            var ability = new AbilityState
            {
                Key = ReadKey(reader),
                CooldownCounter = ReadCounter(reader, false),
                UsesLeft = ReadCounter(reader, true),
                NotReady = reader.Int(),
                PerEncounterReset = ReadTimer(reader),
                ModalCooldown = ReadTimer(reader),
                Flags = reader.Byte(),
                Kind = reader.Byte(),
            };
            if ((ability.NotReady & ~AllNotReadyBits) != 0) throw new FormatException("ability not-ready mask");
            if ((ability.Flags & ~AbilityAllFlags) != 0) throw new FormatException("ability state flags");
            if (ability.Kind == KindChant)
            {
                ability.ChantTimer = ReadTimer(reader);
                ability.ChantDelay = ReadTimer(reader);
                ability.ChantStartupDelay = ReadTimer(reader);
                ability.CurrentPhrase = reader.Int();
                if (ability.CurrentPhrase < -1 || ability.CurrentPhrase > MaxPhraseIndex)
                    throw new FormatException("chant phrase index");
            }
            else if (ability.Kind == KindSpiritshift)
            {
                ability.SpiritshiftActive = ReadTimer(reader);
                ability.SpiritshiftCommentDelay = ReadTimer(reader);
            }
            else if (ability.Kind != 0) throw new FormatException("ability record kind " + ability.Kind);
            return ability;
        }

        private void GuestUpdate()
        {
            if (!Plugin.IsMirroring || _applied.Count >= _pending.Count) return;
            // Owners that cannot be resolved yet are retried on a short timer instead of every frame.
            if (_now < _retryUnresolvedAt && _retryUnresolvedAt - _now <= UnresolvedRetryInterval) return;

            long started = PerfStats.Begin();
            bool changed = false, unresolved = false;
            s_applyDepth++;
            try
            {
                foreach (var pair in _pending)
                {
                    if (_applied.Contains(pair.Key)) continue;
                    GameObject obj = ResolveObject(pair.Key);
                    CharacterStats stats = obj != null ? StatsFor(pair.Key, obj) : null;
                    if (stats == null)
                    {
                        unresolved = true;
                        continue;
                    }
                    _applied.Add(pair.Key);
                    try
                    {
                        ApplyOwner(stats, pair.Value);
                        // Buttons read timers and counters live; only a change the bar layout could depend on rebuilds it.
                        if (pair.Value.UiDirty) changed = true;
                        else PerfStats.Count("ability.skipped");
                        pair.Value.UiDirty = false;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Could not apply host ability state for " + obj.name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                s_applyDepth--;
            }
            _retryUnresolvedAt = unresolved ? _now + UnresolvedRetryInterval : 0f;
            if (changed && UIAbilityBar.Instance != null) UIAbilityBar.Instance.RefreshAbilities();
            PerfStats.End("ability.apply", started);
        }

        private void ApplyOwner(CharacterStats stats, OwnerState owner)
        {
            RecoveryTimer(stats) = owner.Recovery;
            TotalRecoveryTime(stats) = owner.TotalRecovery;
            stats.CurrentGrimoireCooldown = owner.GrimoireCooldown;
            int[] casts = SpellCastCounts(stats);
            if (casts == null || casts.Length < SpellLevelCount)
            {
                casts = new int[SpellLevelCount];
                SpellCastCounts(stats) = casts;
            }
            for (int i = 0; i < SpellLevelCount; i++) casts[i] = owner.SpellCasts[i];
            float[] modalTimers = ModalCooldownTimers(stats);
            if (modalTimers == null || modalTimers.Length < ModalCooldownCount)
            {
                modalTimers = new float[ModalCooldownCount];
                ModalCooldownTimers(stats) = modalTimers;
            }
            for (int i = 0; i < ModalCooldownCount; i++) modalTimers[i] = owner.ModalCooldowns[i];

            AbilityIndex index = IndexOf(owner.Id, stats);
            Dictionary<AbilityKey, GenericAbility> abilities = index.Lookup();
            FocusTrait focus = index.Focus;
            WoundsTrait wounds = index.Wounds;
            ChanterTrait chanter = index.Chanter;

            List<AbilityState> states = owner.Abilities;
            for (int i = 0; i < states.Count; i++)
            {
                AbilityState state = states[i];
                if (abilities.TryGetValue(state.Key, out GenericAbility ability)) ApplyAbility(ability, state);
            }
            if (focus != null && (owner.Resources & ResourceFocus) != 0)
            {
                Focus(focus) = owner.Focus;
                FocusResetTimer(focus) = owner.FocusReset;
                focus.MaxFocusBonus = owner.MaxFocusBonus;
            }
            if (wounds != null && (owner.Resources & ResourceWounds) != 0)
            {
                // Wound stacks are status effects; this stream only owns the trait's progress towards the next one.
                WoundDamageBuffer(wounds) = owner.WoundBuffer;
                WoundResetTimer(wounds) = owner.WoundReset;
            }
            if (chanter != null && (owner.Resources & ResourceChanter) != 0)
            {
                PhraseCount(chanter) = owner.Phrases;
                PhraseCountMax(chanter) = owner.PhrasesMax;
                CurrentChant(chanter) = ResolveChant(abilities, owner.CurrentChant);
                DesiredChant(chanter) = ResolveChant(abilities, owner.DesiredChant);
            }
        }

        private static Chant ResolveChant(Dictionary<AbilityKey, GenericAbility> abilities, AbilityKey? key)
        {
            return key.HasValue && abilities.TryGetValue(key.Value, out GenericAbility ability) ? ability as Chant : null;
        }

        private static void ApplyAbility(GenericAbility ability, AbilityState state)
        {
            CooldownCounter(ability) = state.CooldownCounter;
            PerEncounterResetTimer(ability) = state.PerEncounterReset;
            RezModalCooldown(ability) = state.ModalCooldown;
            NotReady(ability) = (GenericAbility.NotReadyValue)state.NotReady;
            Activated(ability) = (state.Flags & AbilityActivated) != 0;
            UiActivated(ability) = (state.Flags & AbilityUiActivated) != 0;
            Applied(ability) = (state.Flags & AbilityApplied) != 0;
            ActivatedLaunching(ability) = (state.Flags & AbilityActivatedLaunching) != 0;
            StatusEffectsNeeded(ability) = (state.Flags & AbilityStatusEffectsNeeded) != 0;
            StatusEffectsActivated(ability) = (state.Flags & AbilityStatusEffectsActivated) != 0;
            if (ability is Chant chant && state.Kind == KindChant)
            {
                ChantTimer(chant) = state.ChantTimer;
                ChantDelayTimer(chant) = state.ChantDelay;
                ChantStartupDelayTimer(chant) = state.ChantStartupDelay;
                ChantCurrentPhrase(chant) = state.CurrentPhrase;
            }
            else if (ability is Spiritshift spiritshift && state.Kind == KindSpiritshift)
            {
                SpiritshiftActiveTimer(spiritshift) = state.SpiritshiftActive;
                SpiritshiftCommentDelay(spiritshift) = state.SpiritshiftCommentDelay;
            }
        }

        // ---- shared ----

        private static Guid NetworkId(GameObject obj)
        {
            Guid id = EntityRegistry.NetworkId(obj);
            if (id != Guid.Empty) return id;
            InstanceID instance = obj.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static GameObject ResolveObject(Guid id)
        {
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj : InstanceID.GetObjectByID(id);
        }

        private CharacterStats StatsFor(Guid id, GameObject obj)
        {
            if (_indexes.TryGetValue(id, out AbilityIndex index) && ReferenceEquals(index.Object, obj) && index.Stats != null)
                return index.Stats;
            CharacterStats stats = obj.GetComponent<CharacterStats>();
            if (stats == null) return null;
            index = IndexEntry(id);
            index.Object = obj;
            if (!ReferenceEquals(index.Stats, stats))
            {
                index.Stats = stats;
                index.Built = false;
            }
            return stats;
        }

        private AbilityIndex IndexEntry(Guid id)
        {
            if (_indexes.TryGetValue(id, out AbilityIndex index)) return index;
            if (_indexes.Count >= MaxCachedOwners) _indexes.Clear();
            index = new AbilityIndex();
            _indexes.Add(id, index);
            return index;
        }

        /// <summary>
        /// Returns the owner's ability keys, rebuilt when the ability list no longer matches
        /// the cached one element for element and at least once per repair interval.
        /// </summary>
        private AbilityIndex IndexOf(Guid id, CharacterStats stats)
        {
            AbilityIndex index = IndexEntry(id);
            IList<GenericAbility> abilities = stats.ActiveAbilities;
            float age = _now - index.BuiltAt;
            if (index.Built && ReferenceEquals(index.Stats, stats) && age >= 0f && age < IndexLifetime && index.Matches(abilities))
                return index;

            index.Stats = stats;
            index.Reset();
            _occurrences.Clear();
            for (int i = 0; i < abilities.Count; i++)
            {
                GenericAbility ability = abilities[i];
                index.Abilities.Add(ability);
                if (ability == null)
                {
                    index.Keys.Add(default(AbilityKey));
                    continue;
                }
                if (index.Focus == null) index.Focus = ability as FocusTrait;
                if (index.Wounds == null) index.Wounds = ability as WoundsTrait;
                if (index.Chanter == null) index.Chanter = ability as ChanterTrait;
                string name = NormalizeName(ability);
                if (name.Length == 0)
                {
                    index.Keys.Add(new AbilityKey("", -1, 0));
                    continue;
                }
                int displayId = ability.DisplayName != null ? ability.DisplayName.StringID : -1;
                var baseKey = new AbilityKey(name, displayId, 0);
                _occurrences.TryGetValue(baseKey, out int occurrence);
                _occurrences[baseKey] = occurrence + 1;
                index.Keys.Add(new AbilityKey(name, displayId, occurrence));
            }
            _occurrences.Clear();
            index.BuiltAt = _now;
            index.Built = true;
            return index;
        }

        private OwnerState RentOwner()
        {
            if (_pool.Count == 0) return new OwnerState();
            OwnerState owner = _pool.Pop();
            owner.Reset();
            return owner;
        }

        private void ReleaseOwner(OwnerState owner)
        {
            if (_pool.Count < MaxPooledOwners) _pool.Push(owner);
        }

        private void ReleaseAll(Dictionary<Guid, OwnerState> owners)
        {
            foreach (var pair in owners) ReleaseOwner(pair.Value);
            owners.Clear();
        }

        private static string NormalizeName(GenericAbility ability)
        {
            return Protocol.Clean(ability.name.Replace("(Clone)", ""), MaxNameLength);
        }

        private static int Counter(int value) => Mathf.Clamp(value, 0, MaxCounter);
        private static float Timer(float value) => !IsFinite(value) || value <= 0f ? 0f : Mathf.Min(value, MaxTimer);
        private static float Resource(float value) => !IsFinite(value) ? 0f : Mathf.Clamp(value, -MaxResource, MaxResource);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Same(float a, float b) => Mathf.Abs(a - b) < 0.005f;

        private static int ReadCounter(MessageReader reader, bool unlimited)
        {
            int value = reader.Int();
            if ((unlimited && value == -1) || value >= 0 && value <= MaxCounter) return value;
            throw new FormatException("ability counter " + value);
        }

        private static float ReadTimer(MessageReader reader)
        {
            float value = reader.Float();
            if (!IsFinite(value) || value < 0f || value > MaxTimer) throw new FormatException("ability timer");
            return value;
        }

        private static float ReadResource(MessageReader reader)
        {
            float value = reader.Float();
            if (!IsFinite(value) || value < -MaxResource || value > MaxResource) throw new FormatException("ability resource");
            return value;
        }

        private static bool Same(OwnerState a, OwnerState b)
        {
            if (!Same(a.Recovery, b.Recovery) || !Same(a.TotalRecovery, b.TotalRecovery)
                || !Same(a.GrimoireCooldown, b.GrimoireCooldown) || a.Resources != b.Resources
                || a.Focus != b.Focus || a.FocusReset != b.FocusReset || a.MaxFocusBonus != b.MaxFocusBonus
                || a.WoundCount != b.WoundCount || a.WoundBuffer != b.WoundBuffer || a.WoundReset != b.WoundReset
                || a.Phrases != b.Phrases || a.PhrasesMax != b.PhrasesMax
                || !Nullable.Equals(a.CurrentChant, b.CurrentChant) || !Nullable.Equals(a.DesiredChant, b.DesiredChant)
                || a.Abilities.Count != b.Abilities.Count) return false;
            for (int i = 0; i < SpellLevelCount; i++) if (a.SpellCasts[i] != b.SpellCasts[i]) return false;
            for (int i = 0; i < ModalCooldownCount; i++) if (!Same(a.ModalCooldowns[i], b.ModalCooldowns[i])) return false;
            for (int i = 0; i < a.Abilities.Count; i++) if (!Same(a.Abilities[i], b.Abilities[i])) return false;
            return true;
        }

        private static bool SameKeys(OwnerState a, OwnerState b)
        {
            if (a.Abilities.Count != b.Abilities.Count) return false;
            for (int i = 0; i < a.Abilities.Count; i++) if (!a.Abilities[i].Key.Equals(b.Abilities[i].Key)) return false;
            return true;
        }

        // The ability bar reads timers, counters and readiness from the abilities every frame.
        // These comparisons cover everything else: all discrete state and whether a timer is running.
        private static bool SameForUi(OwnerState a, OwnerState b)
        {
            if (!SameHeaderForUi(a, b) || a.Abilities.Count != b.Abilities.Count) return false;
            for (int i = 0; i < a.Abilities.Count; i++) if (!SameForUi(a.Abilities[i], b.Abilities[i])) return false;
            return true;
        }

        private static bool SameHeaderForUi(OwnerState a, OwnerState b)
        {
            if (!SameRunning(a.Recovery, b.Recovery) || !SameRunning(a.GrimoireCooldown, b.GrimoireCooldown)
                || a.Resources != b.Resources || a.MaxFocusBonus != b.MaxFocusBonus
                || !SameRunning(a.FocusReset, b.FocusReset) || !SameRunning(a.WoundReset, b.WoundReset)
                || a.WoundCount != b.WoundCount || a.Phrases != b.Phrases || a.PhrasesMax != b.PhrasesMax
                || !Nullable.Equals(a.CurrentChant, b.CurrentChant) || !Nullable.Equals(a.DesiredChant, b.DesiredChant)) return false;
            for (int i = 0; i < SpellLevelCount; i++) if (a.SpellCasts[i] != b.SpellCasts[i]) return false;
            for (int i = 0; i < ModalCooldownCount; i++) if (!SameRunning(a.ModalCooldowns[i], b.ModalCooldowns[i])) return false;
            return true;
        }

        private static bool SameForUi(AbilityState a, AbilityState b)
        {
            return a.Key.Equals(b.Key) && a.CooldownCounter == b.CooldownCounter && a.UsesLeft == b.UsesLeft
                && a.NotReady == b.NotReady && a.Flags == b.Flags && a.Kind == b.Kind && a.CurrentPhrase == b.CurrentPhrase
                && SameRunning(a.PerEncounterReset, b.PerEncounterReset) && SameRunning(a.ModalCooldown, b.ModalCooldown)
                && SameRunning(a.ChantDelay, b.ChantDelay) && SameRunning(a.ChantStartupDelay, b.ChantStartupDelay)
                && SameRunning(a.SpiritshiftActive, b.SpiritshiftActive);
        }

        private static bool SameRunning(float a, float b) => (a > 0f) == (b > 0f);

        private static bool Same(AbilityState a, AbilityState b)
        {
            return a.Key.Equals(b.Key) && a.CooldownCounter == b.CooldownCounter && a.UsesLeft == b.UsesLeft
                && a.NotReady == b.NotReady && Same(a.PerEncounterReset, b.PerEncounterReset)
                && Same(a.ModalCooldown, b.ModalCooldown) && a.Flags == b.Flags && a.Kind == b.Kind
                && Same(a.ChantTimer, b.ChantTimer) && Same(a.ChantDelay, b.ChantDelay)
                && Same(a.ChantStartupDelay, b.ChantStartupDelay) && a.CurrentPhrase == b.CurrentPhrase
                && Same(a.SpiritshiftActive, b.SpiritshiftActive) && Same(a.SpiritshiftCommentDelay, b.SpiritshiftCommentDelay);
        }

        private struct AbilityKey : IEquatable<AbilityKey>
        {
            public readonly string Name;
            public readonly int DisplayId;
            public readonly int Occurrence;

            public AbilityKey(string name, int displayId, int occurrence)
            {
                Name = name;
                DisplayId = displayId;
                Occurrence = occurrence;
            }

            public bool Equals(AbilityKey other)
            {
                return DisplayId == other.DisplayId && Occurrence == other.Occurrence && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is AbilityKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Name != null ? Name.GetHashCode() : 0) * 397 ^ DisplayId) * 397 ^ Occurrence;
                }
            }
        }

        private struct AbilityState
        {
            public AbilityKey Key;
            public int CooldownCounter, UsesLeft, NotReady;
            public float PerEncounterReset, ModalCooldown;
            public byte Flags, Kind;
            public float ChantTimer, ChantDelay, ChantStartupDelay;
            public int CurrentPhrase;
            public float SpiritshiftActive, SpiritshiftCommentDelay;
        }

        private sealed class OwnerState
        {
            public Guid Id;
            public float Recovery, TotalRecovery, GrimoireCooldown;
            public readonly int[] SpellCasts = new int[SpellLevelCount];
            public readonly float[] ModalCooldowns = new float[ModalCooldownCount];
            public byte Resources;
            public float Focus, FocusReset, MaxFocusBonus;
            public int WoundCount;
            public float WoundBuffer, WoundReset;
            public int Phrases, PhrasesMax;
            public AbilityKey? CurrentChant, DesiredChant;
            public readonly List<AbilityState> Abilities = new List<AbilityState>();
            /// <summary>Guest: the ability list only holds the records that changed.</summary>
            public bool AbilityDelta;
            /// <summary>Guest: differs from the last applied state in a way the ability bar layout could depend on.</summary>
            public bool UiDirty;

            public void Reset()
            {
                Id = Guid.Empty;
                Recovery = TotalRecovery = GrimoireCooldown = 0f;
                Array.Clear(SpellCasts, 0, SpellCasts.Length);
                Array.Clear(ModalCooldowns, 0, ModalCooldowns.Length);
                Resources = 0;
                Focus = FocusReset = MaxFocusBonus = 0f;
                WoundCount = 0;
                WoundBuffer = WoundReset = 0f;
                Phrases = PhrasesMax = 0;
                CurrentChant = DesiredChant = null;
                Abilities.Clear();
                AbilityDelta = false;
                UiDirty = false;
            }

            /// <summary>Copies everything except the ability list and the guest bookkeeping.</summary>
            public void CopyHeader(OwnerState other)
            {
                Recovery = other.Recovery;
                TotalRecovery = other.TotalRecovery;
                GrimoireCooldown = other.GrimoireCooldown;
                Array.Copy(other.SpellCasts, SpellCasts, SpellCasts.Length);
                Array.Copy(other.ModalCooldowns, ModalCooldowns, ModalCooldowns.Length);
                Resources = other.Resources;
                Focus = other.Focus;
                FocusReset = other.FocusReset;
                MaxFocusBonus = other.MaxFocusBonus;
                WoundCount = other.WoundCount;
                WoundBuffer = other.WoundBuffer;
                WoundReset = other.WoundReset;
                Phrases = other.Phrases;
                PhrasesMax = other.PhrasesMax;
                CurrentChant = other.CurrentChant;
                DesiredChant = other.DesiredChant;
            }
        }

        /// <summary>
        /// One owner's ability list with the stream key of every entry, so names are not
        /// re-read from the engine each tick. A null key name marks a missing ability and an
        /// empty one an ability that cannot be keyed.
        /// </summary>
        private sealed class AbilityIndex
        {
            public GameObject Object;
            public CharacterStats Stats;
            public bool Built;
            public float BuiltAt;
            public readonly List<GenericAbility> Abilities = new List<GenericAbility>();
            public readonly List<AbilityKey> Keys = new List<AbilityKey>();
            public FocusTrait Focus;
            public WoundsTrait Wounds;
            public ChanterTrait Chanter;
            private readonly Dictionary<AbilityKey, GenericAbility> _lookup = new Dictionary<AbilityKey, GenericAbility>();
            private bool _lookupBuilt;

            public void Reset()
            {
                Built = false;
                Abilities.Clear();
                Keys.Clear();
                Focus = null;
                Wounds = null;
                Chanter = null;
                _lookup.Clear();
                _lookupBuilt = false;
            }

            public bool Matches(IList<GenericAbility> abilities)
            {
                if (abilities.Count != Abilities.Count) return false;
                for (int i = 0; i < Abilities.Count; i++)
                {
                    GenericAbility ability = abilities[i];
                    if (!ReferenceEquals(ability, Abilities[i]) || (ability == null) != (Keys[i].Name == null)) return false;
                }
                return true;
            }

            public bool TryGetKey(GenericAbility ability, int scanned, out AbilityKey key)
            {
                for (int i = 0; i < scanned && i < Abilities.Count; i++)
                {
                    if (!ReferenceEquals(Abilities[i], ability) || string.IsNullOrEmpty(Keys[i].Name)) continue;
                    key = Keys[i];
                    return true;
                }
                key = default(AbilityKey);
                return false;
            }

            public Dictionary<AbilityKey, GenericAbility> Lookup()
            {
                if (_lookupBuilt) return _lookup;
                for (int i = 0; i < Abilities.Count; i++)
                    if (!string.IsNullOrEmpty(Keys[i].Name)) _lookup[Keys[i]] = Abilities[i];
                _lookupBuilt = true;
                return _lookup;
            }
        }
    }
}
