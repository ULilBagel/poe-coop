using System;
using System.Collections.Generic;
using AI.Player;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Player orders. A guest's clicks go through the game's own code, which ends
    /// in an order state pushed onto the character's AI stack. The guest's AI is
    /// frozen, so the state just sits there: Collect() reads it, sends it to the
    /// host and removes it. The host checks the sender controls that character and
    /// then pushes the same state the game would have (code-survey.md, section 2).
    /// Covers move, attack, use, stop and abilities, including containers.
    /// </summary>
    public sealed class Orders
    {
        private enum Kind : byte
        {
            Move = 1,
            Attack = 2,
            Use = 3,
            Stop = 4,
            TargetedAbility = 5,
            NonTargetedAbility = 6,
        }

        private enum TargetMode : byte { None = 0, Object = 1, Position = 2 }

        private sealed class PendingOrder
        {
            public AIState State;
            public bool Queued;
        }

        /// <summary>An order to walk further than this from where the character stands is refused.</summary>
        private const float MaxMoveDistance = 500f;
        private const float MaxAbilityTargetDistance = 500f;
        private const float MaxOrderCoordinate = 100000f;
        private const int MaxAbilityNameLength = 128;
        private const int MaxAbilityOccurrence = 511;
        private const int MaxStackStates = 64;

        private static Orders s_instance;
        private static readonly HashSet<GenericAbility> UiTriggers = new HashSet<GenericAbility>();
        private static readonly AccessTools.FieldRef<GenericAbility, bool> AbilityLaunching =
            AccessTools.FieldRefAccess<GenericAbility, bool>("m_activatedLaunching");

        private readonly Session _session;
        private readonly Ownership _ownership;
        private float _nextNote;

        public Orders(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            s_instance = this;
            session.GameMessage += OnGameMessage;
        }

        // ---- guest ----

        internal static void MarkUiTrigger(GenericAbility ability)
        {
            if (Plugin.IsMirroring && !AbilityStateSync.IsApplyingHostState && ability != null)
                UiTriggers.Add(ability);
        }

        /// <summary>Runs only GenericAbility.Update's UI dispatch for a locally triggered ability.</summary>
        internal static void ProcessUiTrigger(GenericAbility ability)
        {
            if (ability == null || !UiTriggers.Contains(ability)) return;
            Orders self = s_instance;
            PartyMemberAI member = ability.OwnerAI;
            if (self == null || member == null || !self._ownership.IsMine(member) || ability.Passive)
            {
                UiTriggers.Remove(ability);
                return;
            }

            if (ability.Modal)
            {
                self.ProcessModalUiTrigger(member, ability);
                return;
            }

            UiTriggers.Remove(ability);
            ability.UntriggerFromUI();
            if (!member.Selected) return;
            if (ability.TriggerOnHit)
            {
                CaptureImmediateAbility(member, ability);
                return;
            }
            DispatchUiAttack(member, ability);
        }

        private void ProcessModalUiTrigger(PartyMemberAI member, GenericAbility ability)
        {
            if (ability.UiActivated == (ability.Activated || AbilityLaunching(ability)))
            {
                UiTriggers.Remove(ability);
                return;
            }
            if (ability.Activated)
            {
                UiTriggers.Remove(ability);
                if (!GameState.Paused) CaptureImmediateAbility(member, ability);
                return;
            }
            if (member.QueuedAbility == ability)
            {
                member.QueuedAbility = null;
                UiTriggers.Remove(ability);
                return;
            }
            if (GameState.Paused && ability.Attack == null) return;
            if (!ability.UiActivated)
            {
                UiTriggers.Remove(ability);
                return;
            }

            Health health = member.GetComponent<Health>();
            if (health != null && (health.Dead || health.Unconscious))
            {
                UiTriggers.Remove(ability);
                return;
            }
            if (!ability.Ready) return;

            UiTriggers.Remove(ability);
            TriggerUiAttack(member, ability, ability.gameObject, false, null, null, null, -1);
        }

        private static void DispatchUiAttack(PartyMemberAI member, GenericAbility ability)
        {
            if (!TryBuildAttack(member, ability, out AttackBase attack, out AttackBase weaponAttack,
                out StatusEffect[] effects, out bool fullAttack, out int animVariation)) return;

            bool weaponBased = ability.UsePrimaryAttack || ability.UseFullAttack;
            GameObject attackObject = weaponBased && attack != null ? attack.gameObject : ability.gameObject;
            TriggerUiAttack(member, ability, attackObject, fullAttack, weaponBased ? ability : null,
                effects, weaponAttack, animVariation);
        }

        private static void TriggerUiAttack(PartyMemberAI member, GenericAbility ability, GameObject attackObject,
            bool fullAttack, GenericAbility weaponAbility, StatusEffect[] effects, AttackBase weaponAttack, int animVariation)
        {
            AbilityLaunching(ability) = true;
            var args = new GameEventArgs
            {
                Type = GameEventType.Ability,
                GameObjectData = new[] { attackObject, ability.Owner },
                GenericData = new object[] { "activate", fullAttack ? "full" : "", effects, animVariation, weaponAttack },
            };
            member.TriggerAbility(args, weaponAbility);
        }

        /// <summary>Call once per frame.</summary>
        public void Collect()
        {
            if (!Plugin.IsMirroring) return;
            bool queued = GameInput.GetControl(MappedControl.QUEUE);
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
            {
                if (member == null) continue;
                AIStateManager states = member.StateManager;
                if (states == null || states.CurrentState == null || states.CurrentState == states.DefaultState) continue;

                // Snapshot the whole stack before changing it. In particular, do not
                // discard a reaction or death state which happens to sit above an order.
                var pending = new List<PendingOrder>();
                for (int depth = 0; depth < MaxStackStates; depth++)
                {
                    AIState state = states.GetQueuedState(depth);
                    if (state == null) break;
                    if (IsRecognized(state))
                        pending.Add(new PendingOrder
                        {
                            State = state,
                            // TriggerAbility only creates this state for a queued order,
                            // even when it was the first state and became CurrentState.
                            Queued = queued || depth > 0 || state is AI.Player.Ability,
                        });
                }

                if (_ownership.IsMine(member))
                    foreach (PendingOrder order in pending) SendOrder(member, order.State, order.Queued);

                foreach (PendingOrder order in pending)
                    if (states.IsStateInStack(order.State)) states.PopState(order.State);
                if (pending.Count > 0) states.PostUpdate(); // hands the removed states back to the game's pool
            }
        }

        private static bool IsRecognized(AIState state)
        {
            return state is TargetedAttack || state is Attack || state is AI.Player.Ability
                || state is UseObject || state is Move;
        }

        private void SendOrder(PartyMemberAI member, AIState state, bool queued)
        {
            Guid who = Ownership.IdOf(member);
            if (who == Guid.Empty) return;
            if (InPresentedTacticalCombat()) queued = false;

            if (state is TargetedAttack targeted)
            {
                GenericAbility ability = AbilityOf(targeted);
                if (ability != null)
                {
                    SendTargetedAbility(member, who, ability, queued, targeted.Target,
                        targeted.TargetPos, targeted.Forward, targeted.StatusEffects);
                }
                else if (targeted.Target != null)
                {
                    SendPlainAttack(member, who, targeted.Target, queued);
                }
                return;
            }

            if (state is Attack attack)
            {
                GenericAbility ability = AbilityOf(attack);
                if (ability != null)
                    SendTargetedAbility(member, who, ability, queued, attack.Target, Vector3.zero, Vector3.zero,
                        attack.EffectsOnLaunch);
                else if (attack.Target != null)
                    SendPlainAttack(member, who, attack.Target, queued);
                return;
            }

            if (state is AI.Player.Ability queuedAbility)
            {
                if (queuedAbility.QueuedAbility != null)
                    SendNonTargetedAbility(member, who, queuedAbility.QueuedAbility, queued);
                return;
            }

            if (state is UseObject use && use.UsableObject != null)
            {
                Guid target = IdOf(use.UsableObject.gameObject);
                if (target == Guid.Empty)
                    NoteOnce("That object cannot be used by a guest yet.");
                else
                {
                    Plugin.Log.LogInfo("Order sent: " + CharacterStats.Name(member) + " use " + use.UsableObject.name);
                    Send(Header(Kind.Use, who, queued).Guid(target));
                }
                return;
            }

            if (state is Move move)
            {
                Vector3 to = move.Destination;
                Plugin.Log.LogInfo("Order sent: " + CharacterStats.Name(member) + " move to " + to.ToString("0.0") + (queued ? " (queued)" : ""));
                Send(Header(Kind.Move, who, queued).Float(to.x).Float(to.y).Float(to.z));
            }
        }

        private void SendPlainAttack(PartyMemberAI member, Guid who, GameObject target, bool queued)
        {
            Guid targetId = IdOf(target);
            if (targetId == Guid.Empty) return;
            Plugin.Log.LogInfo("Order sent: " + CharacterStats.Name(member) + " attack " + target.name);
            Send(Header(Kind.Attack, who, queued).Guid(targetId));
        }

        private void SendTargetedAbility(PartyMemberAI member, Guid who, GenericAbility ability, bool queued,
            GameObject target, Vector3 targetPos, Vector3 forward, StatusEffect[] effects)
        {
            if (!HasSupportedEffects(ability, effects))
            {
                NoteOnce("That ability has custom launch effects and cannot be ordered by a guest yet.");
                return;
            }
            if (!TryAbilityKey(member, ability, out string name, out int displayId, out int occurrence))
            {
                NoteOnce("That ability cannot be ordered by a guest yet.");
                return;
            }
            if (!IsFinite(targetPos) || !IsFinite(forward) || !WithinCoordinateBounds(targetPos)) return;

            TargetMode mode = target != null ? TargetMode.Object : TargetMode.Position;
            Guid targetId = target != null ? IdOf(target) : Guid.Empty;
            if (mode == TargetMode.Object && targetId == Guid.Empty)
            {
                NoteOnce("That ability target cannot be identified yet.");
                return;
            }

            Plugin.Log.LogInfo("Order sent: " + CharacterStats.Name(member) + " ability " + name + (queued ? " (queued)" : ""));
            Send(AbilityHeader(Kind.TargetedAbility, who, queued, name, displayId, occurrence)
                .Byte((byte)mode).Guid(targetId)
                .Float(targetPos.x).Float(targetPos.y).Float(targetPos.z)
                .Float(forward.x).Float(forward.y).Float(forward.z));
        }

        private void SendNonTargetedAbility(PartyMemberAI member, Guid who, GenericAbility ability, bool queued)
        {
            if (!TryAbilityKey(member, ability, out string name, out int displayId, out int occurrence))
            {
                NoteOnce("That ability cannot be ordered by a guest yet.");
                return;
            }
            Plugin.Log.LogInfo("Order sent: " + CharacterStats.Name(member) + " ability " + name + (queued ? " (queued)" : ""));
            Send(AbilityHeader(Kind.NonTargetedAbility, who, queued, name, displayId, occurrence));
        }

        /// <summary>Captures the property-based ability path before PartyMemberAI.Update can consume it.</summary>
        internal static void CaptureImmediateAbility(PartyMemberAI member, GenericAbility ability)
        {
            Orders self = s_instance;
            if (self == null || !Plugin.IsMirroring || member == null || ability == null || !self._ownership.IsMine(member)) return;
            Guid who = Ownership.IdOf(member);
            if (who != Guid.Empty) self.SendNonTargetedAbility(member, who, ability, false);
        }

        private static GenericAbility AbilityOf(TargetedAttack state)
        {
            if (state.Ability != null) return state.Ability;
            GenericAbility wrapped = WrapperAbility(state.StatusEffects);
            if (wrapped != null) return wrapped;
            AttackBase attack = state.AttackToUse;
            if (attack == null || !attack.HasForcedTarget && !attack.ForceTarget(out _)) return null;
            return attack.GetComponent<GenericAbility>();
        }

        private static GenericAbility AbilityOf(Attack state)
        {
            if (state.Ability != null) return state.Ability;
            GenericAbility wrapped = WrapperAbility(state.EffectsOnLaunch);
            if (wrapped != null) return wrapped;
            AttackBase attack = state.AttackToUse;
            return attack != null && attack.HasForcedTarget ? attack.GetComponent<GenericAbility>() : null;
        }

        private static GenericAbility WrapperAbility(StatusEffect[] effects)
        {
            if (effects == null) return null;
            for (int i = 0; i < effects.Length && i < 64; i++)
            {
                StatusEffect effect = effects[i];
                if (effect != null && effect.Params != null
                    && effect.Params.AffectsStat == StatusEffect.ModifiedStat.ApplyAttackEffects
                    && effect.AbilityOrigin != null)
                    return effect.AbilityOrigin;
            }
            return null;
        }

        private static bool HasSupportedEffects(GenericAbility ability, StatusEffect[] effects)
        {
            if (!ability.UsePrimaryAttack && !ability.UseFullAttack || ability.Attack == null)
                return effects == null || effects.Length == 0;
            if (effects == null || effects.Length != 1 || effects[0] == null) return false;
            StatusEffect effect = effects[0];
            return effect.AbilityOrigin == ability && effect.Params != null
                && effect.Params.AffectsStat == StatusEffect.ModifiedStat.ApplyAttackEffects
                && effect.Params.AttackPrefab == ability.Attack;
        }

        private static bool TryAbilityKey(PartyMemberAI member, GenericAbility ability,
            out string name, out int displayId, out int occurrence)
        {
            name = NormalizeAbilityName(ability.name);
            displayId = ability.DisplayName.StringID;
            occurrence = 0;
            if (name.Length == 0 || name.Length > MaxAbilityNameLength) return false;

            CharacterStats stats = member.GetComponent<CharacterStats>();
            if (stats == null) return false;
            foreach (GenericAbility candidate in stats.ActiveAbilities)
            {
                if (candidate == null || NormalizeAbilityName(candidate.name) != name
                    || candidate.DisplayName.StringID != displayId) continue;
                if (candidate == ability) return occurrence <= MaxAbilityOccurrence;
                occurrence++;
            }
            return false;
        }

        private static string NormalizeAbilityName(string name) => (name ?? "").Replace("(Clone)", "");

        /// <summary>The guest pressed stop for this character. Returns false if the game should handle it itself.</summary>
        public static bool GuestStop(GameObject character)
        {
            Orders self = s_instance;
            if (self == null || !Plugin.IsMirroring) return false;
            var member = character != null ? character.GetComponent<PartyMemberAI>() : null;
            if (member != null && self._ownership.IsMine(member))
            {
                Guid who = Ownership.IdOf(member);
                if (who != Guid.Empty) self.Send(Header(Kind.Stop, who, false));
            }
            return true;
        }

        private void NoteOnce(string text)
        {
            if (Time.unscaledTime < _nextNote) return;
            _nextNote = Time.unscaledTime + 3f;
            _session.Note(text);
        }

        private static MessageWriter Header(Kind kind, Guid who, bool queued)
        {
            int epoch = 0, currentEventId = 0;
            if (InPresentedTacticalCombat())
            {
                epoch = TacticalStateSync.CurrentEpoch;
                currentEventId = TacticalStateSync.CurrentEventId;
            }
            return new MessageWriter(MessageType.Order).Byte((byte)kind).Guid(who).Byte((byte)(queued ? 1 : 0))
                .Int(epoch).Int(currentEventId);
        }

        private static bool InPresentedTacticalCombat()
        {
            return TacticalModeManager.IsTacticalModeEnabled && TacticalStateSync.CurrentEventId != 0;
        }

        private static MessageWriter AbilityHeader(Kind kind, Guid who, bool queued,
            string name, int displayId, int occurrence)
        {
            return Header(kind, who, queued).String(name).Int(displayId).Int(occurrence);
        }

        private void Send(MessageWriter message) => _session.SendToHost(message.ToArray());

        // ---- host ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender == null || reader.Type != MessageType.Order || !_session.IsHost) return;
            var kind = (Kind)reader.Byte();
            Guid who = reader.Guid();
            bool queued = reader.Byte() != 0;
            int expectedEpoch = reader.Int();
            int expectedCurrentEventId = reader.Int();

            // Read the rest before deciding anything, so a refused order is still a well-formed one.
            Vector3 destination = default, targetPos = default, forward = default;
            Guid targetId = Guid.Empty;
            string abilityName = null;
            int displayId = 0, occurrence = 0;
            TargetMode targetMode = TargetMode.None;
            switch (kind)
            {
                case Kind.Move: destination = new Vector3(reader.Float(), reader.Float(), reader.Float()); break;
                case Kind.Attack:
                case Kind.Use: targetId = reader.Guid(); break;
                case Kind.Stop: break;
                case Kind.TargetedAbility:
                    ReadAbilityKey(reader, out abilityName, out displayId, out occurrence);
                    targetMode = (TargetMode)reader.Byte();
                    targetId = reader.Guid();
                    targetPos = new Vector3(reader.Float(), reader.Float(), reader.Float());
                    forward = new Vector3(reader.Float(), reader.Float(), reader.Float());
                    if (targetMode != TargetMode.Object && targetMode != TargetMode.Position)
                        throw new FormatException("ability target mode " + (byte)targetMode);
                    break;
                case Kind.NonTargetedAbility:
                    ReadAbilityKey(reader, out abilityName, out displayId, out occurrence);
                    break;
                default: throw new FormatException("order kind " + (byte)kind);
            }

            if (!GameState.IsInGameSession || GameState.IsLoading) return;
            PartyMemberAI member = Ownership.Find(who);
            if (member == null) return; // left the party, or the guest is a level behind
            if (_ownership.OwnerOf(member) != sender.Id)
            {
                Refuse(sender, "does not control " + CharacterStats.Name(member));
                return;
            }
            AIStateManager states = member.StateManager;
            if (states == null || !member.IsControllable) return; // dead, unconscious or not in play
            bool tactical = TacticalModeManager.IsTacticalModeEnabled
                && TacticalModeManager.IsInTacticalCombat(member);
            if (tactical)
            {
                queued = false;
                if (kind != Kind.Stop && !ValidateTacticalTurn(sender, member, expectedEpoch, expectedCurrentEventId))
                    return;
            }
            Plugin.Log.LogInfo("Order from " + sender.Name + ": " + CharacterStats.Name(member) + " " + kind + (queued ? " (queued)" : ""));

            GenericAbility ability = null;
            if (kind == Kind.TargetedAbility || kind == Kind.NonTargetedAbility)
            {
                ability = FindAbility(member, abilityName, displayId, occurrence);
                if (ability == null || ability.Owner != member.gameObject || !ability.ReadyForUI)
                {
                    Refuse(sender, "sent an unavailable ability order");
                    return;
                }
                if (tactical && !member.AvailableAction
                    && ability.ActionType != AttackBase.TacticalActionType.Free)
                {
                    Refuse(sender, "sent a standard ability without an available action");
                    return;
                }
            }

            switch (kind)
            {
                case Kind.Move:
                {
                    float moveDistance = (destination - member.transform.position).magnitude;
                    if (!IsFinite(destination) || moveDistance > MaxMoveDistance)
                    {
                        Refuse(sender, "sent a move to an impossible place");
                        return;
                    }
                    if (tactical && (member.Mover == null || float.IsNaN(member.AvailableMoveDistance)
                        || float.IsInfinity(member.AvailableMoveDistance)
                        || moveDistance > member.AvailableMoveDistance + 0.05f))
                    {
                        Refuse(sender, "sent a move beyond the remaining movement allowance");
                        return;
                    }
                    if (!queued && states.CurrentState is Move running)
                    {
                        // Already walking: redirect that move instead of stacking a second
                        // one on it. This is what Player.IssueCommands does.
                        running.Destination = destination;
                        running.Range = 0.05f;
                        running.ShowDestinationCircle = false;
                        if (member.Mover != null) member.Mover.ClearAllTurnDirections();
                        states.ClearQueuedStates();
                        if (member.Mover != null && member.Mover.enabled) running.OnEnter();
                        break;
                    }
                    Move move = AIStateManager.StatePool.Allocate<Move>();
                    Push(states, move, queued);
                    move.Destination = destination;
                    move.Range = 0.05f;
                    move.ShowDestinationCircle = false;
                    break;
                }
                case Kind.Attack:
                {
                    GameObject target = ResolveObject(targetId);
                    if (target == null || target == member.gameObject || target.GetComponent<Health>() == null) return;
                    AttackBase primaryAttack = tactical ? AIController.GetPrimaryAttack(member.gameObject) : null;
                    if (tactical && (primaryAttack == null || !member.AvailableAction && !primaryAttack.IsFreeAction))
                    {
                        Refuse(sender, "sent a standard attack without an available action");
                        return;
                    }
                    Attack attack = AIStateManager.StatePool.Allocate<Attack>();
                    Push(states, attack, queued);
                    attack.IsAutoAttack = false;
                    attack.Target = target;
                    break;
                }
                case Kind.Use:
                {
                    GameObject target = ResolveObject(targetId);
                    Usable usable = target != null ? target.GetComponent<Usable>() : null;
                    if (usable == null) return;
                    UseObject use = AIStateManager.StatePool.Allocate<UseObject>();
                    Push(states, use, queued);
                    use.UsableObject = usable;
                    break;
                }
                case Kind.Stop:
                    if (GameState.s_playerCharacter != null) GameState.s_playerCharacter.CancelAction(member.gameObject);
                    break;
                case Kind.TargetedAbility:
                    TargetedAbility(sender, member, states, ability, queued, targetMode, targetId, targetPos, forward);
                    break;
                case Kind.NonTargetedAbility:
                    if (ability.Modal && ability.Activated)
                    {
                        ability.UntriggerFromUI();
                        ability.Deactivate(member.gameObject);
                        break;
                    }
                    PrepareAbilityOrder(ability);
                    if (queued)
                    {
                        AI.Player.Ability state = AIStateManager.StatePool.Allocate<AI.Player.Ability>();
                        state.QueuedAbility = ability;
                        states.QueueState(state);
                    }
                    else
                    {
                        member.QueuedAbility = ability;
                    }
                    break;
            }
        }

        private bool ValidateTacticalTurn(PlayerInfo sender, PartyMemberAI member,
            int expectedEpoch, int expectedCurrentEventId)
        {
            if (expectedEpoch != TacticalStateSync.CurrentEpoch
                || expectedCurrentEventId != TacticalStateSync.CurrentEventId)
            {
                Refuse(sender, "sent a stale tactical order");
                return false;
            }
            TacticalModeManager manager = TacticalModeManager.Instance;
            if (manager == null || manager.WhoseTurn != member
                || !(manager.CurrentEvent is TacticalTurnEvent turn) || !turn.IsActive || turn.Actor != member)
            {
                Refuse(sender, "sent an order outside the character's turn");
                return false;
            }
            return true;
        }

        private static void ReadAbilityKey(MessageReader reader, out string name, out int displayId, out int occurrence)
        {
            name = reader.String();
            displayId = reader.Int();
            occurrence = reader.Int();
            if (name.Length == 0 || name.Length > MaxAbilityNameLength || NormalizeAbilityName(name) != name)
                throw new FormatException("invalid ability name");
            if (occurrence < 0 || occurrence > MaxAbilityOccurrence)
                throw new FormatException("ability occurrence " + occurrence);
        }

        private static GenericAbility FindAbility(PartyMemberAI member, string name, int displayId, int occurrence)
        {
            CharacterStats stats = member.GetComponent<CharacterStats>();
            if (stats == null) return null;
            int current = 0;
            foreach (GenericAbility ability in stats.ActiveAbilities)
            {
                if (ability == null || NormalizeAbilityName(ability.name) != name
                    || ability.DisplayName.StringID != displayId) continue;
                if (current++ == occurrence) return ability;
            }
            return null;
        }

        private void TargetedAbility(PlayerInfo sender, PartyMemberAI member, AIStateManager states,
            GenericAbility ability, bool queued, TargetMode targetMode, Guid targetId, Vector3 targetPos, Vector3 forward)
        {
            if (!IsFinite(targetPos) || !IsFinite(forward) || !WithinCoordinateBounds(targetPos)
                || forward.sqrMagnitude > 1.01f)
            {
                Refuse(sender, "sent impossible ability targeting");
                return;
            }
            if (!TryBuildAttack(member, ability, out AttackBase attack, out AttackBase weaponAttack,
                out StatusEffect[] effects, out bool fullAttack, out int animVariation))
            {
                Refuse(sender, "sent an ability without a usable attack");
                return;
            }

            if (attack.HasForcedTarget)
            {
                GameObject forced = attack.ForcedTarget;
                if (!ValidTarget(member, ability, attack, forced)
                    || targetMode != TargetMode.Object || ResolveObject(targetId) != forced)
                {
                    Refuse(sender, "sent an invalid forced-target ability");
                    return;
                }

                // Player.StartCasting avoids restarting the same forced attack.
                Attack current = states.CurrentState as Attack;
                AI.Achievement.Attack performing = states.CurrentState as AI.Achievement.Attack;
                if (!queued && ((current != null && current.AttackToUse == attack)
                    || (performing != null && performing.Parameters.Attack == attack))) return;

                if (!queued)
                {
                    Attack existing = states.FindState(typeof(Attack)) as Attack;
                    while (existing != null)
                    {
                        if (existing.AttackToUse != attack) break;
                        states.PopState(existing);
                        existing = states.FindState(typeof(Attack)) as Attack;
                    }
                }

                PrepareAbilityOrder(ability);
                Attack order = AIStateManager.StatePool.Allocate<Attack>();
                order.AttackToUse = attack;
                order.IsAutoAttack = false;
                order.WeaponAttack = weaponAttack;
                order.Target = forced;
                order.EffectsOnLaunch = effects;
                if (queued) states.QueueState(order);
                else states.PushState(order, TacticalModeManager.IsTacticalModeEnabled);
                return;
            }

            if (attack.ForceTarget(out GameObject automaticTarget))
            {
                if (!ValidTarget(member, ability, attack, automaticTarget))
                {
                    Refuse(sender, "sent an invalid automatic-target ability");
                    return;
                }
                PrepareAbilityOrder(ability);
                TargetedAttack order = AIStateManager.StatePool.Allocate<TargetedAttack>();
                order.AttackToUse = attack;
                order.WeaponAttack = weaponAttack;
                order.Target = automaticTarget;
                order.FullAttack = fullAttack;
                order.StatusEffects = effects;
                order.AnimVariation = animVariation;
                if (queued) states.QueueState(order);
                else states.PushState(order, TacticalModeManager.IsTacticalModeEnabled);
                return;
            }

            GameObject target = null;
            if (attack.RequiresHitObject)
            {
                target = targetMode == TargetMode.Object ? ResolveObject(targetId) : null;
                if (!ValidTarget(member, ability, attack, target))
                {
                    Refuse(sender, "sent an invalid ability target");
                    return;
                }
            }
            else if (targetMode != TargetMode.Position || targetId != Guid.Empty
                || (targetPos - member.transform.position).sqrMagnitude > MaxAbilityTargetDistance * MaxAbilityTargetDistance)
            {
                Refuse(sender, "sent an out-of-range ability target");
                return;
            }

            PrepareAbilityOrder(ability);
            TargetedAttack targeted = AIStateManager.StatePool.Allocate<TargetedAttack>();
            targeted.AttackToUse = attack;
            targeted.WeaponAttack = weaponAttack;
            targeted.FullAttack = fullAttack;
            targeted.StatusEffects = effects;
            targeted.AnimVariation = animVariation;
            targeted.Ability = ability;
            targeted.TargetPos = target != null ? target.transform.position : targetPos;
            targeted.Forward = forward;
            targeted.Target = target;
            Push(states, targeted, queued);
        }

        private static bool ValidTarget(PartyMemberAI member, GenericAbility ability, AttackBase attack, GameObject target)
        {
            return target != null && ability.IsValidTarget(target) && attack.IsValidPrimaryTarget(target)
                && member.IsTargetable(target, attack)
                && (target.transform.position - member.transform.position).sqrMagnitude
                    <= MaxAbilityTargetDistance * MaxAbilityTargetDistance;
        }

        private static void PrepareAbilityOrder(GenericAbility ability)
        {
            // GenericAbility.LaunchAttack sets this before TriggerAbility. Modal
            // abilities also keep their UI trigger set while activation is pending.
            AbilityLaunching(ability) = true;
            if (ability.Modal) ability.ForceTriggerFromUI();
            else ability.UntriggerFromUI();
        }

        /// <summary>Recreates GenericAbility.Update's primary/full-attack wrapper for UI dispatch and host orders.</summary>
        private static bool TryBuildAttack(PartyMemberAI member, GenericAbility ability,
            out AttackBase attack, out AttackBase weaponAttack, out StatusEffect[] effects,
            out bool fullAttack, out int animVariation)
        {
            effects = null;
            fullAttack = ability.UseFullAttack;
            animVariation = -1;
            weaponAttack = ability.gameObject.GetComponent<AttackBase>();

            if (ability.UsePrimaryAttack || ability.UseFullAttack)
            {
                Equipment equipment = member.GetComponent<Equipment>();
                attack = equipment != null ? equipment.PrimaryAttack : null;
                if (attack == null || ability.Owner == null) return false;
                if (ability.Attack != null)
                {
                    var parameters = new StatusEffectParams
                    {
                        AffectsStat = StatusEffect.ModifiedStat.ApplyAttackEffects,
                        AttackPrefab = ability.Attack,
                        OneHitUse = true,
                        IsHostile = false,
                    };
                    GenericAbility.AbilityType type = ability is GenericSpell
                        ? GenericAbility.AbilityType.Spell : GenericAbility.AbilityType.Ability;
                    StatusEffect wrapper = StatusEffect.Create(member.gameObject, ability, parameters, type, null, deleteOnClear: true);
                    wrapper.ApplyAttackEffectsOnlyForAttacks = new AttackBase[2] { attack, equipment.SecondaryAttack };
                    effects = new StatusEffect[1] { wrapper };
                    if (ability.Attack.UseAttackVariationOnFullOrPrimary)
                        animVariation = ability.Attack.AttackVariation;
                }
                return true;
            }

            attack = ability.Attack != null ? ability.Attack : ability.gameObject.GetComponent<AttackBase>();
            weaponAttack = null;
            fullAttack = false;
            return attack != null;
        }

        /// <summary>The same two calls Player.IssueCommands makes.</summary>
        private static void Push(AIStateManager states, AIState state, bool queued)
        {
            if (queued) states.QueueState(state);
            else states.PushState(state, true);
        }

        private void Refuse(PlayerInfo sender, string why)
        {
            _session.Note("Ignored an order: " + sender.Name + " " + why);
        }

        private static Guid IdOf(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            Guid networkId = EntityRegistry.NetworkId(obj);
            if (networkId != Guid.Empty) return networkId;
            var instance = obj.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static GameObject ResolveObject(Guid id)
        {
            if (id == Guid.Empty) return null;
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj : InstanceID.GetObjectByID(id);
        }

        private static bool WithinCoordinateBounds(Vector3 v)
        {
            return Mathf.Abs(v.x) <= MaxOrderCoordinate && Mathf.Abs(v.y) <= MaxOrderCoordinate
                && Mathf.Abs(v.z) <= MaxOrderCoordinate;
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        // ---- testing ----

        /// <summary>Sends a move order for a character whether or not it is the local player's (DevTools).</summary>
        internal void SendMoveUnchecked(PartyMemberAI member, Vector3 destination)
        {
            Send(Header(Kind.Move, Ownership.IdOf(member), false).Float(destination.x).Float(destination.y).Float(destination.z));
        }
    }
}
