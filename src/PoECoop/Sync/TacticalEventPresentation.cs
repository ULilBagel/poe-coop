using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Rebuilds the native tactical objects used by the UI without running their
    /// gameplay lifecycle. The manager's m_events list instance is retained;
    /// sorting, NotifyEventOrderChanged, PopEvent, OnStart and OnEnd are never used.
    /// </summary>
    internal sealed class TacticalEventPresentation : IDisposable
    {
        private static readonly AccessTools.FieldRef<TacticalModeManager, List<TacticalEvent>> EventsField =
            AccessTools.FieldRefAccess<TacticalModeManager, List<TacticalEvent>>("m_events");
        private static readonly AccessTools.FieldRef<TacticalModeManager, TacticalEvent> CurrentEventField =
            AccessTools.FieldRefAccess<TacticalModeManager, TacticalEvent>("<CurrentEvent>k__BackingField");
        private static readonly AccessTools.FieldRef<TacticalModeManager, float> CurrentEventRealTimerField =
            AccessTools.FieldRefAccess<TacticalModeManager, float>("<CurrentEventRealTimer>k__BackingField");
        private static readonly AccessTools.FieldRef<TacticalModeManager, float> CurrentEventTimerField =
            AccessTools.FieldRefAccess<TacticalModeManager, float>("m_currentEventTimer");
        private static readonly AccessTools.FieldRef<TacticalModeManager, float> SpinUpTimerField =
            AccessTools.FieldRefAccess<TacticalModeManager, float>("m_spinUpTimer");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> SpinDownCompleteField =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_spinDownComplete");
        private static readonly AccessTools.FieldRef<TacticalModeManager, IEnumerator> SpinDownRoutineField =
            AccessTools.FieldRefAccess<TacticalModeManager, IEnumerator>("m_spinDownRoutine");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> HasRoundStartField =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_hasRoundStart");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> PartyInCombatField =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_isPartyMemberInCombat");
        private static readonly AccessTools.FieldRef<TacticalModeManager, bool> RefreshLockedField =
            AccessTools.FieldRefAccess<TacticalModeManager, bool>("m_refreshCurrentEventLocked");
        private static readonly FieldInfo TurnLocksField = AccessTools.Field(typeof(TacticalModeManager), "m_turnLockedBy");
        private static readonly FieldInfo TurnLockTimeoutField =
            TurnLocksField != null ? AccessTools.Field(TurnLocksField.FieldType.GetGenericArguments()[0], "TimeOutTime") : null;

        private static readonly AccessTools.FieldRef<AIController, bool> AvailableActionField =
            AccessTools.FieldRefAccess<AIController, bool>("m_availableAction");
        private static readonly AccessTools.FieldRef<AIController, float> AvailableMovementField =
            AccessTools.FieldRefAccess<AIController, float>("m_availableMovement");
        private static readonly AccessTools.FieldRef<AIController, bool> UsedFreePotionField =
            AccessTools.FieldRefAccess<AIController, bool>("UsedFreePotion");
        private static readonly AccessTools.FieldRef<AIController, List<GenericAbility>> UsedFreeActionsField =
            AccessTools.FieldRefAccess<AIController, List<GenericAbility>>("UsedFreeActions");

        private static readonly AccessTools.FieldRef<CharacterStats, float> CurrentInitiativeField =
            AccessTools.FieldRefAccess<CharacterStats, float>("m_CurrentInitiative");
        private static readonly AccessTools.FieldRef<CharacterStats, float> TempInitiativeField =
            AccessTools.FieldRefAccess<CharacterStats, float>("m_TempInitiative");
        private static readonly AccessTools.FieldRef<CharacterStats, float> PreviousIncrementField =
            AccessTools.FieldRefAccess<CharacterStats, float>("<PreviousInitiativeIncrement>k__BackingField");
        private static readonly AccessTools.FieldRef<CharacterStats, float> PreviousBaseInitiativeField =
            AccessTools.FieldRefAccess<CharacterStats, float>("<PreviousBaseInitiative>k__BackingField");
        private static readonly AccessTools.FieldRef<CharacterStats, float> PreviousBaseReloadField =
            AccessTools.FieldRefAccess<CharacterStats, float>("<PreviousBaseReload>k__BackingField");
        private static readonly AccessTools.FieldRef<CharacterStats, float> InitiativeReloadField =
            AccessTools.FieldRefAccess<CharacterStats, float>("<InitiativeDelayFromReload>k__BackingField");

        private static readonly FieldInfo ActorGameObjectField = AccessTools.Field(typeof(TacticalEvent), "ActorGameObject");
        private static readonly FieldInfo ActorStatsField = AccessTools.Field(typeof(TacticalEvent), "ActorStats");
        private static readonly FieldInfo ActorHealthField = AccessTools.Field(typeof(TacticalEvent), "ActorHealth");
        private static readonly FieldInfo ActorFactionField = AccessTools.Field(typeof(TacticalEvent), "ActorFaction");
        private static readonly FieldInfo CastInitiativeField = AccessTools.Field(typeof(TacticalCastFinishedEvent), "m_initiativeDelay");
        private static readonly FieldInfo CastAttackTimeField = AccessTools.Field(typeof(TacticalCastFinishedEvent), "m_initialAttackTime");
        private static readonly FieldInfo RoundCountField = AccessTools.Field(typeof(TacticalRoundStartEvent), "m_RoundCount");
        private static readonly FieldInfo TotalRoundsField = AccessTools.Field(typeof(TacticalRoundStartEvent), "TotalRounds");
        private static readonly MethodInfo ConsumeRoundMethod = AccessTools.Method(typeof(TacticalRoundStartEvent), "ConsumeRound");
        private static readonly FieldInfo ProjectileListField = AccessTools.Field(typeof(TacticalProjectileTurnEvent), "Projectiles");
        private static readonly FieldInfo ProjectileBiasField = AccessTools.Field(typeof(TacticalProjectileTurnEvent), "initiativeBias");

        private readonly Session _session;
        private readonly Dictionary<int, TacticalEvent> _events = new Dictionary<int, TacticalEvent>();
        private readonly Dictionary<int, TacticalEventRecord> _records = new Dictionary<int, TacticalEventRecord>();
        private readonly Dictionary<Guid, AIController> _appliedActors = new Dictionary<Guid, AIController>();
        private readonly List<int> _queue = new List<int>();
        private bool _hasSnapshot;
        private int _activeEventId;
        private GameObject _cameraActor;

        public TacticalEventPresentation(Session session)
        {
            _session = session;
        }

        public void Dispose()
        {
            Reset(false);
            _records.Clear();
            _queue.Clear();
            _appliedActors.Clear();
        }

        internal bool Apply(TacticalResolvedSnapshot resolved)
        {
            using (TacticalStateSync.HostApplyScope())
            {
                TacticalSnapshot snapshot = resolved.Snapshot;
                if (GameState.Option == null) return false;
                snapshot.Settings.Apply();
                GameState.Option.TacticalMode = snapshot.Mode;
                if (snapshot.Mode == TacticalMode.Disabled)
                {
                    Reset(true);
                    return true;
                }

                TacticalModeManager manager = TacticalModeManager.Instance;
                if (manager == null) return false;
                bool orderChanged = OrderChanged(snapshot);
                int previousActive = _activeEventId;

                ReconcileEvents(manager, resolved);
                ApplyManager(manager, snapshot);
                ApplyActors(snapshot, resolved);

                int activeEventId = snapshot.CurrentEventId != 0 && snapshot.Events.Find(e => e.Id == snapshot.CurrentEventId).Active
                    ? snapshot.CurrentEventId : 0;
                TacticalEvent current = snapshot.CurrentEventId != 0 && _events.TryGetValue(snapshot.CurrentEventId, out TacticalEvent found)
                    ? found : null;
                GameObject cameraActor = activeEventId != 0 && current != null ? current.ActorGameObject : null;

                Remember(snapshot);
                if (orderChanged) TriggerUiOnly(TacticalModeManager.OnEventOrderChanged);
                UpdateCamera(cameraActor);
                if (activeEventId != previousActive)
                {
                    _activeEventId = activeEventId;
                    if (activeEventId != 0 && current != null)
                    {
                        TriggerUiOnly(TacticalModeManager.OnEventStart, current);
                        if (current is TacticalTurnEvent turn && turn.Actor != null)
                        {
                            TriggerUiOnly(TacticalModeManager.OnTurnStart, turn.Actor);
                            SelectOwnedActor(turn.Actor);
                        }
                    }
                }
                else _activeEventId = activeEventId;
                return true;
            }
        }

        private void ApplyManager(TacticalModeManager manager, TacticalSnapshot snapshot)
        {
            manager.AutoPassTurns = snapshot.AutoPass;
            manager.UnlimitedMovement = snapshot.UnlimitedMovement;
            manager.UnlimitedActions = snapshot.UnlimitedActions;
            HasRoundStartField(manager) = snapshot.HasRoundStart;
            PartyInCombatField(manager) = snapshot.PartyInCombat;
            RefreshLockedField(manager) = snapshot.RefreshLocked;
            CurrentEventField(manager) = snapshot.CurrentEventId != 0 ? _events[snapshot.CurrentEventId] : null;
            CurrentEventTimerField(manager) = snapshot.CurrentTimer;
            CurrentEventRealTimerField(manager) = snapshot.RealTimer;
            SpinUpTimerField(manager) = snapshot.SpinUpTimer;
            SpinDownRoutineField(manager) = null;
            // A remote ending phase has no local coroutine. Keeping the native
            // completion flag true prevents a null routine if another update runs.
            SpinDownCompleteField(manager) = true;
            manager.EffectTime = snapshot.EffectTime;
            ApplyTurnLocks(manager, snapshot.TurnLocks);
        }

        private static void ApplyTurnLocks(TacticalModeManager manager, List<float> locks)
        {
            if (!(TurnLocksField.GetValue(manager) is IList nativeLocks)) return;
            nativeLocks.Clear();
            Type lockType = TurnLocksField.FieldType.GetGenericArguments()[0];
            foreach (float remaining in locks)
            {
                object entry = Activator.CreateInstance(lockType, new object[] { new object() });
                TurnLockTimeoutField.SetValue(entry, Time.time + remaining);
                nativeLocks.Add(entry);
            }
        }

        private void ReconcileEvents(TacticalModeManager manager, TacticalResolvedSnapshot resolved)
        {
            TacticalSnapshot snapshot = resolved.Snapshot;
            var required = new HashSet<int>();
            foreach (TacticalEventRecord record in snapshot.Events)
            {
                required.Add(record.Id);
                if (_events.TryGetValue(record.Id, out TacticalEvent existing) && !Matches(record, existing, resolved))
                {
                    Teardown(existing);
                    _events.Remove(record.Id);
                    existing = null;
                }
                if (existing == null)
                {
                    existing = Create(record, resolved);
                    _events.Add(record.Id, existing);
                }
                UpdateEvent(existing, record, resolved);
            }

            List<int> stale = null;
            foreach (int id in _events.Keys)
                if (!required.Contains(id)) (stale ?? (stale = new List<int>())).Add(id);
            if (stale != null)
                foreach (int id in stale)
                {
                    Teardown(_events[id]);
                    _events.Remove(id);
                }

            List<TacticalEvent> nativeEvents = EventsField(manager);
            if (nativeEvents == null)
            {
                nativeEvents = new List<TacticalEvent>();
                EventsField(manager) = nativeEvents;
            }
            nativeEvents.Clear();
            foreach (int id in snapshot.Queue) nativeEvents.Add(_events[id]);
        }

        private static bool Matches(TacticalEventRecord record, TacticalEvent existing, TacticalResolvedSnapshot resolved)
        {
            AIController actor = record.ActorId != Guid.Empty ? resolved.Actors[record.ActorId].Actor : null;
            switch (record.Kind)
            {
                case TacticalEventKind.Turn:
                    return existing is PresentationTurnEvent && existing.Actor == actor;
                case TacticalEventKind.CastFinished:
                    return existing is PresentationCastFinishedEvent && existing.Actor == actor
                        && ((TacticalCastFinishedEvent)existing).Attack == resolved.CastAttacks[record.Id];
                case TacticalEventKind.RoundStart:
                    return existing is PresentationRoundStartEvent;
                case TacticalEventKind.Delay:
                    return existing is PresentationDelayEvent;
                case TacticalEventKind.TurnLocked:
                    return existing is PresentationTurnLockedEvent;
                case TacticalEventKind.ProjectileTurn:
                    return existing is PresentationProjectileTurnEvent && existing.Actor == actor;
                default:
                    return false;
            }
        }

        private static TacticalEvent Create(TacticalEventRecord record, TacticalResolvedSnapshot resolved)
        {
            AIController actor = record.ActorId != Guid.Empty ? resolved.Actors[record.ActorId].Actor : null;
            switch (record.Kind)
            {
                case TacticalEventKind.Turn:
                    return new PresentationTurnEvent(actor.gameObject);
                case TacticalEventKind.CastFinished:
                    return new PresentationCastFinishedEvent(resolved.CastAttacks[record.Id], actor.gameObject,
                        record.InitiativeSort, record.SurpriseRound);
                case TacticalEventKind.RoundStart:
                    return new PresentationRoundStartEvent();
                case TacticalEventKind.Delay:
                    return new PresentationDelayEvent(record.Duration);
                case TacticalEventKind.TurnLocked:
                    return new PresentationTurnLockedEvent();
                case TacticalEventKind.ProjectileTurn:
                    var projectile = (PresentationProjectileTurnEvent)FormatterServices.GetUninitializedObject(
                        typeof(PresentationProjectileTurnEvent));
                    ProjectileListField.SetValue(projectile, new List<Projectile>());
                    ProjectileBiasField.SetValue(projectile, -1);
                    return projectile;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void UpdateEvent(TacticalEvent tacticalEvent, TacticalEventRecord record, TacticalResolvedSnapshot resolved)
        {
            AIController actor = record.ActorId != Guid.Empty ? resolved.Actors[record.ActorId].Actor : null;
            tacticalEvent.UpdateActor(actor);
            ActorGameObjectField.SetValue(tacticalEvent, actor != null ? actor.gameObject : null);
            ActorStatsField.SetValue(tacticalEvent, actor != null ? actor.CharacterStats : null);
            ActorHealthField.SetValue(tacticalEvent, actor != null ? actor.GetComponent<Health>() : null);
            ActorFactionField.SetValue(tacticalEvent, actor != null ? actor.GetComponent<Faction>() : null);
            tacticalEvent.IsActive = record.Active;
            tacticalEvent.UseSafeDestroy = record.SafeDestroy;

            if (tacticalEvent is PresentationTurnEvent turn)
            {
                turn.SetPreview(record.Preview);
                turn.PreviewInitiativeModifier = record.PreviewInitiativeModifier;
                turn.PassStyle = record.PassStyle;
                turn.SkipTurnStart = record.SkipTurnStart;
                turn.SkipTurnEnd = record.SkipTurnEnd;
                SetPresentationValues(turn, record);
            }
            else if (tacticalEvent is PresentationCastFinishedEvent cast)
            {
                CastInitiativeField.SetValue(cast, record.InitiativeSort);
                CastAttackTimeField.SetValue(cast, record.AttackTime);
                cast.HostPreview = record.Preview;
                SetPresentationValues(cast, record);
            }
            else if (tacticalEvent is PresentationRoundStartEvent round)
            {
                RoundCountField.SetValue(round, record.Round);
                SetPresentationValues(round, record);
            }
            else if (tacticalEvent is PresentationDelayEvent delay)
            {
                SetPresentationValues(delay, record);
            }
            else if (tacticalEvent is PresentationTurnLockedEvent locked)
            {
                SetPresentationValues(locked, record);
            }
            else if (tacticalEvent is PresentationProjectileTurnEvent projectile)
            {
                projectile.OverrideInitiativeDelay = record.InitiativeSort;
                ProjectileBiasField.SetValue(projectile, record.InitiativeBias);
                SetPresentationValues(projectile, record);
            }
        }

        private static void SetPresentationValues(PresentationEventValues tacticalEvent, TacticalEventRecord record)
        {
            tacticalEvent.HostVisible = record.Visible;
            tacticalEvent.HostObscured = record.Obscured;
            tacticalEvent.HostInitiativeDelay = record.InitiativeDelay;
            tacticalEvent.HostInitiativeSort = record.InitiativeSort;
            tacticalEvent.HostRound = record.Round;
            tacticalEvent.HostInitiativeBias = record.InitiativeBias;
            tacticalEvent.HostDuration = record.Duration;
        }

        private void ApplyActors(TacticalSnapshot snapshot, TacticalResolvedSnapshot resolved)
        {
            _appliedActors.Clear();
            foreach (TacticalActorRecord record in snapshot.Actors)
            {
                TacticalActorResolution resolution = resolved.Actors[record.Id];
                AIController actor = resolution.Actor;
                CharacterStats stats = resolution.Stats;
                _appliedActors[record.Id] = actor;

                AvailableActionField(actor) = record.AvailableAction;
                AvailableMovementField(actor) = record.AvailableMovement;
                UsedFreePotionField(actor) = record.UsedFreePotion;
                actor.SwitchedWeaponsThisTurn = record.SwitchedWeapons;
                actor.EvaluateTarget = record.EvaluateTarget;
                actor.TacticalTurnEventCount = record.TurnEventCount;
                List<GenericAbility> freeActions = UsedFreeActionsField(actor);
                if (freeActions == null)
                {
                    freeActions = new List<GenericAbility>();
                    UsedFreeActionsField(actor) = freeActions;
                }
                freeActions.Clear();
                freeActions.AddRange(resolution.FreeActions);

                CurrentInitiativeField(stats) = record.CurrentInitiative;
                TempInitiativeField(stats) = record.TempInitiative;
                PreviousIncrementField(stats) = record.PreviousInitiativeIncrement;
                PreviousBaseInitiativeField(stats) = record.PreviousBaseInitiative;
                PreviousBaseReloadField(stats) = record.PreviousBaseReload;
                InitiativeReloadField(stats) = record.InitiativeDelayFromReload;
            }
        }

        internal void Reset(bool notifyUi)
        {
            using (TacticalStateSync.HostApplyScope())
            {
                bool hadPresentation = _hasSnapshot || _events.Count > 0 || _activeEventId != 0 || _cameraActor != null;
                UpdateCamera(null);
                foreach (TacticalEvent tacticalEvent in _events.Values) Teardown(tacticalEvent);
                _events.Clear();
                _records.Clear();
                _queue.Clear();
                _hasSnapshot = false;
                _activeEventId = 0;

                TacticalModeManager manager = TacticalModeManager.Instance;
                if (manager != null)
                {
                    List<TacticalEvent> nativeEvents = EventsField(manager);
                    if (nativeEvents != null) nativeEvents.Clear();
                    CurrentEventField(manager) = null;
                    CurrentEventTimerField(manager) = 0f;
                    CurrentEventRealTimerField(manager) = 0f;
                    SpinUpTimerField(manager) = 0f;
                    SpinDownRoutineField(manager) = null;
                    SpinDownCompleteField(manager) = true;
                    HasRoundStartField(manager) = false;
                    PartyInCombatField(manager) = false;
                    RefreshLockedField(manager) = false;
                    manager.EffectTime = 0f;
                    if (TurnLocksField.GetValue(manager) is IList locks) locks.Clear();
                }
                foreach (AIController actor in _appliedActors.Values)
                    if (actor != null) actor.TacticalTurnEventCount = 0;
                _appliedActors.Clear();
                if (notifyUi && hadPresentation) TriggerUiOnly(TacticalModeManager.OnEventOrderChanged);
            }
        }

        private static void Teardown(TacticalEvent tacticalEvent)
        {
            // Turn constructors increment the actor count. SafeDestroy balances
            // exactly that increment and, unlike OnDestroy, does not alter AI state.
            if (tacticalEvent is TacticalTurnEvent turn)
            {
                turn.SafeDestroy();
                return;
            }
            // Round constructors also subscribe and increment a static count.
            // Unsubscribe/decrement directly so teardown does not trigger native
            // OnRoundConsumed or any actor/environment gameplay callback.
            if (tacticalEvent is TacticalRoundStartEvent)
            {
                Action consume = (Action)Delegate.CreateDelegate(typeof(Action), tacticalEvent, ConsumeRoundMethod);
                TacticalModeManager.OnRoundConsumed.Unsubscribe(consume);
                int total = (int)TotalRoundsField.GetValue(null);
                TotalRoundsField.SetValue(null, Math.Max(0, total - 1));
            }
        }

        private void UpdateCamera(GameObject desired)
        {
            if (_cameraActor == desired) return;
            if (_cameraActor != null && SmartCamera.Instance != null)
                SmartCamera.Instance.OnStopsBeingCurrentActor(_cameraActor);
            _cameraActor = desired;
            if (_cameraActor != null && SmartCamera.Instance != null)
                SmartCamera.Instance.OnBecomesCurrentActor(_cameraActor);
            if (_cameraActor != null && TacticalEvent.IsCenterCameraOption(_cameraActor) && CameraControl.Instance != null)
                CameraControl.Instance.FocusOnObject(_cameraActor, 0.5f);
        }

        private static void SelectOwnedActor(AIController actor)
        {
            var member = actor as PartyMemberAI;
            if (member == null || !member.IsControllablePartyMember() || !Ownership.MayLocalPlayerSelect(member)) return;
            PartyMemberAI.ExclusiveSelectPartyMember(member, false);
        }

        private bool OrderChanged(TacticalSnapshot snapshot)
        {
            if (!_hasSnapshot) return snapshot.Queue.Count > 0;
            if (_queue.Count != snapshot.Queue.Count) return true;
            for (int i = 0; i < _queue.Count; i++)
            {
                int id = snapshot.Queue[i];
                if (_queue[i] != id || !_records.TryGetValue(id, out TacticalEventRecord previous)) return true;
                TacticalEventRecord next = snapshot.Events.Find(e => e.Id == id);
                if (next == null || PresentationChanged(previous, next)) return true;
            }
            return false;
        }

        private void Remember(TacticalSnapshot snapshot)
        {
            _queue.Clear();
            _queue.AddRange(snapshot.Queue);
            _records.Clear();
            foreach (TacticalEventRecord record in snapshot.Events) _records[record.Id] = record;
            _hasSnapshot = true;
        }

        private static bool PresentationChanged(TacticalEventRecord a, TacticalEventRecord b)
        {
            return a.Kind != b.Kind || a.ActorId != b.ActorId || a.Preview != b.Preview || a.Visible != b.Visible
                || a.Obscured != b.Obscured || a.SurpriseRound != b.SurpriseRound || a.SkipTurnStart != b.SkipTurnStart
                || a.SkipTurnEnd != b.SkipTurnEnd || a.SafeDestroy != b.SafeDestroy || a.PassStyle != b.PassStyle
                || a.Round != b.Round || a.InitiativeDelay != b.InitiativeDelay || a.InitiativeSort != b.InitiativeSort
                || a.PreviewInitiativeModifier != b.PreviewInitiativeModifier || a.InitiativeBias != b.InitiativeBias
                || a.Duration != b.Duration || a.AttackTime != b.AttackTime || AttackChanged(a.Attack, b.Attack);
        }

        private static bool AttackChanged(TacticalAttackIdentity a, TacticalAttackIdentity b)
        {
            return a.Name != b.Name || a.Occurrence != b.Occurrence || a.Ability.Name != b.Ability.Name
                || a.Ability.DisplayId != b.Ability.DisplayId || a.Ability.Occurrence != b.Ability.Occurrence;
        }

        // Onyx events also accept gameplay listeners. Invoke only delegates owned
        // by native UI types, and never actor/environment turn or time callbacks.
        private static void TriggerUiOnly(OnyxEvent source)
        {
            foreach (Action callback in UiDelegates<Action>(source)) InvokeUi(callback, () => callback());
        }

        private static void TriggerUiOnly<T>(OnyxEvent<T> source, T value)
        {
            foreach (Action<T> callback in UiDelegates<Action<T>>(source)) InvokeUi(callback, () => callback(value));
        }

        private static IEnumerable<T> UiDelegates<T>(object source) where T : class
        {
            FieldInfo field = null;
            for (Type type = source.GetType(); type != null && field == null; type = type.BaseType)
                field = type.GetField("m_delegates", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(field?.GetValue(source) is IEnumerable delegates)) yield break;
            var snapshot = new List<T>();
            foreach (object entry in delegates)
                if (entry is T callback && callback is Delegate del && IsUiDelegate(del)) snapshot.Add(callback);
            foreach (T callback in snapshot) yield return callback;
        }

        private static bool IsUiDelegate(Delegate callback)
        {
            Type type = callback.Target != null ? callback.Target.GetType() : callback.Method.DeclaringType;
            return type != null && type.FullName != null && type.FullName.StartsWith("UI", StringComparison.Ordinal);
        }

        private static void InvokeUi(Delegate callback, Action invoke)
        {
            try { invoke(); }
            catch (Exception ex) { Plugin.Log.LogWarning("Tactical UI callback failed: " + ex.Message); }
        }

        private interface PresentationEventValues
        {
            bool HostVisible { get; set; }
            bool HostObscured { get; set; }
            float HostInitiativeDelay { get; set; }
            float HostInitiativeSort { get; set; }
            int HostRound { get; set; }
            int HostInitiativeBias { get; set; }
            float HostDuration { get; set; }
        }

        private sealed class PresentationTurnEvent : TacticalTurnEvent, PresentationEventValues
        {
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
            public override float InitiativeDelay => HostInitiativeDelay;
            public override float InitiativeSort => HostInitiativeSort;
            public override int RoundOffset => HostRound;
            public override int InitiativeBias { get => HostInitiativeBias; set => HostInitiativeBias = value; }
            public override float TimeOutDuration => HostDuration;
            public PresentationTurnEvent(GameObject actor) : base(actor) { }
        }

        private sealed class PresentationCastFinishedEvent : TacticalCastFinishedEvent, PresentationEventValues
        {
            public bool HostPreview;
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsPreview => HostPreview;
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
            public override float InitiativeDelay => HostInitiativeDelay;
            public override float InitiativeSort => HostInitiativeSort;
            public override int RoundOffset => HostRound;
            public override int InitiativeBias { get => HostInitiativeBias; set => HostInitiativeBias = value; }
            public PresentationCastFinishedEvent(AttackBase attack, GameObject actor, float initiative, bool surprise)
                : base(attack, actor, initiative, surprise) { }
        }

        private sealed class PresentationRoundStartEvent : TacticalRoundStartEvent, PresentationEventValues
        {
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
            public override float InitiativeDelay => HostInitiativeDelay;
            public override float InitiativeSort => HostInitiativeSort;
            public override int RoundOffset => HostRound;
            public override int InitiativeBias { get => HostInitiativeBias; set => HostInitiativeBias = value; }
        }

        private sealed class PresentationDelayEvent : TacticalDelayEvent, PresentationEventValues
        {
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
            public override float TimeOutDuration => HostDuration;
            public PresentationDelayEvent(float duration) : base(duration) { }
        }

        private sealed class PresentationTurnLockedEvent : TacticalTurnLockedEvent, PresentationEventValues
        {
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
        }

        private sealed class PresentationProjectileTurnEvent : TacticalProjectileTurnEvent, PresentationEventValues
        {
            public bool HostVisible { get; set; }
            public bool HostObscured { get; set; }
            public float HostInitiativeDelay { get; set; }
            public float HostInitiativeSort { get; set; }
            public int HostRound { get; set; }
            public int HostInitiativeBias { get; set; }
            public float HostDuration { get; set; }
            public override bool IsVisible => HostVisible;
            public override bool IsObscured => HostObscured;
            public override float InitiativeDelay => HostInitiativeDelay;
            public override float InitiativeSort => HostInitiativeSort;
            public override int RoundOffset => HostRound;
            public override int InitiativeBias { get => HostInitiativeBias; set => HostInitiativeBias = value; }
            public override float TimeOutDuration => HostDuration;
            private PresentationProjectileTurnEvent(List<Projectile> projectiles) : base(projectiles) { }
        }
    }
}
