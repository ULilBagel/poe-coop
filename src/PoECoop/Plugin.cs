using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using PoECoop.Net;
using PoECoop.Sync;
using PoECoop.UI;
using UnityEngine;

namespace PoECoop
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "poecoop";
        public const string Name = "PoE Co-op";
        public const string Version = "0.10.1";

        internal static ManualLogSource Log;

        private Settings _settings;
        private Session _session;
        private SteamLobbyService _steam;
        private FlowCoordinator _flow;
        private Overlay _overlay;
        private SaveHandoff _handoff;
        private TimeSync _time;
        private DialogueSync _dialogue;
        private AreaTransitionSync _areaTransitions;
        private RestSync _rest;
        private ItemRegistry _items;
        private InventorySync _inventory;
        private InventoryCommands _inventoryCommands;
        private LootSync _loot;
        private GroundLootSync _groundLoot;
        private FactionStateSync _factions;
        private EntityRegistry _entities;
        private PositionSync _positions;
        private VitalsSync _vitals;
        private CombatStateSync _combatState;
        private TacticalStateSync _tacticalState;
        private StatusEffectSync _statusEffects;
        private AttackVisualSync _attackVisuals;
        private ProjectileVisualSync _projectileVisuals;
        private CombatLogSync _combatLog;
        private AbilityStateSync _abilityStates;
        private Ownership _ownership;
        private Orders _orders;
        private TacticalOrders _tacticalOrders;

        /// <summary>True while this copy of the game is joined to someone else's session.</summary>
        internal static bool IsGuest => s_session != null && s_session.IsGuest;
        private static Session s_session;

        /// <summary>
        /// True while this copy is a guest inside a level. From then on it shows the
        /// host's game and runs none of its own: see Patches/GuestFreezePatches.cs.
        /// False during loading, so a level still sets itself up normally.
        /// </summary>
        internal static bool IsMirroring => IsGuest && GameState.IsInGameSession && !GameState.IsLoading;

        internal static bool IsHost => s_session != null && s_session.IsHost;

        private void Awake()
        {
            Log = Logger;
            string gameVersion = GameVersion();
            Log.LogInfo($"{Name} {Version} loaded. Unity {Application.unityVersion}, game {gameVersion}");

            _settings = new Settings(Config);
            PerfStats.Start(Environment.GetCommandLineArgs(), Version, Log.LogInfo);
            string logFile = Arg(Environment.GetCommandLineArgs(), "--coop-log");
            if (logFile != null) System.IO.File.WriteAllText(logFile, "");
            Action<string> log = line =>
            {
                Log.LogInfo(line);
                if (logFile != null) System.IO.File.AppendAllText(logFile, line + "\n");
            };
            _session = new Session(Version, gameVersion, () => new TcpTransport(), log);
            _steam = new SteamLobbyService(Version, gameVersion, HostSteamSession, JoinSteamSession, log);
            _steam.SessionStopRequested += _session.Leave;
            s_session = _session;
            // Received work is spread over frames only in a loaded level with no world barrier up.
            _session.CanDeferReceive = () => GameState.IsInGameSession && !GameState.IsInMainMenu
                && !GameState.IsLoading && GameState.Instance != null && !FlowCoordinator.ApplyingWorldBarrier;
            _flow = new FlowCoordinator(_session);
            _handoff = new SaveHandoff(_session, this);
            _ownership = new Ownership(_session);
            _entities = new EntityRegistry(_session);
            _time = new TimeSync(_session);
            _dialogue = new DialogueSync(_session);
            _areaTransitions = new AreaTransitionSync(_session);
            _rest = new RestSync(_session, _ownership);
            _items = new ItemRegistry(_session);
            _inventory = new InventorySync(_session, _ownership);
            _inventoryCommands = new InventoryCommands(_session, _ownership);
            _loot = new LootSync(_session, _ownership);
            _groundLoot = new GroundLootSync(_session, _ownership);
            _factions = new FactionStateSync(_session);
            _positions = new PositionSync(_session);
            _vitals = new VitalsSync(_session);
            _combatState = new CombatStateSync(_session);
            _tacticalState = new TacticalStateSync(_session);
            _statusEffects = new StatusEffectSync(_session);
            _attackVisuals = new AttackVisualSync(_session);
            _projectileVisuals = new ProjectileVisualSync(_session);
            _combatLog = new CombatLogSync(_session);
            _abilityStates = new AbilityStateSync(_session);
            _orders = new Orders(_session, _ownership);
            _tacticalOrders = new TacticalOrders(_session, _ownership);
            _overlay = new Overlay(_session, _settings, _ownership, _steam);
            DevTools.Start(this, _session, _ownership, _orders);

            new Harmony(Guid).PatchAll();
            ApplyCommandLine();
        }

        private static readonly bool s_noEdgeScroll =
            Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-no-edge-scroll") >= 0;

        private void Update()
        {
            PerfStats.BeginFrame();
            float now = Time.unscaledTime;
            DevTools.KeepRunningWhenHidden();
            if (s_noEdgeScroll && GameState.Option != null && GameState.Option.GetOption(GameOption.BoolOption.SCREEN_EDGE_SCROLLING))
                GameState.Option.SetOption(GameOption.BoolOption.SCREEN_EDGE_SCROLLING, false);

            DevTools.UpdateTrace("session");
            _session.Update(now);
            DevTools.UpdateTrace("flow");
            _flow.Update(now);
            DevTools.UpdateTrace("handoff");
            _handoff.Update(now);
            DevTools.UpdateTrace("entities");
            _entities.Update(now);
            _factions.Update(now);
            DevTools.UpdateTrace("items");
            _items.Update(now);
            _inventory.SetWorldState(FlowCoordinator.WorldEpoch,
                _session.IsWorldReady && !FlowCoordinator.ApplyingWorldBarrier);
            _time.Update(now);
            _dialogue.Update(now);
            _areaTransitions.Update(now);
            _rest.Update(now);
            _inventory.Update(now);
            _inventoryCommands.Update(now);
            DevTools.UpdateTrace("loot");
            _loot.Update(now);
            _groundLoot.Update(now);
            DevTools.UpdateTrace("legacy-world");
            _positions.Update(now);
            _vitals.Update(now);
            _combatState.Update(now);
            _tacticalState.Update(now);
            _statusEffects.Update(now);
            _attackVisuals.Update(now);
            _projectileVisuals.Update(now);
            _combatLog.Update(now);
            _abilityStates.Update(now);
            _ownership.Update();
            _orders.Collect();
            _steam.Update();
            _overlay.Update();
            if (PerfStats.Enabled) EndPerfFrame();
        }

        private void EndPerfFrame()
        {
            string role;
            switch (_session.State)
            {
                case SessionState.Hosting: role = "host"; break;
                case SessionState.Joined: role = "guest"; break;
                case SessionState.Connecting: role = "connecting"; break;
                default: role = "offline"; break;
            }
            int peers = _session.Players.Count > 0 ? _session.Players.Count - 1 : 0;
            PerfStats.EndFrame(role, peers, GameState.IsInGameSession && !GameState.IsLoading, GameState.IsLoading);
        }

        private void OnGUI() => _overlay.OnGUI();

        private void OnDisable()
        {
            if (Log != null) Log.LogInfo("TEST plugin OnDisable");
        }

        private void OnDestroy()
        {
            if (Log != null) Log.LogInfo("TEST plugin OnDestroy");
            _overlay?.Close();
            if (_steam != null)
            {
                _steam.SessionStopRequested -= _session.Leave;
                _steam.Dispose();
            }
            _tacticalOrders?.Dispose();
            _factions?.Dispose();
            _groundLoot?.Dispose();
            _loot?.Dispose();
            _inventoryCommands?.Dispose();
            _inventory?.Dispose();
            _rest?.Dispose();
            _areaTransitions?.Dispose();
            _dialogue?.Dispose();
            _time?.Dispose();
            _items?.Dispose();
            _combatLog?.Dispose();
            _projectileVisuals?.Dispose();
            _attackVisuals?.Dispose();
            _statusEffects?.Dispose();
            _tacticalState?.Dispose();
            _combatState?.Dispose();
            _vitals?.Dispose();
            _positions?.Dispose();
            _entities?.Dispose();
            _handoff?.Dispose();
            _flow?.Dispose();
            _ownership?.Dispose();
            _session?.Dispose();
            PerfStats.Stop();
        }

        private static string GameVersion()
        {
            try { return ProductConfiguration.GetVersion(); }
            catch (Exception ex) { return "unknown (" + ex.GetType().Name + ")"; }
        }

        private void HostSteamSession()
        {
            if (_session.State != SessionState.Offline) _session.Leave();
            _session.SetTransportFactory("Steam", () => new SteamTransport(
                _steam.CurrentLobby, _steam.LobbyOwner, true));
            _session.Host(0, _settings.Name.Value);
        }

        private void JoinSteamSession(Steamworks.CSteamID owner)
        {
            if (_session.State != SessionState.Offline) _session.Leave();
            _session.SetTransportFactory("Steam", () => new SteamTransport(
                _steam.CurrentLobby, owner, false));
            _session.Join(_steam.CurrentLobby.ToString(), 0, _settings.Name.Value);
        }

        /// <summary>
        /// For testing two copies on one PC, where both share one config file:
        ///   --coop-host            host as soon as the game starts
        ///   --coop-join ADDRESS    join as soon as the game starts
        ///   --coop-port N          port for either
        ///   --coop-name NAME       player name for this run only
        ///   --coop-say TEXT        send TEXT once another player is in the session
        ///   --coop-log FILE        also write session lines to FILE (both copies share BepInEx's log)
        ///   --coop-perf FILE       write timing and traffic diagnostics to FILE, one JSON line per second (Diagnostics/PerfStats.cs)
        ///   --coop-screenshot FILE save a screenshot every 15 seconds: FILE-1.png, FILE-2.png, ...
        ///   --coop-continue        load the most recent save as soon as the main menu is up
        ///   --coop-newgame         start a new game as soon as the main menu is up
        ///   --coop-no-edge-scroll  keep the camera still when the mouse rests on a window edge (unattended tests)
        ///   --coop-force-handoff   send the game to guests even mid-conversation or in a menu (Sync/SaveHandoff.cs)
        ///   --coop-multi-instance  let a second copy run (see Patches/DevPatches.cs)
        /// </summary>
        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string name = Arg(args, "--coop-name") ?? _settings.Name.Value;
            int port = int.TryParse(Arg(args, "--coop-port"), out int p) ? p : _settings.Port.Value;

            string say = Arg(args, "--coop-say");
            if (say != null)
            {
                bool said = false;
                _session.PlayerJoined += _ =>
                {
                    if (said) return;
                    said = true;
                    _session.SendChat(say);
                };
            }

            string screenshot = Arg(args, "--coop-screenshot");
            if (screenshot != null) StartCoroutine(ScreenshotLater(screenshot));

            if (Array.IndexOf(args, "--coop-newgame") >= 0) StartCoroutine(NewGameWhenReady());
            else if (Array.IndexOf(args, "--coop-continue") >= 0) StartCoroutine(ContinueWhenReady());

            string join = Arg(args, "--coop-join");
            if (Array.IndexOf(args, "--coop-host") >= 0)
            {
                _session.UseDirectTransport();
                _session.Host(port, name);
                _overlay.Visible = true;
            }
            else if (join != null)
            {
                _session.UseDirectTransport();
                _session.Join(join, port, name);
                _overlay.Visible = true;
            }
        }

        /// <summary>Does what the main menu's New Game button ends in, without the difficulty screens and intro.</summary>
        private static System.Collections.IEnumerator NewGameWhenReady()
        {
            while (!GameState.IsInMainMenu || GameState.Instance == null) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            Log.LogInfo("Starting a new game (--coop-newgame)");
            GameState.NewGame = true;
            GameState.IsInMainMenu = false; // the menu clears this itself on the normal path
            UnityEngine.SceneManagement.SceneManager.LoadScene("AR_0701_Encampment");
        }

        /// <summary>Does what the main menu's Continue button does.</summary>
        private static System.Collections.IEnumerator ContinueWhenReady()
        {
            while (!GameState.IsInMainMenu || GameState.Instance == null || FadeManager.Instance == null) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            if (!GameResources.SaveGameExists()) { Log.LogWarning("--coop-continue: there are no saves to load"); yield break; }
            Log.LogInfo("Loading the most recent save (--coop-continue)");
            GameResources.LoadLastGame(true);
        }

        private static System.Collections.IEnumerator ScreenshotLater(string file)
        {
            for (int n = 1; ; n++)
            {
                yield return new WaitForSecondsRealtime(15f);
                ScreenCapture.CaptureScreenshot(file + "-" + n + ".png");
            }
        }

        private static string Arg(string[] args, string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
