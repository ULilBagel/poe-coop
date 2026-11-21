using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// One host-owned transaction for camp, rest-zone, inn and scripted rest.
    /// Guests replay only the native UI. Supplies, currency, world time and rest
    /// effects are changed by the single native host call; the regular state
    /// synchronizers replicate their results.
    /// </summary>
    public sealed class RestSync : IDisposable
    {
        public const FlowDomain Domain = FlowDomain.Rest;
        private const int FullRefreshMilliseconds = 2000;
        private const float FullRefreshInterval = FullRefreshMilliseconds / 1000f;
        private const float GuestRetryInterval = 0.25f;
        private const int MaxPartyRecords = 6;
        private const int MaxCachedRequestsPerPlayer = 256;
        private const int MaxSurvivalIndex = 4096;
        private const int MaxSupplies = 99;
        private const int MaxRooms = 256;
        private const int MaxStateBytes = 16 * 1024;
        private const float MaxCurrency = 1000000000f;
        private const int CannotCampResultBase = 100;

        private enum CommandKind : byte
        {
            OpenCamp = 1,
            Confirm = 2,
            Cancel = 3,
            SelectSurvival = 4,
            OpenStash = 5,
            StashFinished = 6,
            ConfirmInn = 7,
            ConfirmScripted = 8,
            Resync = 9,
        }

        private enum Phase : byte { Closed = 0, Prompt = 1, Stash = 2, Resting = 3, Completed = 4, Cancelled = 5 }
        private enum Source : byte { Camp = 0, Zone = 1, Inn = 2, Scripted = 3 }

        private enum Result : byte
        {
            Accepted = (int)FlowResultCode.Ok,
            Rejected = (int)FlowResultCode.Rejected,
            WrongEpoch = (int)FlowResultCode.StaleWorld,
            NotReady = (int)FlowResultCode.NotReady,
            InvalidSelection = (int)FlowResultCode.Invalid,
            StaleTransaction = (int)FlowResultCode.Conflict,
            RequestConflict = (int)FlowResultCode.Conflict,
            NativeFailure = (int)FlowResultCode.Failed,
            Busy = 20,
            NotInitiator = 21,
            NotOwner = 22,
            CannotPurchase = 23,
            ZoneUnavailable = 24,
            FreeRestAvailable = 25,
        }

        private static readonly AccessTools.FieldRef<CharacterStats, CharacterStats.FatigueLevel> Fatigue =
            AccessTools.FieldRefAccess<CharacterStats, CharacterStats.FatigueLevel>("m_CurrentFatigueLevel");
        private static readonly FieldInfo OverrideMovie = AccessTools.Field(typeof(RestZone), "m_overrideMovie");
        private static readonly AccessTools.FieldRef<UIRestConfirmBox, UIRestBoxPartyMember[]> RestPartyWidgets =
            AccessTools.FieldRefAccess<UIRestConfirmBox, UIRestBoxPartyMember[]>("m_PartyMemberObjects");

        private static RestSync s_instance;
        internal static RestSync Instance => s_instance;
        private static int s_nativeRestDepth;
        private static int s_presentationDepth;

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<int, RequestCache> _requests = new Dictionary<int, RequestCache>();
        private readonly HashSet<Guid> _partyApplied = new HashSet<Guid>();

        private long _generation;
        private bool _wasHosting, _wasJoined, _disposed;
        private bool _externalWorldContext;
        private bool? _worldReadyOverride;
        private long _worldEpoch;
        private long _stateSeq;
        private int _nextTransactionId = 1;
        private long _nextRequestId = 1;
        private Transaction _transaction;
        private Snapshot _snapshot;
        private bool _hasSnapshot;
        private bool _needsResync;
        private float _nextFullRefresh;
        private float _nextGuestRetry;
        private bool _sequenceExhaustedLogged;

        private int _presentedPrompt;
        private int _presentedPromptRevision = -1;
        private UIMessageBox _scriptedPrompt;
        private int _presentedStash;
        private int _stashFinishSent;
        private bool _stashClosed;
        private int _presentedMovie;

        private struct Command
        {
            public long WorldEpoch, RequestId;
            public int Transaction, Revision;
            public CommandKind Kind;
            public Source Source;
            public Guid SourceId;
            public Guid Actor;
            public int RoomIndex, Bonus, SubBonus;
            public bool OpenOnly;

            public bool Same(Command other)
            {
                return WorldEpoch == other.WorldEpoch && Kind == other.Kind && Transaction == other.Transaction
                    && Revision == other.Revision && Source == other.Source && SourceId == other.SourceId
                    && Actor == other.Actor && RoomIndex == other.RoomIndex && Bonus == other.Bonus
                    && SubBonus == other.SubBonus && OpenOnly == other.OpenOnly;
            }
        }

        private sealed class CachedRequest
        {
            public Command Command;
            public int Result;
            public long StateSeq;
        }

        private sealed class RequestCache
        {
            public readonly Dictionary<long, CachedRequest> ById = new Dictionary<long, CachedRequest>();
            public readonly Queue<long> Order = new Queue<long>();

            public void Add(long id, Command command, int result, long stateSeq)
            {
                if (ById.ContainsKey(id)) return;
                ById.Add(id, new CachedRequest { Command = command, Result = result, StateSeq = stateSeq });
                Order.Enqueue(id);
                while (Order.Count > MaxCachedRequestsPerPlayer)
                    ById.Remove(Order.Dequeue());
            }
        }

        private sealed class Transaction
        {
            public int Id, Revision, Initiator, RoomIndex;
            public Source Source;
            public RestZone.Mode Mode;
            public RestMovieMode RequestedMovie, Movie;
            public Guid SourceId;
            public Phase Phase;
            public bool Committed;
            public int StartTime = -1, EndTime = -1, AdvancedSeconds;
        }

        private sealed class PartyState
        {
            public Guid Id;
            public CharacterStats.FatigueLevel Fatigue;
            public int Bonus, SubBonus;
        }

        private sealed class Snapshot
        {
            public long Epoch, StateSeq;
            public int Transaction, Revision, Initiator, RoomIndex;
            public Phase Phase;
            public Source Source;
            public RestZone.Mode Mode;
            public RestMovieMode Movie;
            public Guid SourceId;
            public int Supplies;
            public float Currency;
            public int StartTime = -1, EndTime = -1, AdvancedSeconds;
            public readonly List<PartyState> Party = new List<PartyState>(MaxPartyRecords);

            public bool Prompt => Phase == Phase.Prompt;
            public bool Stash => Phase == Phase.Stash;
            public bool Resting => Phase == Phase.Resting;
            public bool Completed => Phase == Phase.Completed;
            public bool Cancelled => Phase == Phase.Cancelled;
        }

        private sealed class NativeRestScope : IDisposable
        {
            public NativeRestScope() { s_nativeRestDepth++; }
            public void Dispose() { s_nativeRestDepth--; }
        }

        private sealed class PresentationScope : IDisposable
        {
            public PresentationScope() { s_presentationDepth++; }
            public void Dispose() { s_presentationDepth--; }
        }

        public RestSync(Session session, Ownership ownership)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            _generation = session.Generation;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.WorldStateReady += OnWorldStateReady;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
            SyncSession();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.WorldStateReady -= OnWorldStateReady;
            _session.FullStateRequested -= OnFullStateRequested;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            ResetPresentation();
            _requests.Clear();
            _partyApplied.Clear();
            _transaction = null;
            _snapshot = null;
            if (s_instance == this)
            {
                s_instance = null;
                s_nativeRestDepth = 0;
                s_presentationDepth = 0;
            }
        }

        /// <summary>World context supplied by FlowCoordinator. Level events provide a local fallback.</summary>
        public static void SetWorldContext(long worldEpoch, bool ready)
        {
            if (worldEpoch < 0) throw new ArgumentOutOfRangeException(nameof(worldEpoch));
            RestSync self = s_instance;
            if (self == null || self._disposed) return;
            self._externalWorldContext = true;
            bool changed = self._worldEpoch != worldEpoch;
            if (changed) self.ResetWorld(advanceEpoch: false);
            self._worldEpoch = worldEpoch;
            self._worldReadyOverride = ready;
            if (self._session.IsGuest && ready) self._needsResync = true;
            if (self._session.IsHost && changed) self.Publish();
        }

        public static long CurrentWorldEpoch => s_instance != null ? s_instance._worldEpoch : 0;
        public static long CurrentStateSequence => s_instance != null ? s_instance._stateSeq : 0;

        public static void SendLatestTo(int playerId)
        {
            RestSync self = s_instance;
            if (self == null) return;
            self.SyncContext();
            if (self._session.IsHost && self._worldEpoch > 0) self.Publish(playerId);
        }
        public static bool HasActiveTransaction => s_instance != null && s_instance._transaction != null
            && (s_instance._transaction.Phase == Phase.Prompt || s_instance._transaction.Phase == Phase.Stash
                || s_instance._transaction.Phase == Phase.Resting);
        public static int ActiveTransactionInitiator => HasActiveTransaction ? s_instance._transaction.Initiator : -1;

        public static void RequestResync()
        {
            RestSync self = s_instance;
            if (self != null) self.Issue(self.NewCommand(CommandKind.Resync));
        }

        internal static bool InSession => s_instance != null && (s_instance._session.IsHost || s_instance._session.IsGuest);
        internal static bool NativeHostRest => s_nativeRestDepth > 0;
        internal static bool Presenting => s_presentationDepth > 0;

        internal static void OpenCampNative()
        {
            RestSync self = s_instance;
            if (self == null) return;
            Command command = self.NewCommand(CommandKind.OpenCamp);
            command.Transaction = 0;
            command.Revision = 0;
            command.Source = Source.Camp;
            self.Issue(command);
        }

        internal static void OpenZoneNative(RestInteraction interaction)
        {
            RestSync self = s_instance;
            if (self == null) return;
            Command command = self.NewCommand(CommandKind.OpenCamp);
            command.Transaction = 0;
            command.Revision = 0;
            command.Source = Source.Zone;
            command.SourceId = NetworkId(interaction != null ? interaction.gameObject : null);
            self.Issue(command);
        }

        internal static void OpenScriptedNative(RestMovieMode movie)
        {
            RestSync self = s_instance;
            if (self == null || !self._session.IsHost) return;
            Command command = self.NewCommand(CommandKind.ConfirmScripted);
            command.Source = Source.Scripted;
            command.RoomIndex = (int)movie;
            command.OpenOnly = true;
            self.Issue(command);
        }

        internal static void OpenInnNative(Inn inn, Inn.InnRoom room)
        {
            RestSync self = s_instance;
            if (self == null) return;
            Guid innId = InnId(inn);
            if (innId == Guid.Empty && !IsBrighthollow(inn))
            {
                self._session.Note("This inn has no shared network identity.");
                return;
            }
            Command command = self.NewCommand(CommandKind.ConfirmInn);
            command.Transaction = 0;
            command.Revision = 0;
            command.Source = Source.Inn;
            command.SourceId = innId;
            command.RoomIndex = RoomIndex(inn, room);
            command.OpenOnly = true;
            self.Issue(command);
        }

        internal static void CampNative()
        {
            RestSync self = s_instance;
            if (self == null) return;
            if (self._transaction != null && self._transaction.Source == Source.Camp
                && self._transaction.Phase == Phase.Prompt) self.Issue(self.NewCommand(CommandKind.Confirm));
            else OpenCampNative();
        }

        internal static void RestNative(RestZone.Mode mode)
        {
            RestSync self = s_instance;
            if (self == null || !self._session.IsHost) return;
            if (mode == RestZone.Mode.Scripted) OpenScriptedNative(RestMovieMode.None);
        }

        internal static void PurchaseRoomNative(Inn inn, Inn.InnRoom room)
        {
            RestSync self = s_instance;
            if (self == null) return;
            if (InnId(inn) == Guid.Empty && !IsBrighthollow(inn))
            {
                self._session.Note("This inn has no shared network identity.");
                return;
            }
            if (self._transaction != null && self._transaction.Source == Source.Inn
                && self._transaction.Phase == Phase.Prompt)
            {
                Command confirm = self.NewCommand(CommandKind.ConfirmInn);
                confirm.Source = Source.Inn;
                confirm.SourceId = InnId(inn);
                confirm.RoomIndex = RoomIndex(inn, room);
                self.Issue(confirm);
            }
            else OpenInnNative(inn, room);
        }

        internal static void ConfirmBoxNative(UIRestConfirmBox.Result result)
        {
            RestSync self = s_instance;
            if (self != null) self.OnConfirmBox(result);
        }

        internal static void ScriptedPromptNative(UIMessageBox.Result result)
        {
            RestSync self = s_instance;
            if (self != null) self.OnScriptedPrompt(result, null);
        }

        internal static void StashFinishedNative()
        {
            RestSync self = s_instance;
            if (self != null) self.Issue(self.NewCommand(CommandKind.StashFinished));
        }

        internal static void SelectSurvivalNative(CharacterStats stats, object selected)
        {
            RestSync self = s_instance;
            if (self == null || Presenting) return;
            int bonus = -1, subBonus = -1;
            Affliction affliction = selected as Affliction;
            if (affliction == null && selected is CampEffectSubBonus sub) affliction = sub.Affliction;
            if (affliction != null && AfflictionData.Instance != null && AfflictionData.Instance.SurvivalCampEffects != null)
            {
                bonus = AfflictionData.Instance.SurvivalCampEffects.IndexOf(affliction);
                subBonus = AfflictionData.Instance.SurvivalCampEffects.SubIndexOf(affliction);
                if (self._session.IsGuest) self.RegisterSurvivalAffliction(affliction);
            }
            Command command = self.NewCommand(CommandKind.SelectSurvival);
            command.Actor = NetworkId(stats != null ? stats.gameObject : null);
            command.Bonus = bonus;
            command.SubBonus = subBonus;
            self.Issue(command);
        }

        internal static bool PreserveSurvivalSelection(CharacterStats stats, out int[] values)
        {
            values = null;
            RestSync self = s_instance;
            if (self == null || stats == null || !InSession || self._presentedPrompt == 0) return false;
            values = new[] { stats.LastSelectedSurvivalBonus, stats.LastSelectedSurvivalSubBonus };
            return true;
        }

        public void Update(float now)
        {
            if (_disposed) return;
            SyncContext();
            if (_session.IsHost)
            {
                if (_transaction != null && (_transaction.Phase == Phase.Prompt || _transaction.Phase == Phase.Stash)
                    && GameState.InCombat) CancelTransaction();
                if (now >= _nextFullRefresh)
                {
                    _nextFullRefresh = now + FullRefreshInterval;
                    Publish();
                }
                if (_snapshot != null)
                {
                    CheckStashClosed();
                    if (now >= _nextGuestRetry)
                    {
                        _nextGuestRetry = now + GuestRetryInterval;
                        Present(_snapshot);
                    }
                }
                return;
            }
            if (!_session.IsGuest) return;
            if (_needsResync && Ready())
            {
                _needsResync = false;
                RequestResync();
            }
            if (now < _nextGuestRetry) return;
            _nextGuestRetry = now + GuestRetryInterval;
            if (_snapshot != null)
            {
                ApplyParty(_snapshot);
                Present(_snapshot);
                CheckStashClosed();
            }
        }

        private void SyncSession()
        {
            if (_generation == _session.Generation && _wasHosting == _session.IsHost && _wasJoined == _session.IsGuest) return;
            _generation = _session.Generation;
            _wasHosting = _session.IsHost;
            _wasJoined = _session.IsGuest;
            _requests.Clear();
            _nextRequestId = 1;
            _nextTransactionId = 1;
            _externalWorldContext = false;
            _worldReadyOverride = null;
            _worldEpoch = _session.IsHost ? 1 : 0;
            _nextFullRefresh = 0f;
            _nextGuestRetry = 0f;
            ResetWorld(advanceEpoch: false);
        }

        private void SyncContext()
        {
            SyncSession();
            if (!FlowCoordinator.IsInitialized) return;
            _externalWorldContext = true;
            long epoch = FlowCoordinator.WorldEpoch;
            if (_worldEpoch != epoch)
            {
                ResetWorld(advanceEpoch: false);
                _worldEpoch = epoch;
            }
            _worldReadyOverride = _session.IsWorldReady && !FlowCoordinator.ApplyingWorldBarrier;
        }

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            SyncContext();
            if (worldEpoch != _worldEpoch) return;
            if (_session.IsHost && player != null) Publish(player.Id);
            else if (_session.IsGuest)
            {
                _needsResync = true;
                if (_snapshot != null) Present(_snapshot);
            }
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            SyncContext();
            if (_session.IsHost && worldEpoch == _worldEpoch) Publish(playerId);
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            if (_session.IsHost)
            {
                ResetWorld(advanceEpoch: !_externalWorldContext);
                Publish();
            }
            else if (_session.IsGuest)
            {
                ResetWorld(advanceEpoch: false);
                _needsResync = true;
            }
        }

        private void ResetWorld(bool advanceEpoch)
        {
            ResetPresentation();
            if (advanceEpoch) _worldEpoch = NextPositive(_worldEpoch);
            _stateSeq = 0;
            _sequenceExhaustedLogged = false;
            _requests.Clear();
            _nextRequestId = 1;
            _nextTransactionId = 1;
            _transaction = null;
            _snapshot = null;
            _hasSnapshot = false;
            _needsResync = _session.IsGuest;
            _partyApplied.Clear();
            _nextFullRefresh = 0f;
            _nextGuestRetry = 0f;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost && !player.IsHost) Publish(player.Id);
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            _requests.Remove(player.Id);
            if (_session.IsHost && _transaction != null && HasActiveTransaction
                && _transaction.Initiator == player.Id) CancelTransaction();
        }

        private bool Ready()
        {
            if (_worldEpoch <= 0 || _worldReadyOverride == false || !_session.IsWorldReady
                || FlowCoordinator.ApplyingWorldBarrier) return false;
            if (!GameState.IsInGameSession || GameState.IsLoading || GameState.IsInMainMenu
                || GameState.Instance == null || WorldTime.Instance == null || WorldTime.Instance.CurrentTime == null
                || GameState.s_playerCharacter == null) return false;
            return !_session.IsGuest || Plugin.IsMirroring;
        }

        private Command NewCommand(CommandKind kind)
        {
            SyncContext();
            Transaction transaction = _transaction;
            Snapshot snapshot = _session.IsGuest ? _snapshot : null;
            Command command = new Command
            {
                WorldEpoch = _worldEpoch,
                RequestId = NextRequestId(),
                Kind = kind,
                Transaction = transaction != null ? transaction.Id : snapshot != null ? snapshot.Transaction : 0,
                Revision = transaction != null ? transaction.Revision : snapshot != null ? snapshot.Revision : 0,
                Source = transaction != null ? transaction.Source : snapshot != null ? snapshot.Source : Source.Camp,
                SourceId = transaction != null ? transaction.SourceId : snapshot != null ? snapshot.SourceId : Guid.Empty,
                RoomIndex = transaction != null ? transaction.RoomIndex : snapshot != null ? snapshot.RoomIndex : -1,
                Bonus = -1,
                SubBonus = -1,
            };
            if (kind == CommandKind.Resync)
            {
                command.Transaction = 0;
                command.Revision = 0;
            }
            return command;
        }

        private long NextRequestId()
        {
            long id = _nextRequestId;
            _nextRequestId = _nextRequestId == long.MaxValue ? 1 : _nextRequestId + 1;
            return id;
        }

        private void Issue(Command command)
        {
            if (_disposed || command.RequestId <= 0) return;
            if (_session.IsHost)
            {
                HandleCommand(Session.HostId, command);
                return;
            }
            if (!_session.IsGuest || (command.Kind != CommandKind.Resync && !_hasSnapshot)) return;
            MessageWriter message = FlowWire.WriteCommand(command.WorldEpoch, command.RequestId, Domain, (int)command.Kind)
                .Int(command.Transaction).Int(command.Revision);
            switch (command.Kind)
            {
                case CommandKind.OpenCamp:
                    message.Byte((byte)command.Source);
                    if (command.Source == Source.Zone) message.Guid(command.SourceId);
                    break;
                case CommandKind.SelectSurvival:
                    message.Guid(command.Actor).Int(command.Bonus).Int(command.SubBonus);
                    break;
                case CommandKind.ConfirmInn:
                    message.Byte((byte)(command.OpenOnly ? 1 : 0)).Guid(command.SourceId).Int(command.RoomIndex);
                    break;
                case CommandKind.ConfirmScripted:
                    if (command.Transaction == 0)
                        message.Byte((byte)(command.OpenOnly ? 1 : 0)).Int(command.RoomIndex);
                    break;
            }
            _session.SendToHost(message.ToArray());
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (_disposed || reader.Type != MessageType.RestState) return;
            if (sender != null || !_session.IsGuest) throw new FormatException("unexpected rest state direction");
            SyncContext();
            ReceiveSnapshot(reader);
        }

        /// <summary>Called by FlowCoordinator after it parses a Rest command envelope.</summary>
        public void HandleFlowCommand(PlayerInfo sender, long worldEpoch, long requestId, int kind, MessageReader reader)
        {
            if (!_session.IsHost || sender == null || sender.IsHost) throw new FormatException("rest command sender");
            SyncContext();
            Command command = ReadCommand(worldEpoch, requestId, kind, reader);
            HandleCommand(sender.Id, command);
        }

        /// <summary>Called by FlowCoordinator after it parses a Rest result envelope.</summary>
        public void HandleFlowResult(long worldEpoch, long requestId, int kind, FlowResultCode result, long stateSeq)
        {
            SyncContext();
            if (!_session.IsGuest || requestId <= 0 || stateSeq < 0
                || kind < (int)CommandKind.OpenCamp || kind > (int)CommandKind.Resync
                || !ValidResult((int)result)) throw new FormatException("invalid rest flow result");
            if (worldEpoch == _worldEpoch) PresentResult((int)result, (CommandKind)kind);
        }

        private static Command ReadCommand(long worldEpoch, long requestId, int kind, MessageReader reader)
        {
            Command command = new Command
            {
                WorldEpoch = worldEpoch,
                RequestId = requestId,
                Kind = (CommandKind)kind,
                Source = Source.Camp,
                RoomIndex = -1,
                Bonus = -1,
                SubBonus = -1,
            };
            if (kind < (int)CommandKind.OpenCamp || kind > (int)CommandKind.Resync)
                throw new FormatException("unexpected rest command kind");
            command.Transaction = reader.Int();
            command.Revision = reader.Int();
            switch (command.Kind)
            {
                case CommandKind.OpenCamp:
                {
                    byte source = reader.Byte();
                    if (source > (byte)Source.Zone) throw new FormatException("open rest source " + source);
                    command.Source = (Source)source;
                    if (command.Source == Source.Zone) command.SourceId = reader.Guid();
                    break;
                }
                case CommandKind.SelectSurvival:
                    command.Actor = reader.Guid();
                    command.Bonus = reader.Int();
                    command.SubBonus = reader.Int();
                    break;
                case CommandKind.ConfirmInn:
                    command.OpenOnly = ReadFlag(reader, "inn selection");
                    command.SourceId = reader.Guid();
                    command.RoomIndex = reader.Int();
                    command.Source = Source.Inn;
                    break;
                case CommandKind.ConfirmScripted:
                    command.Source = Source.Scripted;
                    if (command.Transaction == 0)
                    {
                        command.OpenOnly = ReadFlag(reader, "scripted open");
                        command.RoomIndex = reader.Int();
                    }
                    break;
            }
            reader.Finish();
            if (command.WorldEpoch < 0 || command.RequestId <= 0 || command.Transaction < 0 || command.Revision < 0)
                throw new FormatException("rest command envelope outside its bound");
            return command;
        }

        private void HandleCommand(int playerId, Command command)
        {
            SyncContext();
            if (!_session.IsHost || playerId < 0) return;
            if (command.Kind != CommandKind.Resync && command.WorldEpoch != _worldEpoch)
            {
                Complete(playerId, command, (int)Result.WrongEpoch);
                return;
            }
            RequestCache cache = GetRequestCache(playerId);
            if (cache.ById.TryGetValue(command.RequestId, out CachedRequest cached))
            {
                if (cached.Command.Same(command))
                    Complete(playerId, command, cached.Result, cacheResult: false, stateSeq: cached.StateSeq);
                else Complete(playerId, command, (int)Result.RequestConflict, cacheResult: false);
                return;
            }

            int result;
            if (command.Kind != CommandKind.Resync && (!Ready()
                || playerId != Session.HostId && !_session.IsPeerReady(playerId))) result = (int)Result.NotReady;
            else
            {
                switch (command.Kind)
                {
                    case CommandKind.OpenCamp: result = OpenCamp(playerId, command); break;
                    case CommandKind.Confirm: result = ConfirmCamp(playerId, command); break;
                    case CommandKind.Cancel: result = Cancel(playerId, command); break;
                    case CommandKind.SelectSurvival: result = SelectSurvival(playerId, command); break;
                    case CommandKind.OpenStash: result = OpenStash(playerId, command); break;
                    case CommandKind.StashFinished: result = StashFinished(playerId, command); break;
                    case CommandKind.ConfirmInn: result = ConfirmInn(playerId, command); break;
                    case CommandKind.ConfirmScripted: result = ConfirmScripted(playerId, command); break;
                    case CommandKind.Resync:
                        Publish(playerId);
                        result = (int)Result.Accepted;
                        break;
                    default: result = (int)Result.Rejected; break;
                }
            }
            Complete(playerId, command, result);
        }

        private RequestCache GetRequestCache(int playerId)
        {
            if (!_requests.TryGetValue(playerId, out RequestCache cache))
                _requests.Add(playerId, cache = new RequestCache());
            return cache;
        }

        private void Complete(int playerId, Command command, int result, bool cacheResult = true, long? stateSeq = null)
        {
            long resultStateSeq = stateSeq ?? _stateSeq;
            if (cacheResult) GetRequestCache(playerId).Add(command.RequestId, command, result, resultStateSeq);
            if (playerId == Session.HostId)
            {
                PresentResult(result, command.Kind);
                return;
            }
            _session.SendToPlayer(playerId, FlowWire.WriteResult(command.WorldEpoch, command.RequestId,
                Domain, (int)command.Kind, result, resultStateSeq).ToArray());
        }

        private int OpenCamp(int playerId, Command command)
        {
            if (command.Source == Source.Zone) return OpenZone(playerId, command);
            if (command.Transaction != 0 || command.Revision != 0 || command.SourceId != Guid.Empty)
                return (int)Result.Rejected;
            if (_transaction != null && HasActiveTransaction)
                return _transaction.Source == Source.Camp && _transaction.Phase == Phase.Prompt
                    ? (int)Result.Accepted : (int)Result.Busy;

            RestZone.CannotCampReason reason = RestZone.WhyCannotCamp();
            if ((reason & ~(RestZone.CannotCampReason.NoSupplies | RestZone.CannotCampReason.NoCampMap)) == 0
                && FreeRestAvailable()) return (int)Result.FreeRestAvailable;
            if (reason != 0) return CannotCampResultBase + (int)reason;
            Begin(playerId, Source.Camp, RestZone.Mode.Camp, RestMovieMode.None, Guid.Empty, -1);
            return (int)Result.Accepted;
        }

        private int OpenZone(int playerId, Command command)
        {
            if (command.Transaction != 0 || command.Revision != 0 || command.SourceId == Guid.Empty)
                return (int)Result.Rejected;
            if (_transaction != null && HasActiveTransaction)
            {
                if (_transaction.Source == Source.Zone && _transaction.SourceId == command.SourceId)
                    return (int)Result.Accepted;
                return (int)Result.Busy;
            }
            RestInteraction interaction = ResolveZone(command.SourceId);
            if (interaction == null || interaction.LinkedRestZone == null || !interaction.LinkedRestZone.CanRest
                || GameState.InCombat) return (int)Result.ZoneUnavailable;
            Begin(playerId, Source.Zone, RestZone.Mode.Camp, RestMovieMode.None, command.SourceId, -1, publish: false);
            return ExecuteTransaction();
        }

        private int ConfirmCamp(int playerId, Command command)
        {
            int common = ValidateTransaction(playerId, command, Source.Camp, Phase.Prompt);
            if (common != (int)Result.Accepted) return common;
            RestZone.CannotCampReason reason = RestZone.WhyCannotCamp();
            if (reason != 0) return CannotCampResultBase + (int)reason;
            return ExecuteTransaction();
        }

        private int ConfirmInn(int playerId, Command command)
        {
            if (command.SourceId == Guid.Empty && !FreeRestAvailable()) return (int)Result.CannotPurchase;
            if (!command.OpenOnly)
            {
                int common = ValidateTransaction(playerId, command, Source.Inn, Phase.Prompt);
                if (common != (int)Result.Accepted) return common;
                if (_transaction.SourceId != command.SourceId || _transaction.RoomIndex != command.RoomIndex)
                    return (int)Result.StaleTransaction;
                if (_transaction.Phase == Phase.Completed || _transaction.Phase == Phase.Cancelled)
                    return (int)Result.Accepted;
                return ExecuteTransaction();
            }

            Inn inn = ResolveInn(command.SourceId);
            Inn.InnRoom room = Room(inn, command.RoomIndex);
            if (inn == null || room == null || !inn.CanPurchaseRoom(room)) return (int)Result.CannotPurchase;
            if (command.Transaction != 0 || command.Revision != 0) return (int)Result.Rejected;
            if (_transaction != null && HasActiveTransaction)
            {
                if (_transaction.Source == Source.Inn && _transaction.SourceId == command.SourceId
                    && _transaction.RoomIndex == command.RoomIndex && _transaction.Phase == Phase.Prompt)
                    return (int)Result.Accepted;
                return (int)Result.Busy;
            }
            Begin(playerId, Source.Inn, RestZone.Mode.Inn, RestMovieMode.None, command.SourceId, command.RoomIndex);
            return (int)Result.Accepted;
        }

        private int ConfirmScripted(int playerId, Command command)
        {
            if (command.Transaction == 0)
            {
                if (!command.OpenOnly || playerId != Session.HostId || !Enum.IsDefined(typeof(RestMovieMode), command.RoomIndex))
                    return (int)Result.Rejected;
                if (_transaction != null && HasActiveTransaction) return (int)Result.Busy;
                Begin(playerId, Source.Scripted, RestZone.Mode.Scripted, (RestMovieMode)command.RoomIndex, Guid.Empty, -1);
                return (int)Result.Accepted;
            }
            int common = ValidateTransaction(playerId, command, Source.Scripted, Phase.Prompt);
            return common == (int)Result.Accepted ? ExecuteTransaction() : common;
        }

        private int ValidateTransaction(int playerId, Command command, Source source, Phase phase)
        {
            if (_transaction == null || command.Transaction == 0 || command.Transaction != _transaction.Id)
                return (int)Result.StaleTransaction;
            if (_transaction.Source != source) return (int)Result.Rejected;
            if ((_transaction.Phase == Phase.Completed || _transaction.Phase == Phase.Cancelled)
                && command.Kind != CommandKind.SelectSurvival && command.Kind != CommandKind.OpenStash)
                return (int)Result.Accepted;
            if (_transaction.Phase != phase || command.Revision != _transaction.Revision)
                return (int)Result.StaleTransaction;
            return playerId == _transaction.Initiator ? (int)Result.Accepted : (int)Result.NotInitiator;
        }

        private int Cancel(int playerId, Command command)
        {
            if (_transaction == null || command.Transaction == 0 || command.Transaction != _transaction.Id)
                return (int)Result.StaleTransaction;
            if (_transaction.Phase == Phase.Cancelled || _transaction.Phase == Phase.Completed)
                return (int)Result.Accepted;
            if (_transaction.Phase != Phase.Prompt && _transaction.Phase != Phase.Stash)
                return (int)Result.StaleTransaction;
            if (playerId != _transaction.Initiator) return (int)Result.NotInitiator;
            if (command.Revision != _transaction.Revision) return (int)Result.StaleTransaction;
            CancelTransaction();
            return (int)Result.Accepted;
        }

        private int OpenStash(int playerId, Command command)
        {
            if (_transaction == null || command.Transaction != _transaction.Id
                || (_transaction.Source != Source.Camp && _transaction.Source != Source.Inn))
                return (int)Result.StaleTransaction;
            if (_transaction.Phase == Phase.Stash && command.Revision == _transaction.Revision)
                return (int)Result.Accepted;
            int common = ValidateTransaction(playerId, command, _transaction.Source, Phase.Prompt);
            if (common != (int)Result.Accepted) return common;
            _transaction.Phase = Phase.Stash;
            _transaction.Revision = NextPositive(_transaction.Revision);
            Publish();
            return (int)Result.Accepted;
        }

        private int StashFinished(int playerId, Command command)
        {
            if (_transaction == null || command.Transaction != _transaction.Id
                || (_transaction.Source != Source.Camp && _transaction.Source != Source.Inn))
                return (int)Result.StaleTransaction;
            int common = ValidateTransaction(playerId, command, _transaction.Source, Phase.Stash);
            if (common != (int)Result.Accepted) return common;
            if (_transaction.Source == Source.Camp)
            {
                RestZone.CannotCampReason reason = RestZone.WhyCannotCamp();
                if (reason != 0) return CannotCampResultBase + (int)reason;
            }
            else
            {
                Inn inn = ResolveInn(_transaction.SourceId);
                Inn.InnRoom room = Room(inn, _transaction.RoomIndex);
                if (inn == null || room == null || !inn.CanPurchaseRoom(room)) return (int)Result.CannotPurchase;
            }
            return ExecuteTransaction();
        }

        private int SelectSurvival(int playerId, Command command)
        {
            if (_transaction == null || command.Transaction != _transaction.Id
                || (_transaction.Source != Source.Camp && _transaction.Source != Source.Zone)
                || (_transaction.Phase != Phase.Prompt && _transaction.Phase != Phase.Stash)
                || command.Revision != _transaction.Revision) return (int)Result.StaleTransaction;
            PartyMemberAI member = ResolveMember(command.Actor);
            if (member == null || member.Slot < 0 || member.Slot >= MaxPartyRecords)
                return (int)Result.InvalidSelection;
            if (_ownership.OwnerOf(member) != playerId) return (int)Result.NotOwner;
            CharacterStats stats = member.GetComponent<CharacterStats>();
            if (!ValidSurvival(stats, command.Bonus, command.SubBonus)) return (int)Result.InvalidSelection;
            if (stats.LastSelectedSurvivalBonus != command.Bonus || stats.LastSelectedSurvivalSubBonus != command.SubBonus)
            {
                stats.LastSelectedSurvivalBonus = command.Bonus;
                stats.LastSelectedSurvivalSubBonus = command.SubBonus;
                _transaction.Revision = NextPositive(_transaction.Revision);
                Publish();
            }
            return (int)Result.Accepted;
        }

        private void Begin(int playerId, Source source, RestZone.Mode mode, RestMovieMode movie, Guid sourceId, int roomIndex,
            bool publish = true)
        {
            ResetPresentation();
            _transaction = new Transaction
            {
                Id = NextTransactionId(),
                Revision = 0,
                Initiator = playerId,
                Source = source,
                Mode = mode,
                RequestedMovie = movie,
                Movie = movie,
                SourceId = sourceId,
                RoomIndex = roomIndex,
                Phase = Phase.Prompt,
            };
            if (publish) Publish();
        }

        private int NextTransactionId()
        {
            int id = _nextTransactionId;
            _nextTransactionId = _nextTransactionId == int.MaxValue ? 1 : _nextTransactionId + 1;
            return id;
        }

        private int ExecuteTransaction()
        {
            Transaction transaction = _transaction;
            if (transaction == null || transaction.Committed) return (int)Result.Accepted;
            if (transaction.Phase != Phase.Prompt && transaction.Phase != Phase.Stash)
                return (int)Result.StaleTransaction;

            Inn inn = null;
            Inn.InnRoom room = null;
            if (transaction.Source == Source.Inn)
            {
                if (transaction.SourceId == Guid.Empty && !FreeRestAvailable()) return (int)Result.CannotPurchase;
                inn = ResolveInn(transaction.SourceId);
                room = Room(inn, transaction.RoomIndex);
                if (inn == null || room == null || !inn.CanPurchaseRoom(room)) return (int)Result.CannotPurchase;
            }
            else if (transaction.Source == Source.Camp)
            {
                RestZone.CannotCampReason reason = RestZone.WhyCannotCamp();
                if (reason != 0) return CannotCampResultBase + (int)reason;
            }
            else if (transaction.Source == Source.Zone)
            {
                RestInteraction interaction = ResolveZone(transaction.SourceId);
                if (interaction == null || interaction.LinkedRestZone == null || !interaction.LinkedRestZone.CanRest
                    || GameState.InCombat) return (int)Result.ZoneUnavailable;
            }

            transaction.Committed = true;
            transaction.Movie = MovieFor(transaction.Mode, transaction.RequestedMovie);
            transaction.StartTime = WorldTime.Instance.CurrentTime.TotalSeconds;
            transaction.Phase = Phase.Resting;
            transaction.Revision = NextPositive(transaction.Revision);
            Publish();
            try
            {
                using (new NativeRestScope())
                {
                    OverrideMovie.SetValue(null, transaction.Movie);
                    switch (transaction.Source)
                    {
                        case Source.Camp:
                            RestZone.Camp();
                            break;
                        case Source.Inn:
                            inn.PurchaseRoom(room);
                            break;
                        case Source.Zone:
                        case Source.Scripted:
                            RestZone.Rest(transaction.Mode);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Native rest transaction " + transaction.Id + " failed: " + ex);
                transaction.EndTime = WorldTime.Instance.CurrentTime.TotalSeconds;
                transaction.AdvancedSeconds = Math.Max(0, transaction.EndTime - transaction.StartTime);
                transaction.Phase = Phase.Cancelled;
                transaction.Revision = NextPositive(transaction.Revision);
                InventorySync.MarkChanged(GameState.PlayerInventory);
                Publish();
                _session.Note("Rest failed on the host; no further rest action was attempted.");
                return (int)Result.NativeFailure;
            }
            finally
            {
                OverrideMovie.SetValue(null, RestMovieMode.None);
            }
            transaction.EndTime = WorldTime.Instance.CurrentTime.TotalSeconds;
            transaction.AdvancedSeconds = Math.Max(0, transaction.EndTime - transaction.StartTime);
            InventorySync.MarkChanged(GameState.PlayerInventory);
            transaction.Phase = Phase.Completed;
            transaction.Revision = NextPositive(transaction.Revision);
            Publish();
            return (int)Result.Accepted;
        }

        private void CancelTransaction()
        {
            if (_transaction == null || !_session.IsHost) return;
            if (_transaction.Phase == Phase.Completed || _transaction.Phase == Phase.Cancelled) return;
            _transaction.Phase = Phase.Cancelled;
            _transaction.Revision = NextPositive(_transaction.Revision);
            Publish();
        }

        private static RestMovieMode MovieFor(RestZone.Mode mode, RestMovieMode requested)
        {
            RestMovieMode movie = RestMovieMode.None;
            if (mode == RestZone.Mode.Camp) movie = RestMovieMode.Camp;
            else if (mode == RestZone.Mode.Inn) movie = RestMovieMode.Inn;
            Player player = GameState.s_playerCharacter;
            if (player != null && GameGlobalVariables.IsPlayerWatcher())
            {
                Health health = player.GetComponent<Health>();
                CharacterStats stats = player.GetComponent<CharacterStats>();
                if (health != null && stats != null && (health.HealthPercentage < 0.25f
                    || stats.GetFatigueLevel() >= CharacterStats.FatigueLevel.Minor)) movie = RestMovieMode.Watcher;
            }
            return requested == RestMovieMode.None ? movie : requested;
        }

        private long Publish(int targetPlayer = -1)
        {
            if (!_session.IsHost || _disposed || _worldEpoch <= 0) return _stateSeq;
            Snapshot snapshot = Capture();
            if (_stateSeq == long.MaxValue)
            {
                if (!_sequenceExhaustedLogged)
                {
                    _sequenceExhaustedLogged = true;
                    Plugin.Log.LogWarning("Rest snapshot sequence exhausted; waiting for the next world epoch");
                }
                return _stateSeq;
            }
            snapshot.Epoch = _worldEpoch;
            snapshot.StateSeq = ++_stateSeq;
            _snapshot = snapshot;
            _hasSnapshot = true;
            MessageWriter message = FlowWire.WriteSnapshot(MessageType.RestState, snapshot.Epoch, snapshot.StateSeq, true, 1);
            WriteSnapshot(message, snapshot);
            byte[] data = message.ToArray();
            if (data.Length > MaxStateBytes) throw new InvalidOperationException("rest snapshot exceeds its byte bound");
            if (targetPlayer >= 0) _session.SendToPlayer(targetPlayer, data);
            else _session.SendToGuests(data);
            Present(snapshot);
            return _stateSeq;
        }

        private Snapshot Capture()
        {
            Transaction t = _transaction;
            Snapshot snapshot = new Snapshot
            {
                Epoch = _worldEpoch,
                StateSeq = _stateSeq + 1,
                Transaction = t != null ? t.Id : 0,
                Revision = t != null ? t.Revision : 0,
                Phase = t != null ? t.Phase : Phase.Closed,
                Source = t != null ? t.Source : Source.Camp,
                Mode = t != null ? t.Mode : RestZone.Mode.Camp,
                Movie = t != null ? (t.Phase == Phase.Resting || t.Phase == Phase.Completed ? t.Movie : t.RequestedMovie) : RestMovieMode.None,
                Initiator = t != null ? t.Initiator : -1,
                SourceId = t != null ? t.SourceId : Guid.Empty,
                RoomIndex = t != null ? t.RoomIndex : -1,
                StartTime = t != null ? t.StartTime : -1,
                EndTime = t != null ? t.EndTime : -1,
                AdvancedSeconds = t != null ? t.AdvancedSeconds : 0,
            };
            PlayerInventory inventory = GameState.s_playerCharacter != null ? GameState.s_playerCharacter.Inventory : null;
            if (inventory != null)
            {
                snapshot.Supplies = Mathf.Clamp(inventory.CampingSuppliesTotal, 0, MaxSupplies);
                snapshot.Currency = BoundedCurrency(inventory.currencyTotalValue.v);
            }
            PartyMemberAI[] members = PartyMemberAI.PartyMembers;
            for (int i = 0; i < members.Length && i < MaxPartyRecords; i++)
            {
                PartyMemberAI member = members[i];
                CharacterStats stats = member != null ? member.GetComponent<CharacterStats>() : null;
                Guid id = NetworkId(member != null ? member.gameObject : null);
                if (stats == null || id == Guid.Empty) continue;
                snapshot.Party.Add(new PartyState
                {
                    Id = id,
                    Fatigue = stats.GetFatigueLevel(),
                    Bonus = stats.LastSelectedSurvivalBonus,
                    SubBonus = stats.LastSelectedSurvivalSubBonus,
                });
            }
            return snapshot;
        }

        private static void WriteSnapshot(MessageWriter message, Snapshot snapshot)
        {
            message.Int(snapshot.Transaction).Int(snapshot.Revision)
                .Byte((byte)snapshot.Phase)
                .Byte((byte)(snapshot.Prompt ? 1 : 0)).Byte((byte)(snapshot.Stash ? 1 : 0))
                .Byte((byte)(snapshot.Resting ? 1 : 0)).Byte((byte)(snapshot.Completed ? 1 : 0))
                .Byte((byte)(snapshot.Cancelled ? 1 : 0))
                .Byte((byte)snapshot.Source).Byte((byte)snapshot.Mode).Byte((byte)snapshot.Movie)
                .Int(snapshot.Initiator).Guid(snapshot.SourceId).Int(snapshot.RoomIndex)
                .Int(snapshot.Party.Count);
            foreach (PartyState party in snapshot.Party)
                message.Guid(party.Id).Byte((byte)party.Fatigue).Int(party.Bonus).Int(party.SubBonus);
            message.Int(snapshot.Supplies).Float(snapshot.Currency)
                .Int(snapshot.StartTime).Int(snapshot.EndTime).Int(snapshot.AdvancedSeconds);
        }

        private void ReceiveSnapshot(MessageReader reader)
        {
            Snapshot snapshot = ReadSnapshot(reader);
            if (FlowCoordinator.IsInitialized && snapshot.Epoch != FlowCoordinator.WorldEpoch) return;
            if (_hasSnapshot && snapshot.Epoch == _worldEpoch && snapshot.StateSeq <= _stateSeq) return;
            bool newEpoch = !_hasSnapshot || snapshot.Epoch != _worldEpoch;
            if (newEpoch) ResetPresentation();
            _snapshot = snapshot;
            _worldEpoch = snapshot.Epoch;
            _stateSeq = snapshot.StateSeq;
            _hasSnapshot = true;
            _needsResync = false;
            _partyApplied.Clear();
            ApplyParty(snapshot);
            Present(snapshot);
        }

        private static Snapshot ReadSnapshot(MessageReader reader)
        {
            FlowSnapshotHeader header = FlowWire.ReadSnapshot(reader, MessageType.RestState, 1);
            if (!header.Full || header.Count != 1 || header.StateSeq <= 0)
                throw new FormatException("rest state must be one full snapshot");
            Snapshot snapshot = new Snapshot
            {
                Epoch = header.WorldEpoch,
                StateSeq = header.StateSeq,
                Transaction = reader.Int(),
                Revision = reader.Int(),
                Phase = (Phase)reader.Byte(),
            };
            bool prompt = ReadFlag(reader, "rest prompt");
            bool stash = ReadFlag(reader, "rest stash");
            bool resting = ReadFlag(reader, "rest resting");
            bool completed = ReadFlag(reader, "rest completed");
            bool cancelled = ReadFlag(reader, "rest cancelled");
            snapshot.Source = (Source)reader.Byte();
            snapshot.Mode = (RestZone.Mode)reader.Byte();
            snapshot.Movie = (RestMovieMode)reader.Byte();
            snapshot.Initiator = reader.Int();
            snapshot.SourceId = reader.Guid();
            snapshot.RoomIndex = reader.Int();
            int count = reader.Int();
            if (count < 0 || count > MaxPartyRecords) throw new FormatException("rest party count " + count);
            HashSet<Guid> ids = new HashSet<Guid>();
            for (int i = 0; i < count; i++)
            {
                PartyState party = new PartyState
                {
                    Id = reader.Guid(),
                    Fatigue = (CharacterStats.FatigueLevel)reader.Byte(),
                    Bonus = reader.Int(),
                    SubBonus = reader.Int(),
                };
                if (party.Id == Guid.Empty || !ids.Add(party.Id) || !Enum.IsDefined(typeof(CharacterStats.FatigueLevel), party.Fatigue)
                    || party.Bonus < -1 || party.Bonus > MaxSurvivalIndex
                    || party.SubBonus < -1 || party.SubBonus > MaxSurvivalIndex)
                    throw new FormatException("invalid rest party record");
                snapshot.Party.Add(party);
            }
            snapshot.Supplies = reader.Int();
            snapshot.Currency = reader.Float();
            snapshot.StartTime = reader.Int();
            snapshot.EndTime = reader.Int();
            snapshot.AdvancedSeconds = reader.Int();
            reader.Finish();
            ValidateSnapshot(snapshot, prompt, stash, resting, completed, cancelled);
            return snapshot;
        }

        private static void ValidateSnapshot(Snapshot s, bool prompt, bool stash, bool resting, bool completed, bool cancelled)
        {
            if (s.Epoch <= 0 || s.StateSeq <= 0 || s.Transaction < 0 || s.Revision < 0
                || !Enum.IsDefined(typeof(Phase), s.Phase) || !Enum.IsDefined(typeof(Source), s.Source)
                || !Enum.IsDefined(typeof(RestZone.Mode), s.Mode) || !Enum.IsDefined(typeof(RestMovieMode), s.Movie)
                || prompt != s.Prompt || stash != s.Stash || resting != s.Resting
                || completed != s.Completed || cancelled != s.Cancelled
                || s.Initiator < -1 || s.Initiator > 1000000 || s.RoomIndex < -1 || s.RoomIndex >= MaxRooms
                || s.Supplies < 0 || s.Supplies > MaxSupplies || !IsFinite(s.Currency)
                || s.Currency < 0f || s.Currency > MaxCurrency || s.StartTime < -1 || s.EndTime < -1
                || s.AdvancedSeconds < 0) throw new FormatException("invalid rest snapshot fields");
            if ((s.Transaction == 0) != (s.Phase == Phase.Closed)
                || (s.Phase == Phase.Closed && (s.Initiator != -1 || s.SourceId != Guid.Empty || s.RoomIndex != -1))
                || (s.Phase != Phase.Closed && s.Initiator < 0)
                || (s.Source == Source.Zone && s.SourceId == Guid.Empty)
                || ((s.Source == Source.Camp || s.Source == Source.Scripted) && s.SourceId != Guid.Empty)
                || (s.Source != Source.Inn && s.RoomIndex != -1 && s.Source != Source.Scripted))
                throw new FormatException("inconsistent rest transaction snapshot");
            if (s.Phase == Phase.Completed && (s.StartTime < 0 || s.EndTime < s.StartTime))
                throw new FormatException("invalid completed rest times");
        }

        private void ApplyParty(Snapshot snapshot)
        {
            if (!_session.IsGuest || !Plugin.IsMirroring) return;
            RegisterSurvivalAfflictions();
            foreach (PartyState party in snapshot.Party)
            {
                if (_partyApplied.Contains(party.Id)) continue;
                PartyMemberAI member = ResolveMember(party.Id);
                CharacterStats stats = member != null ? member.GetComponent<CharacterStats>() : null;
                if (stats == null) continue;
                Fatigue(stats) = party.Fatigue;
                stats.LastSelectedSurvivalBonus = party.Bonus;
                stats.LastSelectedSurvivalSubBonus = party.SubBonus;
                _partyApplied.Add(party.Id);
            }
        }

        private void RegisterSurvivalAffliction(Affliction affliction)
        {
            AfflictionData data = AfflictionData.Instance;
            if (data == null || data.AfflictionsForScripts == null) return;
            bool present = false;
            for (int i = data.AfflictionsForScripts.Count - 1; i >= 0; i--)
            {
                Affliction existing = data.AfflictionsForScripts[i];
                if (existing == null) data.AfflictionsForScripts.RemoveAt(i);
                else if (ReferenceEquals(existing, affliction)) present = true;
            }
            if (affliction != null && !present) data.AfflictionsForScripts.Add(affliction);
        }

        private void RegisterSurvivalAfflictions()
        {
            AfflictionData data = AfflictionData.Instance;
            CampEffectList effects = data != null ? data.SurvivalCampEffects : null;
            if (effects == null || data.AfflictionsForScripts == null) return;
            if (effects.BonusesByLevel != null)
                foreach (Affliction affliction in effects.BonusesByLevel)
                    RegisterSurvivalAffliction(affliction);
            if (effects.SubBonuses == null) return;
            foreach (CampEffectSubBonuses group in effects.SubBonuses)
            {
                if (group == null || group.SubBonuses == null) continue;
                foreach (CampEffectSubBonus subBonus in group.SubBonuses)
                    RegisterSurvivalAffliction(subBonus != null ? subBonus.Affliction : null);
            }
        }

        private void Present(Snapshot snapshot)
        {
            if (_disposed || !Ready()) return;
            try
            {
                if (snapshot.Prompt)
                {
                    if (_presentedStash != 0) HideStash();
                    if (_presentedMovie != 0) HideMovie();
                    if (snapshot.Source == Source.Scripted) ShowScriptedPrompt(snapshot);
                    else ShowConfirmPrompt(snapshot);
                }
                else if (snapshot.Stash)
                {
                    if (_presentedMovie != 0) HideMovie();
                    HidePrompt();
                    ShowStash(snapshot);
                }
                else
                {
                    HidePrompt();
                    if (_presentedStash != 0) HideStash();
                    if ((snapshot.Resting || snapshot.Completed) && snapshot.Source == Source.Inn) HideInnStore();
                    if (snapshot.Resting && _session.IsGuest) ShowMovie(snapshot);
                    else if (!snapshot.Completed) HideMovie();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not present rest state " + snapshot.Transaction + ": " + ex.Message);
            }
        }

        private void ShowConfirmPrompt(Snapshot snapshot)
        {
            UIRestConfirmBox box = UIRestConfirmBox.Instance;
            if (box == null) return;
            if (_presentedPrompt == snapshot.Transaction && box.WindowActive())
            {
                if (_presentedPromptRevision != snapshot.Revision) RefreshPartyGrid(box);
                _presentedPromptRevision = snapshot.Revision;
                return;
            }
            HidePrompt();
            string text;
            UIRestConfirmBox.RestType type;
            if (snapshot.Source == Source.Camp)
            {
                text = GUIUtils.GetText(2302);
                type = UIRestConfirmBox.RestType.CAMP;
            }
            else if (snapshot.Source == Source.Inn)
            {
                Inn inn = ResolveInn(snapshot.SourceId);
                Inn.InnRoom room = Room(inn, snapshot.RoomIndex);
                if (inn == null || room == null) return;
                type = inn.IsPlayerOwned ? UIRestConfirmBox.RestType.PLAYER_HOUSE : UIRestConfirmBox.RestType.INN;
                text = GUIUtils.Format(inn.IsPlayerOwned ? 888 : 755, room.DisplayName.GetText(),
                    GUIUtils.Format(294, room.ActualCost(inn)));
            }
            else return;

            _presentedPrompt = snapshot.Transaction;
            _presentedPromptRevision = snapshot.Revision;
            using (new PresentationScope())
            {
                UIRestConfirmBox.OnDialogEnd = (UIRestConfirmBox.OnEndDialog)Delegate.Combine(
                    UIRestConfirmBox.OnDialogEnd, new UIRestConfirmBox.OnEndDialog(OnConfirmBox));
                box.SetData(type, text);
            }
        }

        private void RefreshPartyGrid(UIRestConfirmBox box)
        {
            UIRestBoxPartyMember[] widgets = RestPartyWidgets(box);
            if (widgets == null) return;
            using (new PresentationScope())
            {
                for (int i = 0; i < widgets.Length && i < PartyMemberAI.PartyMembers.Length; i++)
                    if (widgets[i] != null) widgets[i].LoadPartyMember(PartyMemberAI.PartyMembers[i]);
            }
        }

        private void OnConfirmBox(UIRestConfirmBox.Result result)
        {
            if (Presenting || _snapshot == null || !_snapshot.Prompt || _presentedPrompt != _snapshot.Transaction) return;
            Source source = _snapshot.Source;
            UIRestConfirmBox.OnDialogEnd = (UIRestConfirmBox.OnEndDialog)Delegate.Remove(
                UIRestConfirmBox.OnDialogEnd, new UIRestConfirmBox.OnEndDialog(OnConfirmBox));
            _presentedPrompt = 0;
            _presentedPromptRevision = -1;
            if (result == UIRestConfirmBox.Result.CANCEL)
            {
                Issue(NewCommand(CommandKind.Cancel));
                return;
            }
            if (result == UIRestConfirmBox.Result.STASH)
            {
                Issue(NewCommand(CommandKind.OpenStash));
                return;
            }
            if (source == Source.Camp) Issue(NewCommand(CommandKind.Confirm));
            else if (source == Source.Inn)
            {
                Command command = NewCommand(CommandKind.ConfirmInn);
                command.Source = Source.Inn;
                Issue(command);
            }
        }

        private void ShowScriptedPrompt(Snapshot snapshot)
        {
            if (_presentedPrompt == snapshot.Transaction && _scriptedPrompt != null && _scriptedPrompt.WindowActive()) return;
            HidePrompt();
            _presentedPrompt = snapshot.Transaction;
            _presentedPromptRevision = snapshot.Revision;
            using (new PresentationScope())
            {
                _scriptedPrompt = UIWindowManager.ShowMessageBox(UIMessageBox.ButtonStyle.YESNO,
                    GUIUtils.GetText(356), GUIUtils.Format(359, RestZone.DEFAULT_REST_HOURS));
                if (_scriptedPrompt != null)
                    _scriptedPrompt.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Combine(
                        _scriptedPrompt.OnDialogEnd, new UIMessageBox.OnEndDialog(OnScriptedPrompt));
            }
        }

        private void OnScriptedPrompt(UIMessageBox.Result result, UIMessageBox owner)
        {
            if (Presenting || _snapshot == null || !_snapshot.Prompt || _snapshot.Source != Source.Scripted
                || _presentedPrompt != _snapshot.Transaction || owner != null && owner != _scriptedPrompt) return;
            if (owner != null)
                owner.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Remove(
                    owner.OnDialogEnd, new UIMessageBox.OnEndDialog(OnScriptedPrompt));
            _presentedPrompt = 0;
            _presentedPromptRevision = -1;
            _scriptedPrompt = null;
            Issue(NewCommand(result == UIMessageBox.Result.AFFIRMATIVE ? CommandKind.ConfirmScripted : CommandKind.Cancel));
        }

        private void HidePrompt()
        {
            int presented = _presentedPrompt;
            _presentedPrompt = 0;
            _presentedPromptRevision = -1;
            if (_scriptedPrompt != null)
            {
                UIMessageBox scripted = _scriptedPrompt;
                _scriptedPrompt = null;
                using (new PresentationScope())
                {
                    scripted.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Remove(
                        scripted.OnDialogEnd, new UIMessageBox.OnEndDialog(OnScriptedPrompt));
                    if (scripted.WindowActive()) scripted.HideWindow(true);
                }
            }
            if (UIRestConfirmBox.Instance == null) return;
            UIRestConfirmBox box = UIRestConfirmBox.Instance;
            using (new PresentationScope())
            {
                UIRestConfirmBox.OnDialogEnd = (UIRestConfirmBox.OnEndDialog)Delegate.Remove(
                    UIRestConfirmBox.OnDialogEnd, new UIRestConfirmBox.OnEndDialog(OnConfirmBox));
                if (presented != 0)
                {
                    UIRestBoxPartyMember[] widgets = RestPartyWidgets(box);
                    if (widgets != null)
                        foreach (UIRestBoxPartyMember widget in widgets)
                            if (widget != null && widget.BonusDropdown != null) widget.BonusDropdown.Close();
                    if (box.WindowActive()) box.HideWindow(true);
                }
            }
        }

        private void ShowStash(Snapshot snapshot)
        {
            UIInventoryManager inventory = UIInventoryManager.Instance;
            if (inventory == null || _stashFinishSent == snapshot.Transaction) return;
            if (_presentedStash == snapshot.Transaction && inventory.WindowActive()) return;
            if (_presentedStash != 0 && _presentedStash != snapshot.Transaction) HideStash();
            _presentedStash = snapshot.Transaction;
            _stashClosed = false;
            using (new PresentationScope())
            {
                inventory.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Remove(
                    inventory.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                inventory.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Combine(
                    inventory.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                if (UICraftingManager.Instance != null)
                {
                    UICraftingManager.Instance.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Remove(
                        UICraftingManager.Instance.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                    UICraftingManager.Instance.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Combine(
                        UICraftingManager.Instance.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                }
                inventory.ImmediateStashAccess();
            }
        }

        private void OnStashWindowHidden(UIHudWindow window)
        {
            if (!Presenting && _presentedStash != 0) _stashClosed = true;
        }

        private void CheckStashClosed()
        {
            if (_snapshot == null || !_snapshot.Stash || _presentedStash != _snapshot.Transaction
                || !_stashClosed || _stashFinishSent == _snapshot.Transaction) return;
            bool inventoryOpen = UIInventoryManager.Instance != null && UIInventoryManager.Instance.WindowActive();
            bool craftingOpen = UICraftingManager.Instance != null && UICraftingManager.Instance.WindowActive();
            if (inventoryOpen || craftingOpen) return;
            _stashFinishSent = _snapshot.Transaction;
            Issue(NewCommand(CommandKind.StashFinished));
        }

        private void HideStash()
        {
            _presentedStash = 0;
            _stashFinishSent = 0;
            _stashClosed = false;
            using (new PresentationScope())
            {
                if (UIInventoryManager.Instance != null)
                {
                    UIInventoryManager.Instance.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Remove(
                        UIInventoryManager.Instance.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                    if (UIInventoryManager.Instance.WindowActive()) UIInventoryManager.Instance.HideWindow(true);
                }
                if (UICraftingManager.Instance != null)
                {
                    UICraftingManager.Instance.OnWindowHidden = (UIHudWindow.WindowHiddenDelegate)Delegate.Remove(
                        UICraftingManager.Instance.OnWindowHidden, new UIHudWindow.WindowHiddenDelegate(OnStashWindowHidden));
                    if (UICraftingManager.Instance.WindowActive()) UICraftingManager.Instance.HideWindow(true);
                }
            }
        }

        private static void HideInnStore()
        {
            if (UIStoreManager.Instance != null && UIStoreManager.Instance.WindowActive())
                UIStoreManager.Instance.HideWindow(true);
        }

        private void ShowMovie(Snapshot snapshot)
        {
            if (_presentedMovie == snapshot.Transaction || UIRestMovieManager.Instance == null) return;
            _presentedMovie = snapshot.Transaction;
            using (new PresentationScope())
            {
                UIRestMovieManager.Instance.ForMode = snapshot.Movie;
                UIRestMovieManager.Instance.ShowWindow();
                if (GlobalAudioPlayer.Instance != null) GlobalAudioPlayer.Instance.Play(UIAudioList.UIAudioType.Rest);
            }
        }

        private void HideMovie()
        {
            if (_presentedMovie == 0) return;
            _presentedMovie = 0;
            if (UIRestMovieManager.Instance == null) return;
            using (new PresentationScope())
                if (UIRestMovieManager.Instance.WindowActive()) UIRestMovieManager.Instance.HideWindow(true);
        }

        private void ResetPresentation()
        {
            HidePrompt();
            if (_presentedStash != 0) HideStash();
            HideMovie();
            _partyApplied.Clear();
        }

        private void PresentResult(int result, CommandKind kind)
        {
            if (result == (int)Result.Accepted) return;
            if (result == (int)Result.FreeRestAvailable)
            {
                ShowFreeRestStore();
                return;
            }
            if (result >= CannotCampResultBase && result <= CannotCampResultBase + 63)
            {
                ShowCampReason((RestZone.CannotCampReason)(result - CannotCampResultBase));
                return;
            }
            if (result == (int)Result.CannotPurchase)
            {
                if (UIWindowManager.Instance != null)
                    UIWindowManager.ShowMessageBox(UIMessageBox.ButtonStyle.OK, GUIUtils.GetText(754), GUIUtils.GetText(721));
                return;
            }
            string message;
            switch ((Result)result)
            {
                case Result.WrongEpoch: message = "The shared world changed; the rest request was not applied."; break;
                case Result.Busy: message = "Another shared rest is already in progress."; break;
                case Result.NotInitiator: message = "The player who opened this rest must finish or cancel it."; break;
                case Result.NotOwner: message = "You can select a survival bonus only for a character you control."; break;
                case Result.InvalidSelection: message = "That survival bonus is not available to this character."; break;
                case Result.ZoneUnavailable: message = "The party can no longer use that rest zone."; break;
                case Result.NativeFailure: message = "The host could not complete the shared rest."; break;
                default: message = "The shared rest request was not accepted."; break;
            }
            _session.Note(message);
            if (result == (int)Result.WrongEpoch || result == (int)Result.StaleTransaction) _needsResync = true;
            if (kind == CommandKind.StashFinished) _stashFinishSent = 0;
        }

        private static void ShowCampReason(RestZone.CannotCampReason reason)
        {
            string text = "*No Camp Error*";
            if ((reason & RestZone.CannotCampReason.InCombat) != 0) text = GUIUtils.GetText(1349);
            else if ((reason & RestZone.CannotCampReason.NoCampMap) != 0) text = GUIUtils.GetText(1351);
            else if ((reason & RestZone.CannotCampReason.PartyNotGathered) != 0) text = GUIUtils.GetText(1352);
            else if ((reason & RestZone.CannotCampReason.PartyCanSeeEnemy) != 0) text = GUIUtils.GetText(1353);
            else if ((reason & RestZone.CannotCampReason.NoSupplies) != 0) text = GUIUtils.GetText(1350);
            if (UIWindowManager.Instance != null)
                UIWindowManager.ShowMessageBox(UIMessageBox.ButtonStyle.OK, "", text);
        }

        private void ShowFreeRestStore()
        {
            Stronghold stronghold = GameState.Stronghold;
            Vendor vendor = stronghold != null ? stronghold.BrighthollowPrefab : null;
            if (vendor == null || UIStoreManager.Instance == null || UIWindowManager.Instance == null)
            {
                _session.Note("The stronghold rest room list is not available yet.");
                return;
            }
            UIStoreManager.Instance.Page = UIStorePageType.Inn;
            UIWindowManager.Instance.SuspendFor(UIStoreManager.Instance);
            UIStoreManager.Instance.Vendor = vendor;
            UIStoreManager.Instance.ShowWindow();
        }

        private static bool FreeRestAvailable()
        {
            return UIDisableIfFreeRestAvailable.FreeRestAvailable();
        }

        private static bool ValidSurvival(CharacterStats stats, int bonus, int subBonus)
        {
            if (stats == null || bonus < -1 || bonus > MaxSurvivalIndex || subBonus < -1 || subBonus > MaxSurvivalIndex)
                return false;
            if (bonus == -1) return subBonus == -1;
            if (AfflictionData.Instance == null || AfflictionData.Instance.SurvivalCampEffects == null) return false;
            CampEffectList effects = AfflictionData.Instance.SurvivalCampEffects;
            if (effects.BonusesByLevel == null || bonus >= effects.BonusesByLevel.Length || effects.CyclePeriod <= 0)
                return false;
            int survival = stats.CalculateSkill(CharacterStats.SkillType.Survival);
            if (survival <= 0 || !effects.IsBonusValid(bonus, subBonus, survival)) return false;
            return effects.GetBestBonusByIndex(bonus, subBonus, survival) != null;
        }

        private static RestInteraction ResolveZone(Guid id)
        {
            GameObject obj = ResolveObject(id);
            return obj != null ? obj.GetComponent<RestInteraction>() : null;
        }

        private static Inn ResolveInn(Guid id)
        {
            if (id == Guid.Empty)
            {
                Stronghold stronghold = GameState.Stronghold;
                return stronghold != null && stronghold.BrighthollowPrefab != null
                    ? stronghold.BrighthollowPrefab.GetComponent<Inn>() : null;
            }
            GameObject obj = ResolveObject(id);
            return obj != null ? obj.GetComponent<Inn>() : null;
        }

        private static Inn.InnRoom Room(Inn inn, int index)
        {
            return inn != null && inn.Rooms != null && index >= 0 && index < inn.Rooms.Length && index < MaxRooms
                ? inn.Rooms[index] : null;
        }

        private static int RoomIndex(Inn inn, Inn.InnRoom room)
        {
            if (inn == null || inn.Rooms == null || room == null) return -1;
            for (int i = 0; i < inn.Rooms.Length && i < MaxRooms; i++)
                if (inn.Rooms[i] == room) return i;
            return -1;
        }

        private static bool IsBrighthollow(Inn inn)
        {
            Stronghold stronghold = GameState.Stronghold;
            return inn != null && stronghold != null && stronghold.BrighthollowPrefab != null
                && inn.gameObject == stronghold.BrighthollowPrefab.gameObject;
        }

        private static Guid InnId(Inn inn)
        {
            return IsBrighthollow(inn) ? Guid.Empty : NetworkId(inn != null ? inn.gameObject : null);
        }

        private static PartyMemberAI ResolveMember(Guid id)
        {
            PartyMemberAI member = Ownership.Find(id);
            if (member != null) return member;
            GameObject obj = ResolveObject(id);
            return obj != null ? obj.GetComponent<PartyMemberAI>() : null;
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
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj : InstanceID.GetObjectByID(id);
        }

        private static bool ReadFlag(MessageReader reader, string name)
        {
            byte value = reader.Byte();
            if (value > 1) throw new FormatException(name + " flag " + value);
            return value != 0;
        }

        private static bool ValidResult(int result)
        {
            return result >= (int)Result.Accepted && result <= (int)Result.NativeFailure
                || result >= (int)Result.Busy && result <= (int)Result.FreeRestAvailable
                || result >= CannotCampResultBase && result <= CannotCampResultBase + 63;
        }

        private static float BoundedCurrency(float value)
        {
            return !IsFinite(value) ? 0f : Mathf.Clamp(value, 0f, MaxCurrency);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static int NextPositive(int value) => value <= 0 || value == int.MaxValue ? 1 : value + 1;
        private static long NextPositive(long value) => value <= 0 || value == long.MaxValue ? 1 : value + 1;
    }
}
