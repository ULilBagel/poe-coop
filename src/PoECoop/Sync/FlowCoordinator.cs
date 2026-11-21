using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Correlates save-backed world handoffs with a monotonically changing world
    /// epoch. Existing peers keep their ready world when another guest joins;
    /// a real host level load starts a new epoch and readies every peer again.
    /// </summary>
    public sealed class FlowCoordinator : IDisposable
    {
        private static FlowCoordinator s_instance;
        private static long s_lastEpoch;

        private sealed class PeerWorld
        {
            public long Epoch;
            public int TransferId;
            public string Scene;
            public bool Applying;
            public bool Ready;
            public bool Failed;
        }

        private readonly Session _session;
        private readonly Dictionary<int, PeerWorld> _peers = new Dictionary<int, PeerWorld>();
        private long _generation;
        private long _worldEpoch;
        private int _currentTransfer;
        private long _stateSeq;
        private long _readyStateSeq = -1;
        private string _scene = "";
        private bool _barrier;
        private bool _restoreInputBlock;

        public static long WorldEpoch => s_instance != null ? s_instance._worldEpoch : 0;
        public static int CurrentTransfer => s_instance != null ? s_instance._currentTransfer : 0;
        public static bool ApplyingWorldBarrier => s_instance != null && s_instance._barrier;
        public static bool IsInitialized => s_instance != null;

        public FlowCoordinator(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            if (s_instance != null) throw new InvalidOperationException("FlowCoordinator is already initialized");
            s_instance = this;
            _generation = session.Generation;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.SessionReset += OnSessionReset;
            GameState.OnLevelLoaded += OnLevelLoaded;
            GameState.OnLevelUnload += OnLevelUnload;
        }

        public void Update(float now)
        {
            SyncSession();
            if (_session.IsHost && _worldEpoch == 0) InitializeHostWorld();
            UpdateBarrier();
            if (_barrier)
            {
                GameInput.BeginBlockAllKeys();
                Time.timeScale = 0f;
            }
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelUnload;
            _session.SessionReset -= OnSessionReset;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.GameMessage -= OnGameMessage;
            SetBarrier(false);
            if (s_instance == this) s_instance = null;
            _peers.Clear();
            _worldEpoch = 0;
            _currentTransfer = 0;
        }

        public static bool IsPeerReady(int playerId)
        {
            FlowCoordinator self = s_instance;
            return self != null && self._session.IsPeerReady(playerId);
        }

        public static void ForceFullState()
        {
            FlowCoordinator self = s_instance;
            if (self != null && self._session.IsHost && self._worldEpoch > 0)
                self._session.RequestFullState(-1, self._worldEpoch);
        }

        public static void ForceFullState(int playerId)
        {
            FlowCoordinator self = s_instance;
            if (self != null && self._session.IsHost && self._worldEpoch > 0)
                self._session.RequestFullState(playerId, self._worldEpoch);
        }

        /// <summary>Host: associate the legacy save transfer with its world before the first SaveBegin.</summary>
        public static bool BeginTransfer(int playerId, int transferId, string scene)
        {
            FlowCoordinator self = s_instance;
            if (self == null || !self._session.IsHost || self._worldEpoch <= 0 || transferId <= 0) return false;
            PlayerInfo player = self._session.Players.Find(p => p.Id == playerId && !p.IsHost);
            if (player == null) return false;
            scene = Protocol.Clean(scene, FlowWire.MaxSceneLength);
            if (scene.Length == 0) return false;

            if (!self._peers.TryGetValue(playerId, out PeerWorld peer))
                self._peers[playerId] = peer = new PeerWorld();
            peer.Epoch = self._worldEpoch;
            peer.TransferId = transferId;
            peer.Scene = scene;
            peer.Applying = true;
            peer.Ready = false;
            peer.Failed = false;
            self._currentTransfer = transferId;
            self._session.SetPeerWorldReady(playerId, false);
            self.UpdateBarrier();
            self._session.SendToPlayer(playerId, FlowWire.WriteWorldState(
                self._worldEpoch, transferId, 0, WorldStateKind.Handoff, scene).ToArray());
            return true;
        }

        /// <summary>Host: release a failed handoff without granting readiness or changing the world epoch.</summary>
        public static void AbortHandoff(int playerId, string problem)
        {
            FlowCoordinator self = s_instance;
            if (self == null || !self._session.IsHost || !self._peers.TryGetValue(playerId, out PeerWorld peer)) return;
            peer.Applying = false;
            peer.Ready = false;
            peer.Failed = true;
            self._session.SetPeerWorldReady(playerId, false);
            self.UpdateBarrier();
        }

        /// <summary>Guest: SaveBegin is accepted only after its correlated WorldState marker.</summary>
        public static bool CanAcceptTransfer(int transferId)
        {
            FlowCoordinator self = s_instance;
            return self != null && self._session.IsGuest && self._worldEpoch > 0
                && transferId > 0 && self._currentTransfer == transferId && !self._session.IsWorldReady;
        }

        public static bool IsCurrentTransfer(int transferId, long worldEpoch)
        {
            FlowCoordinator self = s_instance;
            return self != null && self._session.IsGuest && self._currentTransfer == transferId
                && self._worldEpoch == worldEpoch;
        }

        public static void CompleteGuestLoad(int transferId, long worldEpoch, string scene)
        {
            FlowCoordinator self = s_instance;
            if (self == null || !IsCurrentTransfer(transferId, worldEpoch)) return;
            self._session.Note("World load complete; reporting ready for epoch " + worldEpoch
                + ", transfer " + transferId + ", scene " + Protocol.Clean(scene, FlowWire.MaxSceneLength));
            self._session.SendToHost(FlowWire.WriteWorldReady(
                worldEpoch, transferId, FlowResultCode.Ok, scene, "").ToArray());
        }

        public static void FailGuestLoad(int transferId, long worldEpoch, string problem)
        {
            FlowCoordinator self = s_instance;
            if (self == null || !IsCurrentTransfer(transferId, worldEpoch)) return;
            problem = Protocol.Clean(problem, FlowWire.MaxProblemLength);
            if (problem.Length == 0) problem = "the guest could not load the world";
            self._session.SendToHost(FlowWire.WriteWorldReady(
                worldEpoch, transferId, FlowResultCode.Failed, "", problem).ToArray());
        }

        private void InitializeHostWorld()
        {
            _worldEpoch = NextEpoch();
            _currentTransfer = 0;
            _stateSeq = 0;
            _scene = CurrentScene();
            _peers.Clear();
            _session.ResetWorldReadiness();
            foreach (PlayerInfo player in _session.Players)
                if (!player.IsHost) _peers[player.Id] = NewPeer(_worldEpoch, false);
        }

        private void OnLevelUnload(object sender, EventArgs e)
        {
            SyncSession();
            if (!_session.IsHost) return;
            if (_worldEpoch == 0) InitializeHostWorld();
            _currentTransfer = 0;
            _session.ResetWorldReadiness();
            foreach (PeerWorld peer in _peers.Values)
            {
                peer.TransferId = 0;
                peer.Scene = "";
                peer.Applying = true;
                peer.Ready = false;
                peer.Failed = false;
            }
            UpdateBarrier();
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            SyncSession();
            if (!_session.IsHost) return;

            _worldEpoch = NextEpoch();
            _currentTransfer = 0;
            _stateSeq = 0;
            _readyStateSeq = -1;
            _scene = SceneFrom(sender);
            _peers.Clear();
            _session.ResetWorldReadiness();
            foreach (PlayerInfo player in _session.Players)
                if (!player.IsHost) _peers[player.Id] = NewPeer(_worldEpoch, true);
            UpdateBarrier();
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!_session.IsHost || player.IsHost) return;
            if (_worldEpoch == 0) InitializeHostWorld();
            _peers[player.Id] = NewPeer(_worldEpoch, false);
            _session.SetPeerWorldReady(player.Id, false);
            UpdateBarrier();
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            _peers.Remove(player.Id);
            UpdateBarrier();
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            SyncSession();
            if (reader.Type == MessageType.WorldState)
            {
                if (sender != null || !_session.IsGuest) throw new FormatException("WorldState must come from the host");
                HandleWorldState(FlowWire.ReadWorldState(reader));
            }
            else if (reader.Type == MessageType.WorldReady)
            {
                if (sender == null || !_session.IsHost) throw new FormatException("WorldReady must come from a guest");
                HandleWorldReady(sender, FlowWire.ReadWorldReady(reader));
            }
            else if (reader.Type == MessageType.FlowCommand)
            {
                if (sender == null || !_session.IsHost) throw new FormatException("FlowCommand must come from a guest");
                RouteCommand(sender, FlowWire.ReadCommand(reader), reader);
            }
            else if (reader.Type == MessageType.FlowResult)
            {
                if (sender != null || !_session.IsGuest) throw new FormatException("FlowResult must come from the host");
                RouteResult(FlowWire.ReadResult(reader));
            }
        }

        private static void RouteCommand(PlayerInfo sender, FlowCommandEnvelope command, MessageReader reader)
        {
            switch (command.Domain)
            {
                case FlowDomain.Pause:
                    TimeSync.Instance?.HandleFlowCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                case FlowDomain.Dialogue:
                    DialogueSync.Instance?.HandleFlowCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                case FlowDomain.Area:
                    AreaTransitionSync.Instance?.HandleFlowCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                case FlowDomain.Rest:
                    RestSync.Instance?.HandleFlowCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                case FlowDomain.Loot:
                    LootSync.Instance?.HandleFlowCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                case FlowDomain.Inventory:
                    InventoryCommands.Instance?.HandleHostCommand(sender, command.WorldEpoch, command.RequestId, command.Kind, reader);
                    break;
                default:
                    throw new FormatException("flow command domain " + command.Domain);
            }
        }

        private static void RouteResult(FlowResultEnvelope result)
        {
            switch (result.Domain)
            {
                case FlowDomain.Pause:
                    TimeSync.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                case FlowDomain.Dialogue:
                    DialogueSync.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                case FlowDomain.Area:
                    AreaTransitionSync.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                case FlowDomain.Rest:
                    RestSync.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                case FlowDomain.Loot:
                    LootSync.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                case FlowDomain.Inventory:
                    InventoryCommands.Instance?.HandleFlowResult(result.WorldEpoch, result.RequestId, result.Kind, result.Result, result.StateSeq);
                    break;
                default:
                    throw new FormatException("flow result domain " + result.Domain);
            }
        }

        private void HandleWorldState(WorldStateData state)
        {
            bool same = state.WorldEpoch == _worldEpoch && state.TransferId == _currentTransfer;
            if (state.Kind == WorldStateKind.Handoff)
            {
                if (same && _session.IsWorldReady) return;
                if (!same)
                {
                    _worldEpoch = state.WorldEpoch;
                    _currentTransfer = state.TransferId;
                    _readyStateSeq = -1;
                    _session.ResetWorldReadiness();
                }
                _scene = state.Scene;
                _session.SetLocalWorldReady(false);
                return;
            }

            if (!same || state.Scene != _scene) return;
            if (state.StateSeq < _readyStateSeq) return;
            bool firstReady = !_session.IsWorldReady;
            _readyStateSeq = state.StateSeq;
            _session.SetLocalWorldReady(true);
            if (firstReady)
                _session.Note("World ready for epoch " + _worldEpoch + ", transfer " + _currentTransfer
                    + ", state " + state.StateSeq);
            _session.RaiseWorldStateReady(null, _worldEpoch);
        }

        private void HandleWorldReady(PlayerInfo sender, WorldReadyData ready)
        {
            if (!_peers.TryGetValue(sender.Id, out PeerWorld peer)
                || peer.Epoch != ready.WorldEpoch || peer.Epoch != _worldEpoch
                || peer.TransferId <= 0 || peer.TransferId != ready.TransferId) return;

            if (ready.Result != FlowResultCode.Ok)
            {
                peer.Applying = false;
                peer.Ready = false;
                peer.Failed = true;
                _session.SetPeerWorldReady(sender.Id, false);
                UpdateBarrier();
                _session.Note(sender.Name + " could not load world " + ready.WorldEpoch
                    + " (transfer " + ready.TransferId + "): " + ready.Problem);
                return;
            }

            string currentScene = CurrentScene();
            if (ready.Scene != peer.Scene || (currentScene.Length > 0 && ready.Scene != currentScene))
            {
                peer.Applying = false;
                peer.Ready = false;
                peer.Failed = true;
                _session.SetPeerWorldReady(sender.Id, false);
                UpdateBarrier();
                _session.Note(sender.Name + " loaded an unexpected scene; world readiness was not granted");
                return;
            }

            peer.Applying = false;
            peer.Ready = true;
            peer.Failed = false;
            long seq = ++_stateSeq;
            _session.SendToPlayer(sender.Id, FlowWire.WriteWorldState(
                _worldEpoch, peer.TransferId, seq, WorldStateKind.Ready, peer.Scene).ToArray());
            _session.SetPeerWorldReady(sender.Id, true);
            _session.Note(sender.Name + " world ready for epoch " + _worldEpoch
                + ", transfer " + peer.TransferId + ", state " + seq);
            UpdateBarrier();
            _session.RaiseWorldStateReady(sender, _worldEpoch);
            _session.RequestFullState(sender.Id, _worldEpoch);
        }

        private void SyncSession()
        {
            if (_generation == _session.Generation) return;
            ResetForSession();
        }

        private void OnSessionReset() => ResetForSession();

        private void ResetForSession()
        {
            _generation = _session.Generation;
            SetBarrier(false);
            _peers.Clear();
            _worldEpoch = 0;
            _currentTransfer = 0;
            _stateSeq = 0;
            _readyStateSeq = -1;
            _scene = "";
        }

        private PeerWorld NewPeer(long epoch, bool applying)
            => new PeerWorld { Epoch = epoch, Scene = _scene, Applying = applying };

        private void UpdateBarrier()
        {
            bool apply = false;
            if (_session.IsHost)
                foreach (PeerWorld peer in _peers.Values)
                    if (peer.Applying) { apply = true; break; }
            SetBarrier(apply);
        }

        private void SetBarrier(bool value)
        {
            if (_barrier == value) return;
            _barrier = value;
            if (value)
            {
                _restoreInputBlock = GameInput.BlockAllKeys;
                GameInput.BeginBlockAllKeys();
                Time.timeScale = 0f;
            }
            else
            {
                if (!_restoreInputBlock) GameInput.EndBlockAllKeys();
                _restoreInputBlock = false;
                if (TimeController.Instance != null) TimeController.Instance.UpdateTimeScale();
            }
        }

        private string CurrentScene()
        {
            return GameState.Instance != null && GameState.Instance.CurrentMap != null
                ? Protocol.Clean(GameState.Instance.CurrentMap.SceneName, FlowWire.MaxSceneLength)
                : "";
        }

        private string SceneFrom(object sender)
        {
            string scene = CurrentScene();
            return scene.Length > 0 ? scene : Protocol.Clean(sender as string, FlowWire.MaxSceneLength);
        }

        private static long NextEpoch()
        {
            long candidate = DateTime.UtcNow.Ticks;
            while (true)
            {
                long previous = Volatile.Read(ref s_lastEpoch);
                long next = candidate > previous ? candidate : previous + 1;
                if (next <= 0) next = 1;
                if (Interlocked.CompareExchange(ref s_lastEpoch, next, previous) == previous) return next;
            }
        }
    }

    [HarmonyPatch(typeof(TimeController), "UpdateTimeScale")]
    internal static class FlowCoordinatorTimeBarrier
    {
        private static void Postfix()
        {
            if (FlowCoordinator.ApplyingWorldBarrier) Time.timeScale = 0f;
        }
    }

    [HarmonyPatch(typeof(AIStateManager), "Update")]
    internal static class FlowCoordinatorAiBarrier
    {
        private static bool Prefix() => !FlowCoordinator.ApplyingWorldBarrier;
    }

    [HarmonyPatch(typeof(Mover), "Update")]
    internal static class FlowCoordinatorMovementBarrier
    {
        private static bool Prefix() => !FlowCoordinator.ApplyingWorldBarrier;
    }

    [HarmonyPatch(typeof(TacticalModeManager), "Update")]
    internal static class FlowCoordinatorTacticalBarrier
    {
        private static bool Prefix() => !FlowCoordinator.ApplyingWorldBarrier;
    }

    [HarmonyPatch(typeof(Player), "IssueCommands")]
    internal static class FlowCoordinatorInputBarrier
    {
        private static bool Prefix() => !FlowCoordinator.ApplyingWorldBarrier;
    }
}
