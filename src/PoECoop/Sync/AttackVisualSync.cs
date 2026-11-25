using System;
using System.Collections.Generic;
using System.Globalization;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates the body attack/cast action selected by AttackBase. Guests set
    /// AnimationController state directly: no attack is launched and no impact is
    /// replayed. Current weapon and grimoire controllers mirror the body action.
    /// </summary>
    public sealed class AttackVisualSync : IDisposable
    {
        private enum Phase : byte { Start = 1, Update = 2, End = 3, Interrupt = 4 }

        private const int MaxEventsPerMessage = 128;
        private const int MaxPendingEvents = 512;
        private const int MaxVariation = 9999;
        private const float MaxSpeed = 16f;
        private const float MaxPosition = 100000f;
        private const float FullRefreshInterval = 3f;
        // Added once after a forced refresh so the periodic refreshes of the
        // different streams do not all land in the same frame.
        private const float ForcedRefreshPhase = 2.85f;
        private const float IdlePruneInterval = 1f;
        private const float GuestTimeout = 10f;
        private const byte OffhandFlag = 1;
        private const byte InstantFlag = 2;
        private const byte LoopFlag = 4;

        private static readonly int InstantHash = Animator.StringToHash("Instant");
        private static readonly int LoopHash = Animator.StringToHash("Loop");

        private static AttackVisualSync s_instance;

        private readonly Session _session;
        private readonly bool _probe;
        private readonly List<VisualEvent> _pending = new List<VisualEvent>();
        private readonly Dictionary<AttackBase, HostAction> _hostActions = new Dictionary<AttackBase, HostAction>();
        private readonly Dictionary<Guid, GuestAction> _guestActions = new Dictionary<Guid, GuestAction>();
        private readonly List<VisualEvent> _received = new List<VisualEvent>();
        private readonly List<AttackBase> _pruned = new List<AttackBase>();
        private bool _anyHostActive;
        private float _nextIdlePrune;
        private int _epoch;
        private int _sequence;
        private int _nextActionId;
        private float _nextFullRefresh;
        private int _guestEpoch = -1;
        private int _guestSequence = -1;
        private bool _wasGuest;

        private sealed class VisualEvent
        {
            public Guid Owner;
            public int ActionId;
            public Phase Phase;
            public AnimationController.ActionType ActionType;
            public int Variation;
            public float Speed;
            public byte Flags;
            public Guid Target;
            public Vector3 TargetPosition;
        }

        private struct Snapshot
        {
            public AnimationController.ActionType ActionType;
            public int Variation;
            public float Speed;
            public byte Flags;
        }

        /// <summary>
        /// One entry per AttackBase seen on the host. An entry that is not Tracked
        /// only caches the owner's controller for an attack that has never started
        /// an action; it is treated exactly like a missing entry.
        /// </summary>
        private sealed class HostAction
        {
            public bool Tracked;
            public AnimationController Controller;
            public GameObject Owner;
            public Guid OwnerId;
            public GameObject Target;
            public Guid TargetId;
            public Vector3 TargetPosition;
            public bool TargetIsLocation;
            public int ActionId;
            public bool Active;
            public VisualEvent Last;
        }

        private sealed class GuestAction
        {
            public GameObject Owner;
            public int ActionId;
            public float LastSeen;
            public readonly List<AnimationController> Controllers = new List<AnimationController>();
        }

        public AttackVisualSync(Session session)
        {
            _session = session;
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
            if (s_instance == this) s_instance = null;
            _pending.Clear();
            _hostActions.Clear();
            ClearGuestActions(false);
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsGuest && player.IsHost)
            {
                ResetGuestSession();
                _wasGuest = true;
                return;
            }
            if (_session.IsHost) _nextFullRefresh = 0f;
        }

        private void SyncGuestSession()
        {
            if (!_session.IsGuest)
            {
                _wasGuest = false;
                return;
            }
            if (_wasGuest) return;
            ResetGuestSession();
            _wasGuest = true;
        }

        private void ResetGuestSession()
        {
            _pending.Clear();
            _hostActions.Clear();
            ClearGuestActions(false);
            _guestEpoch = -1;
            _guestSequence = -1;
            _nextFullRefresh = 0f;
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            _pending.Clear();
            _hostActions.Clear();
            _nextFullRefresh = 0f;
            ClearGuestActions(false);
            if (_session.IsHost)
            {
                _epoch = _epoch == int.MaxValue ? 1 : _epoch + 1;
                _sequence = 0;
                _nextActionId = 0;
            }
        }

        public void Update(float now)
        {
            SyncGuestSession();
            if (_session.IsHost)
            {
                if (!GameState.IsInGameSession || GameState.IsLoading) return;
                long started = PerfStats.Begin();
                PruneHostActions(now);
                if (_session.Players.Count < 2)
                {
                    _pending.Clear();
                    PerfStats.End("attackvisual.update", started);
                    return;
                }
                if (now >= _nextFullRefresh)
                {
                    bool forced = _nextFullRefresh <= 0f;
                    _nextFullRefresh = now + FullRefreshInterval + (forced ? ForcedRefreshPhase : 0f);
                    PerfStats.Count("attackvisual.full");
                    // Guests drop an action they have not heard about for GuestTimeout,
                    // so every active action is repeated even when it has not changed.
                    if (_anyHostActive)
                    {
                        foreach (HostAction action in _hostActions.Values)
                        {
                            if (!action.Active || action.Last == null) continue;
                            UpdateTarget(action);
                            action.Last.Target = action.TargetId;
                            action.Last.TargetPosition = action.TargetPosition;
                            Queue(Copy(action.Last, Phase.Update));
                        }
                    }
                }
                Flush();
                PerfStats.End("attackvisual.update", started);
            }
            else if (_session.IsGuest)
            {
                if (_guestActions.Count > 0) PruneGuestActions(now);
            }
        }

        // ---- host capture (called by AttackVisualPatches on the main thread) ----

        internal static void HostLaunched(AttackBase attack, GameObject target, Vector3 location, bool targetIsLocation)
        {
            AttackVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || attack == null) return;
            long started = PerfStats.Begin();
            self.BeginHostAction(attack, target, location, targetIsLocation);
            PerfStats.End("attackvisual.capture", started);
        }

        internal static void HostVisualUpdate(AttackBase attack)
        {
            AttackVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || attack == null) return;
            long started = PerfStats.Begin();
            self.UpdateHostAction(attack);
            PerfStats.End("attackvisual.capture", started);
        }

        internal static void HostInterrupted(AttackBase attack)
        {
            AttackVisualSync self = s_instance;
            if (self == null || !self._session.IsHost || attack == null) return;
            if (self._hostActions.TryGetValue(attack, out HostAction action) && action.Active)
                self.FinishHostAction(action, Phase.Interrupt);
        }

        private void BeginHostAction(AttackBase attack, GameObject target, Vector3 location, bool targetIsLocation)
        {
            GameObject owner = attack.Owner;
            AnimationController controller = owner != null ? owner.GetComponent<AnimationController>() : null;
            if (owner == null || !TrySnapshot(controller, out Snapshot snapshot))
            {
                if (_hostActions.TryGetValue(attack, out HostAction previous) && previous.Active)
                    FinishHostAction(previous, Phase.End);
                return;
            }

            Guid ownerId = IdOf(owner);
            if (ownerId == Guid.Empty) return;
            SupersedeOwnerActions(ownerId, attack);

            VisualEvent visual = ToEvent(snapshot);
            HostAction action = new HostAction
            {
                Tracked = true,
                Controller = controller,
                Owner = owner,
                OwnerId = ownerId,
                Target = target,
                TargetId = IdOf(target),
                TargetPosition = target != null && !targetIsLocation ? target.transform.position : location,
                TargetIsLocation = targetIsLocation,
                ActionId = NextActionId(),
                Active = true,
            };
            visual.Owner = ownerId;
            visual.ActionId = action.ActionId;
            visual.Phase = Phase.Start;
            visual.Target = action.TargetId;
            visual.TargetPosition = action.TargetPosition;
            action.Last = visual;
            _hostActions[attack] = action;
            _anyHostActive = true;
            Queue(visual);
        }

        private void UpdateHostAction(AttackBase attack)
        {
            if (!_hostActions.TryGetValue(attack, out HostAction action) || !action.Tracked)
            {
                // This runs every frame for every attack component that is not in
                // use, so the owner's controller is cached instead of looked up.
                GameObject owner = attack.Owner;
                if (owner == null) return;
                if (action == null || !ReferenceEquals(action.Owner, owner))
                {
                    action = new HostAction { Owner = owner, Controller = owner.GetComponent<AnimationController>() };
                    _hostActions[attack] = action;
                }
                else if (action.Controller == null)
                {
                    action.Controller = owner.GetComponent<AnimationController>();
                }
                if (!TrySnapshot(action.Controller, out Snapshot _)) return;
                BeginHostAction(attack, null, owner.transform.position, false);
                return;
            }
            if (!action.Active) return;
            if (action.Owner == null)
            {
                FinishHostAction(action, Phase.End);
                return;
            }
            if (action.Controller == null) action.Controller = action.Owner.GetComponent<AnimationController>();
            if (!TrySnapshot(action.Controller, out Snapshot snapshot))
            {
                FinishHostAction(action, Phase.End);
                return;
            }

            UpdateTarget(action);
            if (SameVisual(action.Last, snapshot))
            {
                action.Last.Target = action.TargetId;
                action.Last.TargetPosition = action.TargetPosition;
                PerfStats.Count("attackvisual.skipped");
                return;
            }

            VisualEvent current = ToEvent(snapshot);
            current.Owner = action.OwnerId;
            current.ActionId = action.ActionId;
            current.Phase = Phase.Update;
            current.Target = action.TargetId;
            current.TargetPosition = action.TargetPosition;
            action.Last = current;
            Queue(current);
        }

        private void SupersedeOwnerActions(Guid ownerId, AttackBase replacement)
        {
            foreach (var pair in _hostActions)
                if (pair.Key != replacement && pair.Value.Active && pair.Value.OwnerId == ownerId)
                    pair.Value.Active = false;
        }

        private void FinishHostAction(HostAction action, Phase phase)
        {
            if (!action.Active) return;
            action.Active = false;
            if (action.Last != null) Queue(Copy(action.Last, phase));
        }

        private int NextActionId()
        {
            _nextActionId = _nextActionId == int.MaxValue ? 1 : _nextActionId + 1;
            return _nextActionId;
        }

        private static bool TrySnapshot(AnimationController controller, out Snapshot snapshot)
        {
            snapshot = default(Snapshot);
            if (controller == null) return false;
            AnimationController.Action action = controller.DesiredAction;
            if (action == null) return false;
            int type = (int)action.m_actionType;
            if (type <= (int)AnimationController.ActionType.None || type >= (int)AnimationController.ActionType.Pending) return false;
            if (action.m_variation < 0 || action.m_variation > MaxVariation || !IsFinite(action.m_speed)) return false;

            byte flags = action.m_offhand ? OffhandFlag : (byte)0;
            // Same parameters AnimationController.Instant/Loop read, without
            // hashing the parameter name for every active action every frame.
            Animator avatar = controller.CurrentAvatar;
            if (avatar != null)
            {
                if (avatar.GetBool(InstantHash)) flags |= InstantFlag;
                if (avatar.GetBool(LoopHash)) flags |= LoopFlag;
            }
            snapshot.ActionType = action.m_actionType;
            snapshot.Variation = action.m_variation;
            snapshot.Speed = Mathf.Clamp(action.m_speed, 0f, MaxSpeed);
            snapshot.Flags = flags;
            return true;
        }

        private static VisualEvent ToEvent(Snapshot snapshot)
        {
            return new VisualEvent
            {
                ActionType = snapshot.ActionType,
                Variation = snapshot.Variation,
                Speed = snapshot.Speed,
                Flags = snapshot.Flags,
            };
        }

        private static bool SameVisual(VisualEvent a, Snapshot b)
        {
            return a != null && a.ActionType == b.ActionType && a.Variation == b.Variation
                && Mathf.Abs(a.Speed - b.Speed) < 0.001f && a.Flags == b.Flags;
        }

        private static void UpdateTarget(HostAction action)
        {
            if (action.Target != null)
            {
                action.TargetId = IdOf(action.Target);
                if (!action.TargetIsLocation) action.TargetPosition = action.Target.transform.position;
            }
        }

        private static VisualEvent Copy(VisualEvent source, Phase phase)
        {
            return new VisualEvent
            {
                Owner = source.Owner,
                ActionId = source.ActionId,
                Phase = phase,
                ActionType = source.ActionType,
                Variation = source.Variation,
                Speed = source.Speed,
                Flags = source.Flags,
                Target = source.Target,
                TargetPosition = source.TargetPosition,
            };
        }

        private void Probe(VisualEvent visual, string role)
        {
            if (!_probe) return;
            string data = "{\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"role\":\"" + role
                + "\",\"owner\":\"" + visual.Owner + "\",\"actionId\":" + visual.ActionId
                + ",\"phase\":\"" + visual.Phase + "\",\"actionType\":\"" + visual.ActionType
                + "\",\"variation\":" + visual.Variation + ",\"speed\":" + visual.Speed.ToString("R", CultureInfo.InvariantCulture)
                + ",\"flags\":" + visual.Flags + ",\"target\":\"" + visual.Target + "\"}";
            _session.Note("TEST attack-visual " + data);
        }

        private void Queue(VisualEvent visual)
        {
            if (visual.Owner == Guid.Empty || !IsFinite(visual.TargetPosition)
                || !WithinBounds(visual.TargetPosition) || !IsFinite(visual.Speed)) return;
            if (_pending.Count >= MaxPendingEvents)
            {
                if (visual.Phase == Phase.Update) return;
                int replace = _pending.FindIndex(e => e.Phase == Phase.Update);
                if (replace >= 0) _pending.RemoveAt(replace);
                else return;
            }
            _pending.Add(visual);
            Probe(visual, "host");
        }

        private void Flush()
        {
            while (_pending.Count > 0)
            {
                int count = Math.Min(_pending.Count, MaxEventsPerMessage);
                _sequence = _sequence == int.MaxValue ? 1 : _sequence + 1;
                MessageWriter message = new MessageWriter(MessageType.AttackVisuals)
                    .Int(_epoch).Int(_sequence).Int(count);
                for (int i = 0; i < count; i++)
                {
                    VisualEvent visual = _pending[i];
                    message.Guid(visual.Owner).Int(visual.ActionId).Byte((byte)visual.Phase)
                        .Byte((byte)visual.ActionType).Int(visual.Variation).Float(visual.Speed)
                        .Byte(visual.Flags).Guid(visual.Target)
                        .Float(visual.TargetPosition.x).Float(visual.TargetPosition.y).Float(visual.TargetPosition.z);
                }
                _pending.RemoveRange(0, count);
                _session.SendToGuests(message.ToArray());
                PerfStats.Count("attackvisual.delta");
                PerfStats.Count("attackvisual.records", count);
            }
        }

        /// <summary>
        /// Ends active actions whose attack or owner went away. While nothing is
        /// active the pass only frees entries of destroyed attacks, so it runs at
        /// IdlePruneInterval instead of every frame.
        /// </summary>
        private void PruneHostActions(float now)
        {
            if (!_anyHostActive && now < _nextIdlePrune) return;
            _nextIdlePrune = now + IdlePruneInterval;
            bool anyActive = false;
            foreach (var pair in _hostActions)
            {
                HostAction action = pair.Value;
                if (pair.Key != null && action.Owner != null)
                {
                    if (action.Active && (!pair.Key.isActiveAndEnabled || !action.Owner.activeInHierarchy))
                        FinishHostAction(action, Phase.End);
                    anyActive |= action.Active;
                    continue;
                }
                if (action.Active) FinishHostAction(action, Phase.End);
                _pruned.Add(pair.Key);
            }
            _anyHostActive = anyActive;
            if (_pruned.Count == 0) return;
            for (int i = 0; i < _pruned.Count; i++) _hostActions.Remove(_pruned[i]);
            _pruned.Clear();
        }

        // ---- guest presentation ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.AttackVisuals || !_session.IsGuest) return;
            SyncGuestSession();
            long started = PerfStats.Begin();
            int epoch = reader.Int();
            int sequence = reader.Int();
            int count = reader.Int();
            if (epoch < 0 || sequence < 0) throw new FormatException("attack visual epoch/sequence");
            if (count < 0 || count > MaxEventsPerMessage) throw new FormatException("attack visual count " + count);

            // Events are not kept after they are applied, so the objects are reused.
            for (int i = 0; i < count; i++)
            {
                Guid owner = reader.Guid();
                int actionId = reader.Int();
                Phase phase = (Phase)reader.Byte();
                int actionType = reader.Byte();
                int variation = reader.Int();
                float speed = reader.Float();
                byte flags = reader.Byte();
                Guid target = reader.Guid();
                Vector3 targetPosition = new Vector3(reader.Float(), reader.Float(), reader.Float());
                if (owner == Guid.Empty || actionId <= 0 || phase < Phase.Start || phase > Phase.Interrupt
                    || actionType <= (int)AnimationController.ActionType.None || actionType >= (int)AnimationController.ActionType.Pending
                    || variation < 0 || variation > MaxVariation || !IsFinite(speed) || speed < 0f || speed > MaxSpeed
                    || (flags & ~(OffhandFlag | InstantFlag | LoopFlag)) != 0
                    || !IsFinite(targetPosition) || !WithinBounds(targetPosition))
                    throw new FormatException("invalid attack visual");
                VisualEvent visual;
                if (i < _received.Count) visual = _received[i];
                else _received.Add(visual = new VisualEvent());
                visual.Owner = owner;
                visual.ActionId = actionId;
                visual.Phase = phase;
                visual.ActionType = (AnimationController.ActionType)actionType;
                visual.Variation = variation;
                visual.Speed = speed;
                visual.Flags = flags;
                visual.Target = target;
                visual.TargetPosition = targetPosition;
            }

            if (_guestEpoch >= 0 && epoch < _guestEpoch || epoch == _guestEpoch && sequence <= _guestSequence)
            {
                PerfStats.Count("attackvisual.skipped", count);
                PerfStats.End("attackvisual.apply", started);
                return;
            }
            if (epoch != _guestEpoch)
            {
                ClearGuestActions(false);
                _guestEpoch = epoch;
            }
            _guestSequence = sequence;
            for (int i = 0; i < count; i++) ApplyGuestEvent(_received[i]);
            PerfStats.Count("attackvisual.records", count);
            PerfStats.End("attackvisual.apply", started);
        }

        private void ApplyGuestEvent(VisualEvent visual)
        {
            Probe(visual, "guest");
            bool terminal = visual.Phase == Phase.End || visual.Phase == Phase.Interrupt;
            if (_guestActions.TryGetValue(visual.Owner, out GuestAction current)
                && (terminal && current.ActionId != visual.ActionId || current.ActionId > visual.ActionId)) return;

            if (terminal)
            {
                if (current != null)
                {
                    FinishGuestAction(current, visual.Phase == Phase.Interrupt);
                    _guestActions.Remove(visual.Owner);
                }
                return;
            }

            GameObject owner = Resolve(visual.Owner);
            if (owner == null) return;

            bool restart = current == null || current.ActionId != visual.ActionId || visual.Phase == Phase.Start;
            if (current != null && current.ActionId != visual.ActionId) FinishGuestAction(current, false);
            if (current == null || current.ActionId != visual.ActionId)
            {
                current = new GuestAction { Owner = owner, ActionId = visual.ActionId };
                _guestActions[visual.Owner] = current;
            }
            current.Owner = owner;
            current.LastSeen = Time.unscaledTime;
            CollectControllers(owner, current.Controllers);
            foreach (AnimationController controller in current.Controllers)
                ApplyController(controller, visual, restart);
        }

        private static void CollectControllers(GameObject owner, List<AnimationController> controllers)
        {
            AddController(owner.GetComponent<AnimationController>(), controllers);
            Equipment equipment = owner.GetComponent<Equipment>();
            if (equipment == null || equipment.CurrentItems == null) return;
            AddItemController(equipment.CurrentItems.PrimaryWeapon, controllers);
            AddItemController(equipment.CurrentItems.SecondaryWeapon, controllers);
            Equippable grimoire = equipment.CurrentItems.GetItemInSlot(Equippable.EquipmentSlot.Grimoire);
            if (grimoire != null && equipment.ShouldGrimoireBeDisplayed()) AddItemController(grimoire, controllers);
        }

        private static void AddItemController(Equippable item, List<AnimationController> controllers)
        {
            if (item != null) AddController(item.GetComponent<AnimationController>(), controllers);
        }

        private static void AddController(AnimationController controller, List<AnimationController> controllers)
        {
            if (controller != null && !controllers.Contains(controller)) controllers.Add(controller);
        }

        private static void ApplyController(AnimationController controller, VisualEvent visual, bool restart)
        {
            if (controller == null) return;
            if (controller.CurrentAvatar == null) controller.BindComponents();
            if (restart) controller.ClearActions();
            if (controller.DesiredAction == null) controller.DesiredAction = new AnimationController.Action();
            AnimationController.Action action = controller.DesiredAction;
            action.m_actionType = visual.ActionType;
            action.m_variation = visual.Variation;
            action.m_speed = visual.Speed;
            action.m_offhand = (visual.Flags & OffhandFlag) != 0;
            controller.TimeScale = visual.Speed;
            controller.SetBool("Offhand", action.m_offhand);
            if (controller.CurrentAvatar != null)
            {
                controller.Instant = (visual.Flags & InstantFlag) != 0;
                controller.Loop = (visual.Flags & LoopFlag) != 0;
            }
            Equippable item = controller.GetComponent<Equippable>();
            if (item != null && item.EquippedSlot == Equippable.EquipmentSlot.Grimoire) item.IsAnimating = true;
        }

        private static void FinishGuestAction(GuestAction action, bool interrupt)
        {
            foreach (AnimationController controller in action.Controllers)
            {
                if (controller == null) continue;
                if (controller.DesiredAction != null) controller.DesiredAction.Reset();
                controller.ClearActions();
                if (interrupt) controller.Interrupt();
                if (controller.CurrentAvatar != null) controller.Instant = false;
                Equippable item = controller.GetComponent<Equippable>();
                if (item != null && item.EquippedSlot == Equippable.EquipmentSlot.Grimoire) item.IsAnimating = false;
            }
            action.Controllers.Clear();
        }

        private void PruneGuestActions(float now)
        {
            List<Guid> remove = null;
            foreach (var pair in _guestActions)
            {
                if (pair.Value.Owner != null && now - pair.Value.LastSeen < GuestTimeout) continue;
                FinishGuestAction(pair.Value, false);
                (remove ?? (remove = new List<Guid>())).Add(pair.Key);
            }
            if (remove == null) return;
            foreach (Guid id in remove) _guestActions.Remove(id);
        }

        private void ClearGuestActions(bool interrupt)
        {
            foreach (GuestAction action in _guestActions.Values) FinishGuestAction(action, interrupt);
            _guestActions.Clear();
        }

        // ---- death/removal cleanup for VitalsSync, PositionSync and EntityRegistry ----

        public static void OnEntityDeath(GameObject entity)
        {
            if (entity != null) OnEntityDeath(IdOf(entity));
        }

        public static void OnEntityDeath(Guid networkId)
        {
            AttackVisualSync self = s_instance;
            if (self == null || networkId == Guid.Empty) return;
            if (self._session.IsHost) self.FinishOwner(networkId, Phase.End);
            else if (self._session.IsGuest) self.FinishGuest(networkId);
        }

        public static void OnEntityRemoved(GameObject entity)
        {
            if (entity != null) OnEntityRemoved(IdOf(entity));
        }

        public static void OnEntityRemoved(Guid networkId)
        {
            AttackVisualSync self = s_instance;
            if (self == null || networkId == Guid.Empty) return;
            if (self._session.IsHost) self.FinishOwner(networkId, Phase.End);
            else if (self._session.IsGuest) self.FinishGuest(networkId);
        }

        private void FinishOwner(Guid ownerId, Phase phase)
        {
            foreach (HostAction action in _hostActions.Values)
                if (action.Active && action.OwnerId == ownerId) FinishHostAction(action, phase);
        }

        private void FinishGuest(Guid ownerId)
        {
            if (!_guestActions.TryGetValue(ownerId, out GuestAction action)) return;
            FinishGuestAction(action, false);
            _guestActions.Remove(ownerId);
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

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool WithinBounds(Vector3 value)
        {
            return Mathf.Abs(value.x) <= MaxPosition && Mathf.Abs(value.y) <= MaxPosition && Mathf.Abs(value.z) <= MaxPosition;
        }
    }
}
