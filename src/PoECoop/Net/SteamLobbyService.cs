using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Steamworks;

namespace PoECoop.Net
{
    public enum SteamLobbyVisibility
    {
        FriendsOnly,
        Private,
    }

    public enum SteamLobbyStatus
    {
        Unavailable,
        WaitingForSteam,
        Idle,
        Creating,
        Joining,
        Hosting,
        Joined,
    }

    public sealed class SteamFriendInfo
    {
        public CSteamID SteamId { get; }
        public string PersonaName { get; }
        public EPersonaState State { get; }
        public bool IsInGame { get; }
        public uint GameAppId { get; }
        public ulong GameId { get; }
        public bool IsPlayingThisGame => IsInGame && GameAppId == SteamLobbyService.GameAppId;

        internal SteamFriendInfo(CSteamID steamId, string personaName, EPersonaState state,
            bool isInGame, uint gameAppId, ulong gameId)
        {
            SteamId = steamId;
            PersonaName = personaName;
            State = state;
            IsInGame = isInGame;
            GameAppId = gameAppId;
            GameId = gameId;
        }
    }

    /// <summary>
    /// Steam lobby lifetime and discovery. Session transport is started only after a
    /// lobby and its owner have been validated. All methods are main-thread only.
    /// </summary>
    public sealed class SteamLobbyService : IDisposable
    {
        public const uint GameAppId = 291650u;
        public const string ProtocolKey = "poecoop.protocol";
        public const string ModVersionKey = "poecoop.mod";
        public const string GameVersionKey = "poecoop.game";
        public const string NonceKey = "poecoop.nonce";
        public const string VisibilityKey = "poecoop.visibility";

        private const int MaxLobbyMembers = 2;
        private const double OperationTimeoutSeconds = 45.0;
        private const double OwnerCheckIntervalSeconds = 0.5;
        private const double FriendRefreshIntervalSeconds = 2.0;
        private const string FriendsVisibility = "friends";
        private const string PrivateVisibility = "private";

        private enum Operation
        {
            None,
            Host,
            Join,
        }

        private readonly string _modVersion;
        private readonly string _gameVersion;
        private readonly Action _hostSession;
        private readonly Action<CSteamID> _joinSession;
        private readonly Action<string> _log;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SteamFriendInfo> _friends = new List<SteamFriendInfo>();
        private readonly ReadOnlyCollection<SteamFriendInfo> _readOnlyFriends;

        private readonly object _availabilityLock = new object();
        private Type _steamManagerType;
        private FieldInfo _appUsesSteamField;
        private FieldInfo _instanceField;
        private FieldInfo _initializedField;
        private bool _reflectionSearched;
        private string _unavailableReason = "Steam is not initialized";
        private bool _callbacksBroken;

        private CallResult<LobbyCreated_t> _lobbyCreatedResult;
        private CallResult<LobbyEnter_t> _lobbyEnterResult;
        private Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequested;
        private Callback<LobbyInvite_t> _lobbyInvite;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdate;
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdate;
        private Callback<PersonaStateChange_t> _personaStateChange;
        private Callback<FriendRichPresenceUpdate_t> _friendRichPresenceUpdate;
        private Callback<NewUrlLaunchParameters_t> _newUrlLaunchParameters;
        private bool _callbacksReady;

        private Operation _operation;
        private long _generation;
        private CSteamID _operationLobby;
        private SteamLobbyVisibility _pendingVisibility;
        private string _pendingNonce;
        private bool _configuringLobby;
        private bool _sessionActionCalled;
        private CSteamID _localSteamId;
        private ulong? _pendingColdLobby;
        private bool _launchArgsProcessed;
        private double _operationDeadline;
        private double _nextOwnerCheck;
        private double _nextFriendRefresh;
        private bool _friendsDirty = true;
        private bool _disposed;

        public SteamLobbyStatus Status { get; private set; } = SteamLobbyStatus.Unavailable;
        public string StatusText { get; private set; } = "Steam is not initialized";
        public bool IsAvailable { get; private set; }
        public bool IsReady => Status == SteamLobbyStatus.Hosting || Status == SteamLobbyStatus.Joined;
        public bool IsBusy => Status == SteamLobbyStatus.Creating || Status == SteamLobbyStatus.Joining;
        public CSteamID CurrentLobby { get; private set; }
        public CSteamID LobbyOwner { get; private set; }
        public SteamLobbyVisibility? Visibility { get; private set; }
        public string CurrentNonce { get; private set; }
        public IReadOnlyList<SteamFriendInfo> OnlineFriends => _readOnlyFriends;
        public int FriendsVersion { get; private set; }

        /// <summary>Raised after Status or StatusText changes.</summary>
        public event Action StatusChanged;

        /// <summary>
        /// Notification only. Accepting the invite in Steam produces GameLobbyJoinRequested;
        /// this event never joins the lobby itself.
        /// </summary>
        public event Action<CSteamID, CSteamID> LobbyInviteReceived;

        /// <summary>
        /// A ready lobby was cancelled, lost, migrated, or failed validation. The integration
        /// should leave its Session in response. Disposal does not raise this event.
        /// </summary>
        public event Action SessionStopRequested;

        public SteamLobbyService(string modVersion, string gameVersion, Action hostSession,
            Action<CSteamID> joinSession, Action<string> log = null)
        {
            _modVersion = modVersion ?? throw new ArgumentNullException(nameof(modVersion));
            _gameVersion = gameVersion ?? throw new ArgumentNullException(nameof(gameVersion));
            _hostSession = hostSession ?? throw new ArgumentNullException(nameof(hostSession));
            _joinSession = joinSession ?? throw new ArgumentNullException(nameof(joinSession));
            _log = log;
            _readOnlyFriends = _friends.AsReadOnly();

            string[] args = Environment.GetCommandLineArgs();
            if (TryFindConnectLobby(args, out ulong lobby))
            {
                _pendingColdLobby = lobby;
                SetStatus(SteamLobbyStatus.WaitingForSteam,
                    "Waiting for Steam to join lobby " + lobby.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Call once per Unity frame. The game's SteamManager pumps callbacks.</summary>
        public void Update()
        {
            if (_disposed) return;
            if (!PrepareSteam()) return;

            if (!_launchArgsProcessed)
            {
                _launchArgsProcessed = true;
                if (TryReadSteamAppsLobby(out ulong launchLobby)) _pendingColdLobby = launchLobby;
            }

            if (_pendingColdLobby.HasValue)
            {
                ulong lobby = _pendingColdLobby.Value;
                _pendingColdLobby = null;
                Join(lobby);
            }
            else if (!IsBusy && !IsReady && Status != SteamLobbyStatus.Idle)
            {
                SetStatus(SteamLobbyStatus.Idle, "Steam is ready");
            }

            double now = _clock.Elapsed.TotalSeconds;
            if (_operation != Operation.None && now >= _operationDeadline)
            {
                Terminate("Steam did not answer the lobby request in time", true, true);
                return;
            }

            if (CurrentLobby != CSteamID.Nil && !_configuringLobby && now >= _nextOwnerCheck)
            {
                _nextOwnerCheck = now + OwnerCheckIntervalSeconds;
                if (!ValidateCurrentLobby(out _, out _, out _, out string reason))
                {
                    Terminate(reason, true, true);
                    return;
                }
            }

            if (_friendsDirty || now >= _nextFriendRefresh) RefreshFriends();
        }

        public bool Host(SteamLobbyVisibility visibility = SteamLobbyVisibility.FriendsOnly)
        {
            if (_disposed) return false;
            if (IsReady || CurrentLobby != CSteamID.Nil)
            {
                SetStatus(Status, "Leave the current Steam lobby before hosting another");
                return false;
            }
            if (!Enum.IsDefined(typeof(SteamLobbyVisibility), visibility))
            {
                SetStatus(SteamLobbyStatus.Idle, "Unknown Steam lobby visibility");
                return false;
            }

            _pendingColdLobby = null;
            _launchArgsProcessed = true;
            Terminate("Previous Steam lobby request cancelled", true, false);
            if (!PrepareSteam())
            {
                SetUnavailableStatus();
                return false;
            }

            long generation = ++_generation;
            _operation = Operation.Host;
            _operationLobby = CSteamID.Nil;
            _pendingVisibility = visibility;
            _pendingNonce = Guid.NewGuid().ToString("N");
            _operationDeadline = _clock.Elapsed.TotalSeconds + OperationTimeoutSeconds;
            SetStatus(SteamLobbyStatus.Creating, visibility == SteamLobbyVisibility.FriendsOnly
                ? "Creating a friends-only Steam lobby"
                : "Creating a private Steam lobby");

            try
            {
                _lobbyCreatedResult = CallResult<LobbyCreated_t>.Create(
                    (result, ioFailure) => OnLobbyCreated(generation, result, ioFailure));
                ELobbyType type = visibility == SteamLobbyVisibility.FriendsOnly
                    ? ELobbyType.k_ELobbyTypeFriendsOnly
                    : ELobbyType.k_ELobbyTypePrivate;
                SteamAPICall_t call = SteamMatchmaking.CreateLobby(type, MaxLobbyMembers);
                if (call.m_SteamAPICall == 0) throw new InvalidOperationException("CreateLobby returned no API call");
                _lobbyCreatedResult.Set(call);
                return true;
            }
            catch (Exception ex)
            {
                Log("Steam CreateLobby failed: " + ex);
                if (generation == _generation) Terminate("Could not create Steam lobby: " + ex.Message, true, true);
                return false;
            }
        }

        public bool Join(ulong lobbyId) => Join(new CSteamID(lobbyId));

        public bool Join(CSteamID lobby)
        {
            if (_disposed || lobby == CSteamID.Nil) return false;
            if (IsReady || CurrentLobby != CSteamID.Nil)
            {
                SetStatus(Status, "Leave the current Steam lobby before joining another");
                return false;
            }
            if (_operation == Operation.Join && _operationLobby == lobby) return true;

            _pendingColdLobby = null;
            _launchArgsProcessed = true;
            Terminate("Previous Steam lobby request cancelled", true, false);
            if (!PrepareSteam())
            {
                SetUnavailableStatus();
                return false;
            }

            long generation = ++_generation;
            _operation = Operation.Join;
            _operationLobby = lobby;
            _operationDeadline = _clock.Elapsed.TotalSeconds + OperationTimeoutSeconds;
            SetStatus(SteamLobbyStatus.Joining, "Joining Steam lobby " + LobbyIdText(lobby));

            try
            {
                _lobbyEnterResult = CallResult<LobbyEnter_t>.Create(
                    (result, ioFailure) => OnLobbyEntered(generation, result, ioFailure));
                SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobby);
                if (call.m_SteamAPICall == 0) throw new InvalidOperationException("JoinLobby returned no API call");
                _lobbyEnterResult.Set(call);
                return true;
            }
            catch (Exception ex)
            {
                Log("Steam JoinLobby failed: " + ex);
                if (generation == _generation) Terminate("Could not join Steam lobby: " + ex.Message, true, true);
                return false;
            }
        }

        /// <summary>Cancels pending work and leaves the current lobby, but never shuts down Steam.</summary>
        public void Cancel()
        {
            if (_disposed) return;
            _pendingColdLobby = null;
            _launchArgsProcessed = true;
            Terminate("Steam lobby closed", true, true);
        }

        public bool InviteUserToLobby(CSteamID friend)
        {
            if (!CanInvite(friend)) return false;
            try
            {
                bool sent = SteamMatchmaking.InviteUserToLobby(CurrentLobby, friend);
                SetStatus(Status, sent
                    ? "Steam invitation sent to " + FriendName(friend)
                    : "Steam could not send the invitation");
                return sent;
            }
            catch (Exception ex)
            {
                Log("Steam InviteUserToLobby failed: " + ex);
                SetStatus(Status, "Steam could not send the invitation: " + ex.Message);
                return false;
            }
        }

        public bool ActivateGameOverlayInviteDialog()
        {
            if (_disposed || !IsAvailable || Status != SteamLobbyStatus.Hosting
                || CurrentLobby == CSteamID.Nil || LobbyOwner != _localSteamId) return false;
            try
            {
                SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
                return true;
            }
            catch (Exception ex)
            {
                Log("Steam invite overlay failed: " + ex);
                SetStatus(Status, "Could not open the Steam invite overlay: " + ex.Message);
                return false;
            }
        }

        public bool RefreshFriends()
        {
            if (_disposed || !IsAvailable) return false;
            try
            {
                var refreshed = new List<SteamFriendInfo>();
                int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < count; i++)
                {
                    CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    EPersonaState state = SteamFriends.GetFriendPersonaState(id);
                    if (state == EPersonaState.k_EPersonaStateOffline) continue;

                    string name = Protocol.Clean(SteamFriends.GetFriendPersonaName(id), 128);
                    if (name.Length == 0) name = id.ToString();
                    bool inGame = SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game);
                    ulong gameId = inGame ? game.m_gameID.m_GameID : 0UL;
                    uint appId = inGame ? unchecked((uint)(gameId & 0xFFFFFFUL)) : 0u;
                    refreshed.Add(new SteamFriendInfo(id, name, state, inGame, appId, gameId));
                }
                refreshed.Sort(CompareFriends);
                _friends.Clear();
                _friends.AddRange(refreshed);
                FriendsVersion++;
                _friendsDirty = false;
                _nextFriendRefresh = _clock.Elapsed.TotalSeconds + FriendRefreshIntervalSeconds;
                return true;
            }
            catch (Exception ex)
            {
                Log("Steam friend refresh failed: " + ex);
                _nextFriendRefresh = _clock.Elapsed.TotalSeconds + FriendRefreshIntervalSeconds;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Terminate("Steam lobby service disposed", IsAvailable, false);
            _disposed = true;
            IsAvailable = false;
            DisposeCallbacks();
            _friends.Clear();
            FriendsVersion++;
            Status = SteamLobbyStatus.Unavailable;
            StatusText = "Steam lobby service disposed";
        }

        private bool CanInvite(CSteamID friend)
        {
            if (_disposed || !IsAvailable || Status != SteamLobbyStatus.Hosting
                || CurrentLobby == CSteamID.Nil || LobbyOwner != _localSteamId
                || friend == CSteamID.Nil || friend == _localSteamId) return false;
            return true;
        }

        private static int CompareFriends(SteamFriendInfo a, SteamFriendInfo b)
        {
            int game = b.IsPlayingThisGame.CompareTo(a.IsPlayingThisGame);
            if (game != 0) return game;
            int name = string.Compare(a.PersonaName, b.PersonaName, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : a.SteamId.CompareTo(b.SteamId);
        }

        private bool PrepareSteam()
        {
            if (TryEnsureSteam()) return true;
            IsAvailable = false;
            if (_callbacksReady) DisposeCallbacks();
            if (_friends.Count > 0)
            {
                _friends.Clear();
                FriendsVersion++;
            }
            _friendsDirty = true;
            if (_operation != Operation.None || CurrentLobby != CSteamID.Nil)
                Terminate("Steam became unavailable", false, true);
            SetUnavailableStatus();
            return false;
        }

        private bool TryEnsureSteam()
        {
            if (_callbacksBroken || !ReadSteamManagerInitialized())
            {
                IsAvailable = false;
                return false;
            }

            try
            {
                _localSteamId = SteamUser.GetSteamID();
                if (_localSteamId == CSteamID.Nil || !_localSteamId.IsValid())
                {
                    _unavailableReason = "Steam has no signed-in user";
                    IsAvailable = false;
                    return false;
                }
                if (!_callbacksReady) CreateCallbacks();
                IsAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                _callbacksBroken = true;
                _unavailableReason = "Steam callbacks could not be registered: " + ex.Message;
                IsAvailable = false;
                Log(_unavailableReason + Environment.NewLine + ex);
                DisposeCallbacks();
                return false;
            }
        }

        private bool ReadSteamManagerInitialized()
        {
            lock (_availabilityLock)
            {
                try
                {
                    if (!_reflectionSearched)
                    {
                        _reflectionSearched = true;
                        _steamManagerType = Type.GetType("SteamManager, Assembly-CSharp", false);
                        if (_steamManagerType != null)
                        {
                            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
                            _appUsesSteamField = _steamManagerType.GetField("m_appUsesSteam", flags);
                            _instanceField = _steamManagerType.GetField("s_instance", flags);
                            _initializedField = _steamManagerType.GetField("m_bInitialized", flags);
                        }
                    }

                    if (_steamManagerType == null || _appUsesSteamField == null
                        || _instanceField == null || _initializedField == null)
                    {
                        _unavailableReason = "The game's SteamManager is unavailable";
                        return false;
                    }
                    if (!(bool)_appUsesSteamField.GetValue(null))
                    {
                        _unavailableReason = "The game was not launched with Steam enabled";
                        return false;
                    }
                    object instance = _instanceField.GetValue(null);
                    if (instance == null || instance is UnityEngine.Object unityObject && unityObject == null)
                    {
                        _unavailableReason = "The game has not initialized Steam";
                        return false;
                    }
                    if (!(bool)_initializedField.GetValue(instance))
                    {
                        _unavailableReason = "The game has not initialized Steam";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _unavailableReason = "Could not inspect the game's SteamManager: " + ex.Message;
                    return false;
                }
            }
        }

        private void CreateCallbacks()
        {
            _gameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _lobbyInvite = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
            _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _lobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
            _personaStateChange = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
            _friendRichPresenceUpdate = Callback<FriendRichPresenceUpdate_t>.Create(OnFriendRichPresenceUpdate);
            _newUrlLaunchParameters = Callback<NewUrlLaunchParameters_t>.Create(OnNewUrlLaunchParameters);
            _callbacksReady = true;
        }

        private void SetUnavailableStatus()
        {
            if (_pendingColdLobby.HasValue)
                SetStatus(SteamLobbyStatus.WaitingForSteam,
                    "Waiting for Steam to join lobby " + _pendingColdLobby.Value.ToString(CultureInfo.InvariantCulture));
            else
                SetStatus(SteamLobbyStatus.Unavailable, _unavailableReason);
        }

        private void OnLobbyCreated(long generation, LobbyCreated_t result, bool ioFailure)
        {
            if (generation != _generation || _operation != Operation.Host) return;
            try
            {
                var lobby = new CSteamID(result.m_ulSteamIDLobby);
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || lobby == CSteamID.Nil)
                {
                    Terminate("Steam could not create a lobby: " + result.m_eResult, true, true);
                    return;
                }

                _configuringLobby = true;
                CurrentLobby = lobby;
                Visibility = _pendingVisibility;
                CurrentNonce = _pendingNonce;
                ELobbyType type = _pendingVisibility == SteamLobbyVisibility.FriendsOnly
                    ? ELobbyType.k_ELobbyTypeFriendsOnly
                    : ELobbyType.k_ELobbyTypePrivate;
                string visibility = _pendingVisibility == SteamLobbyVisibility.FriendsOnly
                    ? FriendsVisibility : PrivateVisibility;

                bool configured = SteamMatchmaking.SetLobbyType(lobby, type)
                    && SteamMatchmaking.SetLobbyMemberLimit(lobby, MaxLobbyMembers)
                    && SteamMatchmaking.SetLobbyJoinable(lobby, true)
                    && SteamMatchmaking.SetLobbyData(lobby, ProtocolKey,
                        Protocol.Version.ToString(CultureInfo.InvariantCulture))
                    && SteamMatchmaking.SetLobbyData(lobby, ModVersionKey, _modVersion)
                    && SteamMatchmaking.SetLobbyData(lobby, GameVersionKey, _gameVersion)
                    && SteamMatchmaking.SetLobbyData(lobby, NonceKey, CurrentNonce)
                    && SteamMatchmaking.SetLobbyData(lobby, VisibilityKey, visibility);
                if (!configured)
                {
                    Terminate("Steam refused the co-op lobby settings", true, true);
                    return;
                }
                if (!ValidateCurrentLobby(out CSteamID owner, out _, out _, out string reason))
                {
                    Terminate(reason, true, true);
                    return;
                }
                LobbyOwner = owner;
                _configuringLobby = false;
                CompleteSession(generation, true, owner);
            }
            catch (Exception ex)
            {
                Log("Steam lobby creation callback failed: " + ex);
                if (generation == _generation) Terminate("Could not finish the Steam lobby: " + ex.Message, true, true);
            }
        }

        private void OnLobbyEntered(long generation, LobbyEnter_t result, bool ioFailure)
        {
            if (generation != _generation || _operation != Operation.Join) return;
            try
            {
                var lobby = new CSteamID(result.m_ulSteamIDLobby);
                var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
                if (ioFailure || response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                    || lobby == CSteamID.Nil || lobby != _operationLobby)
                {
                    Terminate("Could not enter Steam lobby: " + JoinFailureText(response), true, true);
                    return;
                }

                _configuringLobby = true;
                CurrentLobby = lobby;
                if (!ValidateCurrentLobby(out CSteamID owner, out SteamLobbyVisibility visibility,
                    out string nonce, out string reason))
                {
                    Terminate(reason, true, true);
                    return;
                }
                LobbyOwner = owner;
                Visibility = visibility;
                CurrentNonce = nonce;
                _configuringLobby = false;
                CompleteSession(generation, false, owner);
            }
            catch (Exception ex)
            {
                Log("Steam lobby enter callback failed: " + ex);
                if (generation == _generation) Terminate("Could not finish joining the Steam lobby: " + ex.Message, true, true);
            }
        }

        private void CompleteSession(long generation, bool host, CSteamID owner)
        {
            if (generation != _generation || _disposed) return;
            _operation = Operation.None;
            _operationLobby = CSteamID.Nil;
            _sessionActionCalled = true;
            SetStatus(host ? SteamLobbyStatus.Hosting : SteamLobbyStatus.Joined,
                host ? "Steam lobby ready" : "Joined " + FriendName(owner) + "'s Steam lobby", false);
            try
            {
                if (host) _hostSession();
                else _joinSession(owner);
            }
            catch (Exception ex)
            {
                Log("Session start after Steam lobby failed: " + ex);
                if (generation == _generation) Terminate("Could not start the co-op session: " + ex.Message, true, true);
                return;
            }
            if (generation == _generation) NotifyStatusChanged();
        }

        private bool ValidateCurrentLobby(out CSteamID owner, out SteamLobbyVisibility visibility,
            out string nonce, out string reason)
        {
            owner = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
            visibility = Visibility ?? SteamLobbyVisibility.FriendsOnly;
            nonce = null;
            reason = null;
            if (CurrentLobby == CSteamID.Nil || owner == CSteamID.Nil || !owner.IsValid())
            {
                reason = "The Steam lobby has no valid owner";
                return false;
            }

            bool hosting = _operation == Operation.Host || Status == SteamLobbyStatus.Hosting;
            if (hosting && owner != _localSteamId)
            {
                reason = "Steam lobby ownership moved; hosting was stopped";
                return false;
            }
            if (!hosting && owner == _localSteamId)
            {
                reason = "Refusing to join a lobby owned by this Steam user";
                return false;
            }
            if (LobbyOwner != CSteamID.Nil && owner != LobbyOwner)
            {
                reason = "Steam lobby ownership changed; migration is not supported";
                return false;
            }

            string protocol = SteamMatchmaking.GetLobbyData(CurrentLobby, ProtocolKey);
            string mod = SteamMatchmaking.GetLobbyData(CurrentLobby, ModVersionKey);
            string game = SteamMatchmaking.GetLobbyData(CurrentLobby, GameVersionKey);
            nonce = SteamMatchmaking.GetLobbyData(CurrentLobby, NonceKey);
            string visibilityData = SteamMatchmaking.GetLobbyData(CurrentLobby, VisibilityKey);
            string expectedProtocol = Protocol.Version.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(protocol, expectedProtocol, StringComparison.Ordinal))
            {
                reason = "Host protocol " + DisplayMetadata(protocol) + " does not match local protocol " + expectedProtocol;
                return false;
            }
            if (!string.Equals(mod, _modVersion, StringComparison.Ordinal))
            {
                reason = "Host mod version " + DisplayMetadata(mod) + " does not match local version " + _modVersion;
                return false;
            }
            if (!string.Equals(game, _gameVersion, StringComparison.Ordinal))
            {
                reason = "Host game version " + DisplayMetadata(game) + " does not match local version " + _gameVersion;
                return false;
            }
            if (!Guid.TryParseExact(nonce, "N", out _))
            {
                reason = "The Steam lobby has no valid co-op nonce";
                return false;
            }
            if (visibilityData == FriendsVisibility) visibility = SteamLobbyVisibility.FriendsOnly;
            else if (visibilityData == PrivateVisibility) visibility = SteamLobbyVisibility.Private;
            else
            {
                reason = "The Steam lobby has an unknown visibility";
                return false;
            }
            if (Visibility.HasValue && visibility != Visibility.Value)
            {
                reason = "The Steam lobby visibility changed unexpectedly";
                return false;
            }
            if (CurrentNonce != null && !string.Equals(nonce, CurrentNonce, StringComparison.Ordinal))
            {
                reason = "The Steam lobby nonce changed unexpectedly";
                return false;
            }
            return true;
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            if (!_disposed) Join(request.m_steamIDLobby);
        }

        private void OnLobbyInvite(LobbyInvite_t invite)
        {
            if (_disposed) return;
            var lobby = new CSteamID(invite.m_ulSteamIDLobby);
            var inviter = new CSteamID(invite.m_ulSteamIDUser);
            if (lobby == CSteamID.Nil) return;
            if (IsAvailable && Status == SteamLobbyStatus.Idle)
                SetStatus(Status, "Steam invitation from " + FriendName(inviter) + "; accept it in Steam to join");
            Log("Steam lobby invitation received from " + inviter + "; no automatic join performed");
            try { LobbyInviteReceived?.Invoke(lobby, inviter); }
            catch (Exception ex) { Log("LobbyInviteReceived handler failed: " + ex); }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (_disposed || _configuringLobby || CurrentLobby == CSteamID.Nil
                || update.m_ulSteamIDLobby != CurrentLobby.m_SteamID) return;
            try
            {
                var changed = new CSteamID(update.m_ulSteamIDUserChanged);
                var flags = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
                if (changed == LobbyOwner && (flags & (EChatMemberStateChange.k_EChatMemberStateChangeLeft
                    | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                    | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                    | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0)
                {
                    Terminate("The Steam lobby owner left", true, true);
                    return;
                }
                if (changed == _localSteamId && (flags & (EChatMemberStateChange.k_EChatMemberStateChangeLeft
                    | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                    | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                    | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0)
                {
                    Terminate("This user left or was removed from the Steam lobby", false, true);
                    return;
                }
                if (!ValidateCurrentLobby(out _, out _, out _, out string reason)) Terminate(reason, true, true);
            }
            catch (Exception ex)
            {
                Log("Steam lobby membership callback failed: " + ex);
                Terminate("Steam lobby membership validation failed: " + ex.Message, true, true);
            }
        }

        private void OnLobbyDataUpdate(LobbyDataUpdate_t update)
        {
            if (_disposed || _configuringLobby || CurrentLobby == CSteamID.Nil
                || update.m_ulSteamIDLobby != CurrentLobby.m_SteamID) return;
            try
            {
                if (update.m_bSuccess == 0)
                {
                    Terminate("Steam lobby metadata became unavailable", true, true);
                    return;
                }
                if (!ValidateCurrentLobby(out _, out _, out _, out string reason)) Terminate(reason, true, true);
            }
            catch (Exception ex)
            {
                Log("Steam lobby data callback failed: " + ex);
                Terminate("Steam lobby metadata validation failed: " + ex.Message, true, true);
            }
        }

        private void OnPersonaStateChange(PersonaStateChange_t change) => _friendsDirty = true;

        private void OnFriendRichPresenceUpdate(FriendRichPresenceUpdate_t update) => _friendsDirty = true;

        private void OnNewUrlLaunchParameters(NewUrlLaunchParameters_t unused)
        {
            if (!_disposed && TryReadSteamAppsLobby(out ulong lobby)) Join(lobby);
        }

        private bool TryReadSteamAppsLobby(out ulong lobby)
        {
            lobby = 0;
            try
            {
                int length = SteamApps.GetLaunchCommandLine(out string commandLine, 2048);
                if (length >= 2048) length = SteamApps.GetLaunchCommandLine(out commandLine, 16384);
                return length > 0 && TryFindConnectLobby(Tokenize(commandLine), out lobby);
            }
            catch (Exception ex)
            {
                Log("SteamApps.GetLaunchCommandLine failed: " + ex);
                return false;
            }
        }

        private static bool TryFindConnectLobby(IList<string> args, out ulong lobby)
        {
            lobby = 0;
            if (args == null) return false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == null) continue;
                string value = null;
                if (arg.StartsWith("+connect_lobby=", StringComparison.OrdinalIgnoreCase))
                    value = arg.Substring("+connect_lobby=".Length);
                else if (string.Equals(arg, "+connect_lobby", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                    value = args[++i];
                if (value != null && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out lobby)
                    && lobby != 0) return true;
            }
            lobby = 0;
            return false;
        }

        private static IList<string> Tokenize(string commandLine)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return tokens;
            var token = new StringBuilder();
            bool quoted = false;
            foreach (char c in commandLine)
            {
                if (c == '"') quoted = !quoted;
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (token.Length > 0)
                    {
                        tokens.Add(token.ToString());
                        token.Clear();
                    }
                }
                else token.Append(c);
            }
            if (token.Length > 0) tokens.Add(token.ToString());
            return tokens;
        }

        private void Terminate(string message, bool leaveSteam, bool notifyStop)
        {
            ++_generation;
            bool stopSession = notifyStop && _sessionActionCalled && IsReady;
            CSteamID lobby = CurrentLobby;
            if (lobby == CSteamID.Nil && _operation == Operation.Join) lobby = _operationLobby;
            _operation = Operation.None;
            _operationLobby = CSteamID.Nil;
            _configuringLobby = false;
            _sessionActionCalled = false;
            DisposeCallResults();
            CurrentLobby = CSteamID.Nil;
            LobbyOwner = CSteamID.Nil;
            Visibility = null;
            CurrentNonce = null;
            _pendingNonce = null;

            if (leaveSteam && IsAvailable && lobby != CSteamID.Nil)
            {
                try { SteamMatchmaking.LeaveLobby(lobby); }
                catch (Exception ex) { Log("Steam LeaveLobby failed: " + ex); }
            }

            SetStatus(IsAvailable ? SteamLobbyStatus.Idle : SteamLobbyStatus.Unavailable, message);
            if (stopSession)
            {
                try { SessionStopRequested?.Invoke(); }
                catch (Exception ex) { Log("SessionStopRequested handler failed: " + ex); }
            }
        }

        private void DisposeCallResults()
        {
            _lobbyCreatedResult?.Dispose();
            _lobbyCreatedResult = null;
            _lobbyEnterResult?.Dispose();
            _lobbyEnterResult = null;
        }

        private void DisposeCallbacks()
        {
            DisposeCallResults();
            _gameLobbyJoinRequested?.Dispose();
            _gameLobbyJoinRequested = null;
            _lobbyInvite?.Dispose();
            _lobbyInvite = null;
            _lobbyChatUpdate?.Dispose();
            _lobbyChatUpdate = null;
            _lobbyDataUpdate?.Dispose();
            _lobbyDataUpdate = null;
            _personaStateChange?.Dispose();
            _personaStateChange = null;
            _friendRichPresenceUpdate?.Dispose();
            _friendRichPresenceUpdate = null;
            _newUrlLaunchParameters?.Dispose();
            _newUrlLaunchParameters = null;
            _callbacksReady = false;
        }

        private void SetStatus(SteamLobbyStatus status, string text, bool notify = true)
        {
            bool changed = Status != status || !string.Equals(StatusText, text, StringComparison.Ordinal);
            Status = status;
            StatusText = text;
            if (changed)
            {
                Log(text);
                if (notify) NotifyStatusChanged();
            }
        }

        private void NotifyStatusChanged()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Log("StatusChanged handler failed: " + ex); }
        }

        private string FriendName(CSteamID id)
        {
            if (!IsAvailable || id == CSteamID.Nil) return "unknown host";
            try
            {
                string name = Protocol.Clean(SteamFriends.GetFriendPersonaName(id), 128);
                return name.Length > 0 ? name : id.ToString();
            }
            catch { return id.ToString(); }
        }

        private static string LobbyIdText(CSteamID lobby) => lobby.m_SteamID.ToString(CultureInfo.InvariantCulture);
        private static string DisplayMetadata(string value) => string.IsNullOrEmpty(value) ? "(missing)" : value;

        private static string JoinFailureText(EChatRoomEnterResponse response)
        {
            switch (response)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist: return "the lobby no longer exists";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed: return "joining is not allowed";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull: return "the lobby is full";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseError: return "an unexpected Steam error";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned: return "this user is banned from the lobby";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited: return "the lobby owner has limited permissions";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseClanDisabled: return "the clan lobby is disabled";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseCommunityBan: return "a community ban prevents joining";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseMemberBlockedYou: return "a lobby member has blocked this user";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseYouBlockedMember: return "this user has blocked a lobby member";
                default: return response.ToString();
            }
        }

        private void Log(string text)
        {
            try { _log?.Invoke("Steam: " + text); }
            catch { }
        }
    }
}
