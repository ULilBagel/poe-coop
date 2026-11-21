using System;
using Steamworks;

namespace PoECoop.Net
{
    /// <summary>
    /// Main-thread Steam adapter. All methods and events are synchronous from the
    /// transport's point of view; asynchronous Steam results are delivered by events.
    /// </summary>
    internal interface ISteamBackend : IDisposable
    {
        bool IsAvailable { get; }
        CSteamID LocalUserId { get; }

        void AllowP2PPacketRelay();

        event Action<CSteamID, bool, string> LobbyCreated;
        event Action<CSteamID, bool, string> LobbyJoined;
        event Action<CSteamID, CSteamID> LobbyMembershipChanged;
        event Action<CSteamID, CSteamID> LobbyJoinRequested;
        event Action<CSteamID> P2PSessionRequested;
        event Action<CSteamID, byte> P2PSessionFailed;

        void CreateLobby(int maxMembers);
        void JoinLobby(CSteamID lobby);
        void CancelLobbyOperation();
        void LeaveLobby(CSteamID lobby);
        bool InviteUserToLobby(CSteamID lobby, CSteamID invitee);
        CSteamID GetLobbyOwner(CSteamID lobby);
        int GetLobbyMemberCount(CSteamID lobby);
        CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index);

        bool AcceptP2PSession(CSteamID remote);
        void CloseP2PSession(CSteamID remote);
        bool SendPacket(CSteamID remote, byte[] data, int channel);
        bool IsPacketAvailable(out uint size, int channel);
        bool ReadPacket(byte[] destination, out uint size, out CSteamID remote, int channel);
    }

    /// <summary>Steamworks.NET 20.2.0 / legacy ISteamNetworking implementation.</summary>
    internal sealed class SteamworksSteamBackend : ISteamBackend
    {
        private readonly Callback<P2PSessionRequest_t> _sessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> _sessionFailure;
        private readonly Callback<LobbyChatUpdate_t> _lobbyUpdate;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly CallResult<LobbyCreated_t> _createResult;
        private readonly CallResult<LobbyEnter_t> _joinResult;
        private bool _disposed;

        public SteamworksSteamBackend()
        {
            _sessionRequest = Callback<P2PSessionRequest_t>.Create(
                result => P2PSessionRequested?.Invoke(result.m_steamIDRemote));
            _sessionFailure = Callback<P2PSessionConnectFail_t>.Create(
                result => P2PSessionFailed?.Invoke(result.m_steamIDRemote, result.m_eP2PSessionError));
            _lobbyUpdate = Callback<LobbyChatUpdate_t>.Create(result =>
                LobbyMembershipChanged?.Invoke(
                    new CSteamID(result.m_ulSteamIDLobby),
                    new CSteamID(result.m_ulSteamIDUserChanged)));
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(result =>
                LobbyJoinRequested?.Invoke(result.m_steamIDLobby, result.m_steamIDFriend));
            _createResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _joinResult = CallResult<LobbyEnter_t>.Create(OnLobbyJoined);
        }

        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                try
                {
                    return SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
                }
                catch
                {
                    return false;
                }
            }
        }

        public CSteamID LocalUserId => SteamUser.GetSteamID();

        public event Action<CSteamID, bool, string> LobbyCreated;
        public event Action<CSteamID, bool, string> LobbyJoined;
        public event Action<CSteamID, CSteamID> LobbyMembershipChanged;
        public event Action<CSteamID, CSteamID> LobbyJoinRequested;
        public event Action<CSteamID> P2PSessionRequested;
        public event Action<CSteamID, byte> P2PSessionFailed;

        public void AllowP2PPacketRelay() => SteamNetworking.AllowP2PPacketRelay(true);

        public void CreateLobby(int maxMembers)
        {
            CancelLobbyOperation();
            AllowP2PPacketRelay();
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers);
            if (call == SteamAPICall_t.Invalid)
                LobbyCreated?.Invoke(CSteamID.Nil, false, "Steam did not start lobby creation");
            else
                _createResult.Set(call);
        }

        public void JoinLobby(CSteamID lobby)
        {
            CancelLobbyOperation();
            AllowP2PPacketRelay();
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobby);
            if (call == SteamAPICall_t.Invalid)
                LobbyJoined?.Invoke(lobby, false, "Steam did not start joining the lobby");
            else
                _joinResult.Set(call);
        }

        public void CancelLobbyOperation()
        {
            _createResult.Cancel();
            _joinResult.Cancel();
        }

        public void LeaveLobby(CSteamID lobby)
        {
            if (lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(lobby);
        }

        public bool InviteUserToLobby(CSteamID lobby, CSteamID invitee)
            => SteamMatchmaking.InviteUserToLobby(lobby, invitee);

        public CSteamID GetLobbyOwner(CSteamID lobby) => SteamMatchmaking.GetLobbyOwner(lobby);

        public int GetLobbyMemberCount(CSteamID lobby) => SteamMatchmaking.GetNumLobbyMembers(lobby);

        public CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index)
            => SteamMatchmaking.GetLobbyMemberByIndex(lobby, index);

        public bool AcceptP2PSession(CSteamID remote) => SteamNetworking.AcceptP2PSessionWithUser(remote);

        public void CloseP2PSession(CSteamID remote)
        {
            try { SteamNetworking.CloseP2PChannelWithUser(remote, SteamTransport.GameplayChannel); } catch { }
            try { SteamNetworking.CloseP2PChannelWithUser(remote, SteamTransport.ControlChannel); } catch { }
            try { SteamNetworking.CloseP2PSessionWithUser(remote); } catch { }
        }

        public bool SendPacket(CSteamID remote, byte[] data, int channel)
            => SteamNetworking.SendP2PPacket(
                remote, data, (uint)data.Length, EP2PSend.k_EP2PSendReliable, channel);

        public bool IsPacketAvailable(out uint size, int channel)
            => SteamNetworking.IsP2PPacketAvailable(out size, channel);

        public bool ReadPacket(byte[] destination, out uint size, out CSteamID remote, int channel)
            => SteamNetworking.ReadP2PPacket(
                destination, (uint)destination.Length, out size, out remote, channel);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _createResult.Dispose();
            _joinResult.Dispose();
            _sessionRequest.Dispose();
            _sessionFailure.Dispose();
            _lobbyUpdate.Dispose();
            _joinRequested.Dispose();
            LobbyCreated = null;
            LobbyJoined = null;
            LobbyMembershipChanged = null;
            LobbyJoinRequested = null;
            P2PSessionRequested = null;
            P2PSessionFailed = null;
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (_disposed) return;
            bool success = !ioFailure && result.m_eResult == EResult.k_EResultOK
                && result.m_ulSteamIDLobby != 0;
            string reason = ioFailure ? "Steam I/O failure" : result.m_eResult.ToString();
            LobbyCreated?.Invoke(new CSteamID(result.m_ulSteamIDLobby), success, success ? null : reason);
        }

        private void OnLobbyJoined(LobbyEnter_t result, bool ioFailure)
        {
            if (_disposed) return;
            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            bool success = !ioFailure && !result.m_bLocked
                && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                && result.m_ulSteamIDLobby != 0;
            string reason = ioFailure
                ? "Steam I/O failure"
                : result.m_bLocked ? "Lobby is locked" : response.ToString();
            LobbyJoined?.Invoke(new CSteamID(result.m_ulSteamIDLobby), success, success ? null : reason);
        }
    }
}
