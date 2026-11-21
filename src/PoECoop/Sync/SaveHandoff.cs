using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Save-backed late joining and area/world transitions. Legacy save messages
    /// retain their layout; WorldState and WorldReady add session, transfer and
    /// world correlation around the same transfer and GameResources.LoadGame path.
    /// </summary>
    public sealed class SaveHandoff : IDisposable
    {
        private const string HostFile = "coop-handoff.savegame";
        private const string GuestFilePrefix = "coop-session-";
        private const int ChunkSize = 256 * 1024;
        private const int MaxSaveSize = 256 * 1024 * 1024;

        private static readonly bool s_skipQuicksaveCheck =
            Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-force-handoff") >= 0;

        private readonly Session _session;
        private readonly MonoBehaviour _runner;

        private readonly HashSet<int> _waiting = new HashSet<int>();
        private string _waitReason;
        private float _nextTry;
        private int _nextTransferId = 1;

        private long _generation;
        private int _transferId;
        private long _transferGeneration;
        private long _transferEpoch;
        private byte[] _incoming;
        private int _received;

        private string _readyFile;
        private long _readyGeneration;
        private int _readyTransferId;
        private long _readyWorldEpoch;

        private string _loadingFile;
        private long _loadingGeneration;
        private int _loadingTransferId;
        private long _loadingWorldEpoch;
        private bool _loadStarted;

        public SaveHandoff(Session session, MonoBehaviour runner)
        {
            _session = session;
            _runner = runner;
            _generation = session.Generation;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.GameMessage += OnGameMessage;
            GameState.OnLevelLoaded += OnLevelLoaded;
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelLoaded;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.GameMessage -= OnGameMessage;
        }

        public void Update(float now)
        {
            SyncSession();
            if (_session.IsHost)
            {
                if (_waiting.Count == 0 || now < _nextTry) return;
                _nextTry = now + 0.5f;
                string reason = WhyHostCannotSave();
                if (reason == null) SendGame();
                else if (reason != _waitReason)
                {
                    _waitReason = reason;
                    _session.Note("Guests will get the game once you are " + reason);
                    Plugin.Log.LogInfo("Handoff waiting. player=" + GameState.IsInGameSession + " mainMenu=" + GameState.IsInMainMenu
                        + " map=" + (GameState.Instance != null && GameState.Instance.CurrentMap != null)
                        + " loading=" + GameState.IsLoading + " combat=" + GameState.InCombat);
                }
            }
            else if (_session.IsGuest && _readyFile != null)
            {
                if (_readyGeneration != _generation
                    || !FlowCoordinator.IsCurrentTransfer(_readyTransferId, _readyWorldEpoch))
                {
                    ClearReady(true);
                    return;
                }
                if (_loadingFile == null && CanGuestLoad()) _runner.StartCoroutine(LoadReceivedGame());
            }
        }

        private void SyncSession()
        {
            if (_session.Generation == _generation) return;
            _generation = _session.Generation;
            _waiting.Clear();
            _waitReason = null;
            _incoming = null;
            _received = 0;
            _transferId = 0;
            _transferGeneration = 0;
            _transferEpoch = 0;
            ClearReady(true);

            string loadingFile = _loadingFile;
            bool loadStarted = _loadStarted;
            ClearLoading(false);
            if (loadingFile == null) return;

            DeleteGuestFile(loadingFile);
            if (loadStarted)
            {
                _session.Note("The previous host's game had already started loading; ignoring its completion");
                Plugin.Log.LogWarning("A received-save load became uncancellable when the session changed; its completion and ack will be suppressed");
            }
            else
            {
                if (FadeManager.Instance != null)
                    FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.AreaTransition, 0.35f);
                _session.Note("Cancelled loading the previous host's game because the session changed");
            }
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!_session.IsHost) return;
            _waiting.Add(player.Id);
            _waitReason = null;
            _nextTry = 0f;
        }

        private void OnPlayerLeft(PlayerInfo player) => _waiting.Remove(player.Id);

        private static string WhyHostCannotSave()
        {
            if (!GameState.IsInGameSession || GameState.IsInMainMenu || GameState.Instance == null || GameState.Instance.CurrentMap == null)
                return "in a game";
            if (GameState.IsLoading) return "done loading";
            if (GameState.InCombat) return "out of combat";
            if (!s_skipQuicksaveCheck && InGameHUD.Instance != null && !InGameHUD.Instance.QuicksaveAllowed)
                return "out of the current conversation, cutscene or menu";
            return null;
        }

        private void SendGame()
        {
            byte[] data;
            string previousFile = GameState.LoadedFileName;
            try
            {
                if (!GameResources.SaveGame(HostFile, "Co-op session")) return;
                SaveGameInfo.WaitUntilSafeToSaveLoad();
                data = File.ReadAllBytes(Path.Combine(GameResources.SaveGamePath, HostFile));
                if (data.Length <= 0 || data.Length > MaxSaveSize) throw new InvalidDataException("save size " + data.Length);
            }
            catch (Exception ex)
            {
                _session.Note("Could not save the game for guests: " + ex.Message);
                Plugin.Log.LogError(ex);
                AbortWaiting(ex.Message);
                return;
            }
            finally
            {
                GameState.LoadedFileName = previousFile;
                try { GameResources.DeleteSavedGame(HostFile); } catch { }
            }

            string scene = GameState.Instance.CurrentMap.SceneName;
            int id = NextTransferId();
            int sent = 0;
            foreach (int player in _waiting)
            {
                if (!FlowCoordinator.BeginTransfer(player, id, scene))
                {
                    FlowCoordinator.AbortHandoff(player, "world handoff is no longer current");
                    continue;
                }
                _session.SendToPlayer(player, new MessageWriter(MessageType.SaveBegin).Int(id).Int(data.Length).String(scene).ToArray());
                for (int offset = 0; offset < data.Length; offset += ChunkSize)
                {
                    int count = Math.Min(ChunkSize, data.Length - offset);
                    _session.SendToPlayer(player, new MessageWriter(MessageType.SaveChunk).Int(id).Bytes(data, offset, count).ToArray());
                }
                sent++;
            }
            if (sent > 0)
                _session.Note("Sent the game (" + Size(data.Length) + ", " + scene + ") to " + sent + (sent == 1 ? " guest" : " guests"));
            _waiting.Clear();
            _waitReason = null;
        }

        private int NextTransferId()
        {
            int id = _nextTransferId++;
            if (_nextTransferId <= 0) _nextTransferId = 1;
            if (id <= 0) id = _nextTransferId++;
            return id;
        }

        private void AbortWaiting(string problem)
        {
            foreach (int player in _waiting) FlowCoordinator.AbortHandoff(player, problem);
            _waiting.Clear();
            _waitReason = null;
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            SyncSession();
            if (sender != null)
            {
                if (reader.Type != MessageType.SaveLoaded) return;
                string scene = Protocol.Clean(reader.String(), FlowWire.MaxSceneLength);
                string problem = Protocol.Clean(reader.String(), FlowWire.MaxProblemLength);
                reader.Finish();
                _session.Note(scene.Length > 0
                    ? sender.Name + " is in " + scene
                    : sender.Name + " could not load the game: " + problem);
                return;
            }

            switch (reader.Type)
            {
                case MessageType.SaveBegin:
                    ReceiveBegin(reader);
                    break;
                case MessageType.SaveChunk:
                    ReceiveChunk(reader);
                    break;
            }
        }

        private void ReceiveBegin(MessageReader reader)
        {
            int id = reader.Int();
            int length = reader.Int();
            string scene = Protocol.Clean(reader.String(), FlowWire.MaxSceneLength);
            reader.Finish();
            if (length <= 0 || length > MaxSaveSize) throw new FormatException("save size " + length);
            if (!FlowCoordinator.CanAcceptTransfer(id)) return;

            if ((_incoming != null && id == _transferId)
                || (_readyFile != null && id == _readyTransferId)
                || (_loadingFile != null && id == _loadingTransferId)) return;

            ClearReady(true);
            if (_loadingFile != null && !_loadStarted)
            {
                ClearLoading(true);
                if (FadeManager.Instance != null)
                    FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.AreaTransition, 0.35f);
                _session.Note("Cancelled a superseded world load");
            }

            _transferId = id;
            _transferGeneration = _generation;
            _transferEpoch = FlowCoordinator.WorldEpoch;
            _incoming = new byte[length];
            _received = 0;
            _session.Note("Receiving the game from the host (" + Size(length) + ", " + scene + ")");
        }

        private void ReceiveChunk(MessageReader reader)
        {
            int id = reader.Int();
            byte[] chunk = reader.Bytes();
            reader.Finish();
            if (_incoming == null || id != _transferId || _transferGeneration != _generation
                || !FlowCoordinator.IsCurrentTransfer(id, _transferEpoch)) return;
            if (chunk.Length > _incoming.Length - _received) throw new FormatException("save chunk overflow");
            Buffer.BlockCopy(chunk, 0, _incoming, _received, chunk.Length);
            _received += chunk.Length;
            if (_received != _incoming.Length) return;

            string file = GuestFilePrefix + _transferGeneration + "-" + id + "-" + _transferEpoch + ".savegame";
            File.WriteAllBytes(Path.Combine(GameResources.SaveGamePath, file), _incoming);
            _incoming = null;
            ClearReady(true);
            _readyFile = file;
            _readyGeneration = _transferGeneration;
            _readyTransferId = id;
            _readyWorldEpoch = _transferEpoch;
        }

        private static bool CanGuestLoad()
        {
            return GameState.Instance != null && FadeManager.Instance != null && !GameState.IsLoading
                && (GameState.IsInMainMenu || GameState.IsInGameSession);
        }

        private IEnumerator LoadReceivedGame()
        {
            string file = _readyFile;
            long generation = _readyGeneration;
            int transferId = _readyTransferId;
            long worldEpoch = _readyWorldEpoch;
            ClearReady(false);
            _loadingFile = file;
            _loadingGeneration = generation;
            _loadingTransferId = transferId;
            _loadingWorldEpoch = worldEpoch;
            _loadStarted = false;
            _session.Note("Loading the host's game");

            FadeManager.Instance.FadeToBlack(FadeManager.FadeType.AreaTransition, 0.35f, AudioFadeMode.MusicAndFx);
            yield return new WaitForSecondsRealtime(0.45f);

            if (!IsCurrentLoad(file, generation, transferId, worldEpoch))
            {
                if (_loadingFile == file && _loadingGeneration == generation && !_loadStarted)
                {
                    ClearLoading(true);
                    if (FadeManager.Instance != null)
                        FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.AreaTransition, 0.35f);
                }
                yield break;
            }

            _loadStarted = true;
            string problem = null;
            try
            {
                if (!GameResources.LoadGame(file)) problem = "the game rejected the save file";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                Plugin.Log.LogError(ex);
            }

            if (problem == null) yield break;
            bool current = IsCurrentLoad(file, generation, transferId, worldEpoch);
            if (_loadingFile == file && _loadingGeneration == generation) ClearLoading(true);
            if (FadeManager.Instance != null)
                FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.AreaTransition, 0.35f);
            if (!current) yield break;

            _session.Note("Could not load the host's game: " + problem);
            FlowCoordinator.FailGuestLoad(transferId, worldEpoch, problem);
            _session.SendToHost(new MessageWriter(MessageType.SaveLoaded).String("").String(problem).ToArray());
        }

        private bool IsCurrentLoad(string file, long generation, int transferId, long worldEpoch)
        {
            SyncSession();
            return _session.IsGuest && _generation == generation
                && _loadingFile == file && _loadingGeneration == generation
                && _loadingTransferId == transferId && _loadingWorldEpoch == worldEpoch
                && FlowCoordinator.IsCurrentTransfer(transferId, worldEpoch);
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            SyncSession();
            if (_session.IsHost)
            {
                foreach (var player in _session.Players)
                    if (!player.IsHost) _waiting.Add(player.Id);
                _waitReason = null;
                _nextTry = 0f;
                return;
            }
            if (_loadingFile == null) return;

            string file = _loadingFile;
            long generation = _loadingGeneration;
            int transferId = _loadingTransferId;
            long worldEpoch = _loadingWorldEpoch;
            bool current = _session.IsGuest && generation == _generation
                && FlowCoordinator.IsCurrentTransfer(transferId, worldEpoch);
            ClearLoading(true);
            if (!current) return;

            string scene = GameState.Instance != null && GameState.Instance.CurrentMap != null
                ? GameState.Instance.CurrentMap.SceneName
                : sender as string ?? "?";
            _session.Note("In the host's game (" + scene + ")");
            FlowCoordinator.CompleteGuestLoad(transferId, worldEpoch, scene);
            _session.SendToHost(new MessageWriter(MessageType.SaveLoaded).String(scene).String("").ToArray());
        }

        private void ClearReady(bool delete)
        {
            string file = _readyFile;
            _readyFile = null;
            _readyGeneration = 0;
            _readyTransferId = 0;
            _readyWorldEpoch = 0;
            if (delete) DeleteGuestFile(file);
        }

        private void ClearLoading(bool delete)
        {
            string file = _loadingFile;
            _loadingFile = null;
            _loadingGeneration = 0;
            _loadingTransferId = 0;
            _loadingWorldEpoch = 0;
            _loadStarted = false;
            if (delete) DeleteGuestFile(file);
        }

        private static void DeleteGuestFile(string file)
        {
            if (file == null) return;
            try { GameResources.DeleteSavedGame(file); } catch { }
        }

        private static string Size(int bytes) => (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
    }
}
