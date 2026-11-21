using System;
using System.Collections.Generic;
using AI.Player;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>Semantic turn commands. Only the host changes the native tactical schedule.</summary>
    public sealed class TacticalOrders : IDisposable
    {
        internal enum Kind : byte
        {
            FinishTurn = 1,
            DelayTurn = 2,
            PreviewDelay = 3,
            PreviewRecovery = 4,
            PreviewLongCast = 5,
            CancelPreviews = 6,
            Resync = 7,
        }

        private enum Result : byte
        {
            Accepted = 0,
            Stale = 1,
            NotOwner = 2,
            NotTurn = 3,
            Busy = 4,
            Delay = 5,
            Invalid = 6,
        }

        private sealed class Receipt
        {
            public Kind Kind;
            public Result Result;
        }

        private sealed class PlayerRequests
        {
            public readonly Dictionary<int, Receipt> Receipts = new Dictionary<int, Receipt>();
            public readonly Queue<int> Order = new Queue<int>();
            public PreviewKey LastPreview;
            public bool HasLastPreview;
        }

        private struct PreviewKey
        {
            public Kind Kind;
            public int Epoch;
            public int EventId;
            public Guid Actor;
            public string AbilityName;
            public int DisplayId;
            public int Occurrence;
            public int Frame;

            public bool SameCommand(PreviewKey other)
            {
                return Kind == other.Kind && Epoch == other.Epoch && EventId == other.EventId
                    && Actor == other.Actor && AbilityName == other.AbilityName
                    && DisplayId == other.DisplayId && Occurrence == other.Occurrence
                    && Frame == other.Frame;
            }
        }

        private const int MaxRememberedRequests = 256;
        private const int MaxAbilityNameLength = 128;
        private const int MaxAbilityOccurrence = 511;
        private const float MaxPreviewModifier = 10000f;

        private static TacticalOrders s_instance;
        private static int s_remoteOrderDepth;

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<int, PlayerRequests> _requests = new Dictionary<int, PlayerRequests>();
        private long _generation;
        private int _nextRequestId;
        private int _previewPlayerId = -1;
        private Guid _previewActor;
        private int _previewEpoch;
        private int _previewEventId;
        private float _nextPreviewRejectionNote;

        internal static bool ExecutingRemoteOrder => s_remoteOrderDepth > 0;

        public TacticalOrders(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            _generation = session.Generation;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerLeft += OnPlayerLeft;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerLeft -= OnPlayerLeft;
            _requests.Clear();
            if (s_instance == this) s_instance = null;
        }

        // ---- guest capture ----

        internal static void CaptureFinishTurn(AIController actor)
        {
            s_instance?.SendCapture(Kind.FinishTurn, actor, null, 0f);
        }

        internal static void CaptureDelayTurn(AIController actor)
        {
            s_instance?.SendCapture(Kind.DelayTurn, actor, null, 0f);
        }

        internal static void CaptureDelayPreview(AIController actor, float initiativeModifier)
        {
            s_instance?.SendCapture(Kind.PreviewDelay, actor, null, initiativeModifier);
        }

        internal static void CaptureRecoveryPreview(AIController actor, GenericAbility ability)
        {
            s_instance?.SendCapture(Kind.PreviewRecovery, actor, ability, 0f);
        }

        internal static void CaptureLongCastPreview(AIController actor, GenericAbility ability)
        {
            s_instance?.SendCapture(Kind.PreviewLongCast, actor, ability, 0f);
        }

        internal static void CaptureCancelPreviews(AIController actor)
        {
            s_instance?.SendCapture(Kind.CancelPreviews, actor, null, 0f);
        }

        internal static void CaptureResync()
        {
            TacticalOrders self = s_instance;
            if (self == null || !Plugin.IsMirroring) return;
            self.CheckSessionGeneration();
            self.Send(new MessageWriter(MessageType.TacticalOrder)
                .Byte((byte)Kind.Resync).Int(self.NextRequestId()).Int(0).Int(0).Guid(Guid.Empty));
        }

        private void SendCapture(Kind kind, AIController actor, GenericAbility ability, float modifier)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState || actor == null) return;
            CheckSessionGeneration();
            Guid actorId = IdOf(actor.gameObject);
            if (actorId == Guid.Empty) return;

            MessageWriter message = new MessageWriter(MessageType.TacticalOrder)
                .Byte((byte)kind).Int(NextRequestId())
                .Int(TacticalStateSync.CurrentEpoch).Int(TacticalStateSync.CurrentEventId)
                .Guid(actorId);
            switch (kind)
            {
                case Kind.PreviewDelay:
                    message.Float(modifier);
                    break;
                case Kind.PreviewRecovery:
                case Kind.PreviewLongCast:
                    if (!WriteAbilityKey(message, ability)) return;
                    break;
            }
            Send(message);
        }

        private int NextRequestId()
        {
            _nextRequestId = _nextRequestId == int.MaxValue ? 1 : _nextRequestId + 1;
            return _nextRequestId;
        }

        private void Send(MessageWriter message) => _session.SendToHost(message.ToArray());

        private static bool WriteAbilityKey(MessageWriter message, GenericAbility ability)
        {
            if (ability == null)
            {
                message.String("").Int(-1).Int(0);
                return true;
            }
            string name = NormalizeAbilityName(ability.name);
            int displayId = ability.DisplayName.StringID;
            PartyMemberAI member = ability.OwnerAI as PartyMemberAI;
            CharacterStats stats = member != null ? member.GetComponent<CharacterStats>() : null;
            if (stats == null || name.Length == 0 || name.Length > MaxAbilityNameLength) return false;
            int occurrence = 0;
            foreach (GenericAbility candidate in stats.ActiveAbilities)
            {
                if (candidate == null || NormalizeAbilityName(candidate.name) != name
                    || candidate.DisplayName.StringID != displayId) continue;
                if (candidate == ability)
                {
                    if (occurrence > MaxAbilityOccurrence) return false;
                    message.String(name).Int(displayId).Int(occurrence);
                    return true;
                }
                occurrence++;
            }
            return false;
        }

        // ---- host ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            CheckSessionGeneration();
            if (reader.Type == MessageType.OrderResult)
            {
                if (sender == null && _session.IsGuest) ReadOrderResult(reader);
                return;
            }
            if (sender == null || reader.Type != MessageType.TacticalOrder || !_session.IsHost) return;

            Kind kind = (Kind)reader.Byte();
            if (kind < Kind.FinishTurn || kind > Kind.Resync)
                throw new FormatException("tactical order kind " + (byte)kind);
            int requestId = reader.Int();
            int expectedEpoch = reader.Int();
            int expectedEventId = reader.Int();
            Guid actorId = reader.Guid();
            float modifier = 0f;
            string abilityName = null;
            int displayId = -1, occurrence = 0;
            switch (kind)
            {
                case Kind.PreviewDelay:
                    modifier = reader.Float();
                    break;
                case Kind.PreviewRecovery:
                case Kind.PreviewLongCast:
                    ReadAbilityKey(reader, out abilityName, out displayId, out occurrence);
                    break;
            }

            PlayerRequests requests = RequestsFor(sender.Id);
            if (requestId > 0 && requests.Receipts.TryGetValue(requestId, out Receipt previous))
            {
                Result result = previous.Kind == kind ? previous.Result : Result.Invalid;
                SendResult(sender, requestId, kind, result);
                TacticalStateSync.SendLatestTo(sender.Id);
                return;
            }
            if (requestId <= 0 || expectedEpoch < 0 || expectedEventId < 0)
            {
                Complete(sender, requests, requestId, kind, Result.Invalid);
                return;
            }
            if (kind == Kind.Resync)
            {
                Complete(sender, requests, requestId, kind,
                    actorId == Guid.Empty ? Result.Accepted : Result.Invalid);
                return;
            }
            if (actorId == Guid.Empty || !GameState.IsInGameSession || GameState.IsLoading
                || !TacticalModeManager.IsTacticalModeEnabled || TacticalModeManager.Instance == null)
            {
                Complete(sender, requests, requestId, kind, Result.Invalid);
                return;
            }

            PartyMemberAI member = ResolveMember(actorId);
            if (member == null)
            {
                Complete(sender, requests, requestId, kind, Result.Invalid);
                return;
            }
            if (_ownership.OwnerOf(member) != sender.Id)
            {
                Complete(sender, requests, requestId, kind, Result.NotOwner);
                return;
            }

            TacticalModeManager manager = TacticalModeManager.Instance;
            int currentEpoch = TacticalStateSync.CurrentEpoch;
            int currentEventId = TacticalStateSync.CurrentEventId;
            if (expectedEpoch != currentEpoch || expectedEventId != currentEventId)
            {
                Complete(sender, requests, requestId, kind, Result.Stale);
                return;
            }
            if (manager.WhoseTurn != member || !(manager.CurrentEvent is TacticalTurnEvent turn)
                || !turn.IsActive || turn.Actor != member)
            {
                Complete(sender, requests, requestId, kind, Result.NotTurn);
                return;
            }
            if (!member.IsControllable || !member.IsControllablePartyMember())
            {
                Complete(sender, requests, requestId, kind, Result.Invalid);
                return;
            }

            if (_previewPlayerId >= 0 && (_previewEpoch != currentEpoch || _previewEventId != currentEventId))
                _previewPlayerId = -1;

            switch (kind)
            {
                case Kind.FinishTurn:
                    if (!CanFinishTurn(member))
                        Complete(sender, requests, requestId, kind, Result.Busy);
                    else
                    {
                        ExecuteRemote(() => manager.FinishTurn(member, PassTurnStyle.UI));
                        _previewPlayerId = -1;
                        Complete(sender, requests, requestId, kind, Result.Accepted);
                    }
                    break;
                case Kind.DelayTurn:
                    if (!CanDelayTurnWithoutUi(member))
                        Complete(sender, requests, requestId, kind, Result.Delay);
                    else
                    {
                        ExecuteRemote(() => manager.DelayTurn(member, PassTurnStyle.UI));
                        _previewPlayerId = -1;
                        Complete(sender, requests, requestId, kind, Result.Accepted);
                    }
                    break;
                case Kind.PreviewDelay:
                    if (!IsFinite(modifier) || Mathf.Abs(modifier) > MaxPreviewModifier)
                        Complete(sender, requests, requestId, kind, Result.Invalid);
                    else if (!CanDelayTurnWithoutUi(member))
                        Complete(sender, requests, requestId, kind, Result.Delay);
                    else
                    {
                        if (!Coalesced(requests, kind, currentEpoch, currentEventId,
                            actorId, "", -1, 0))
                        {
                            ExecuteRemote(() =>
                            {
                                manager.RemoveLongCastPreviews();
                                manager.CreateDelayedTurnPreview(member, TacticalModeSettingsComponent.DelayTurnStep);
                            });
                            AcceptPreview(sender, requests, requestId, kind, actorId, currentEpoch, currentEventId);
                        }
                        else Complete(sender, requests, requestId, kind, Result.Accepted);
                    }
                    break;
                case Kind.PreviewRecovery:
                case Kind.PreviewLongCast:
                    AttackBase attack = FindPreviewAttack(member, abilityName, displayId, occurrence);
                    if (attack == null || abilityName.Length == 0 && kind == Kind.PreviewLongCast
                        || kind == Kind.PreviewLongCast && !attack.IsLongCast)
                        Complete(sender, requests, requestId, kind, Result.Invalid);
                    else if (!member.AvailableAction)
                        Complete(sender, requests, requestId, kind, Result.Busy);
                    else
                    {
                        bool validRecovery = true;
                        bool coalesced = Coalesced(requests, kind, currentEpoch, currentEventId,
                            actorId, abilityName, displayId, occurrence);
                        if (!coalesced)
                        {
                            ExecuteRemote(() =>
                            {
                                float recovery = attack.GetPreviewRecoveryTime();
                                if (!IsFinite(recovery) || recovery < 0f || recovery > MaxPreviewModifier)
                                {
                                    validRecovery = false;
                                    return;
                                }
                                if (kind == Kind.PreviewLongCast)
                                {
                                    manager.CreateLongCastPreview(attack, member);
                                    recovery += attack.GetActionTime() + float.Epsilon;
                                }
                                else manager.RemoveLongCastPreviews();
                                manager.CreateNextTurnPreviews(member, recovery);
                            });
                        }
                        if (!validRecovery)
                        {
                            requests.HasLastPreview = false;
                            Complete(sender, requests, requestId, kind, Result.Invalid);
                        }
                        else if (coalesced) Complete(sender, requests, requestId, kind, Result.Accepted);
                        else AcceptPreview(sender, requests, requestId, kind, actorId, currentEpoch, currentEventId);
                    }
                    break;
                case Kind.CancelPreviews:
                    if (!Coalesced(requests, kind, currentEpoch, currentEventId,
                        actorId, "", -1, 0))
                    {
                        if (_previewPlayerId == sender.Id && _previewActor == actorId
                            && _previewEpoch == currentEpoch && _previewEventId == currentEventId)
                        {
                            ExecuteRemote(() =>
                            {
                                manager.RemoveLongCastPreviews();
                                manager.ResetTurnPreviews();
                            });
                            _previewPlayerId = -1;
                        }
                        Complete(sender, requests, requestId, kind, Result.Accepted);
                    }
                    else Complete(sender, requests, requestId, kind, Result.Accepted);
                    break;
            }
        }

        private void AcceptPreview(PlayerInfo sender, PlayerRequests requests, int requestId, Kind kind,
            Guid actorId, int epoch, int eventId)
        {
            _previewPlayerId = sender.Id;
            _previewActor = actorId;
            _previewEpoch = epoch;
            _previewEventId = eventId;
            Complete(sender, requests, requestId, kind, Result.Accepted);
        }

        private static bool Coalesced(PlayerRequests requests, Kind kind, int epoch, int eventId,
            Guid actorId, string abilityName, int displayId, int occurrence)
        {
            var key = new PreviewKey
            {
                Kind = kind,
                Epoch = epoch,
                EventId = eventId,
                Actor = actorId,
                AbilityName = abilityName,
                DisplayId = displayId,
                Occurrence = occurrence,
                Frame = Time.frameCount,
            };
            if (requests.HasLastPreview && requests.LastPreview.SameCommand(key))
            {
                requests.LastPreview = key;
                return true;
            }
            requests.LastPreview = key;
            requests.HasLastPreview = true;
            return false;
        }

        internal static bool CanDelayTurnWithoutUi(AIController actor)
        {
            if (actor == null || actor.CharacterStats == null || !actor.AvailableAction
                || actor.AvailableMovement < TacticalModeSettingsComponent.MovementTimePerRound
                || actor.StateManager == null || actor.UsedFreeAction()
                || actor.CharacterStats.CurrentInitiative + TacticalModeSettingsComponent.DelayTurnStep
                    >= TacticalModeSettingsComponent.EffectTimePerRound)
                return false;
            AIState current = actor.StateManager.CurrentState;
            return current == null || !current.IsPerforming();
        }

        private static bool CanFinishTurn(PartyMemberAI member)
        {
            TacticalModeManager manager = TacticalModeManager.Instance;
            if (manager.HasLongCastEvent(member)) return true;
            AIState current = member.StateManager != null ? member.StateManager.CurrentState : null;
            return current == null || current is Move || current is AI.Player.Wait
                || current is AI.Achievement.PathToPosition;
        }

        private static void ExecuteRemote(Action action)
        {
            s_remoteOrderDepth++;
            try { action(); }
            finally { s_remoteOrderDepth--; }
        }

        private void CheckSessionGeneration()
        {
            if (_generation == _session.Generation) return;
            _generation = _session.Generation;
            _requests.Clear();
            _nextRequestId = 0;
            _previewPlayerId = -1;
            _previewActor = Guid.Empty;
            _previewEpoch = 0;
            _previewEventId = 0;
            _nextPreviewRejectionNote = 0f;
        }

        private PlayerRequests RequestsFor(int playerId)
        {
            if (!_requests.TryGetValue(playerId, out PlayerRequests requests))
            {
                requests = new PlayerRequests();
                _requests.Add(playerId, requests);
            }
            return requests;
        }

        private void Complete(PlayerInfo sender, PlayerRequests requests, int requestId, Kind kind, Result result)
        {
            if (requestId > 0)
            {
                requests.Receipts[requestId] = new Receipt { Kind = kind, Result = result };
                requests.Order.Enqueue(requestId);
                while (requests.Order.Count > MaxRememberedRequests)
                    requests.Receipts.Remove(requests.Order.Dequeue());
            }
            SendResult(sender, requestId, kind, result);
            TacticalStateSync.SendLatestTo(sender.Id);
        }

        private void SendResult(PlayerInfo sender, int requestId, Kind kind, Result result)
        {
            _session.SendToPlayer(sender.Id, new MessageWriter(MessageType.OrderResult)
                .Byte((byte)kind).Int(requestId).Byte((byte)result).ToArray());
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            _requests.Remove(player.Id);
            if (_previewPlayerId == player.Id) _previewPlayerId = -1;
        }

        // ---- guest results ----

        private void ReadOrderResult(MessageReader reader)
        {
            Kind kind = (Kind)reader.Byte();
            int requestId = reader.Int();
            Result result = (Result)reader.Byte();
            if (kind < Kind.FinishTurn || kind > Kind.Resync || result < Result.Accepted || result > Result.Invalid)
                throw new FormatException("invalid tactical order result");
            if (result == Result.Accepted) return;

            bool preview = kind == Kind.PreviewDelay || kind == Kind.PreviewRecovery
                || kind == Kind.PreviewLongCast || kind == Kind.CancelPreviews;
            if (preview && Time.unscaledTime < _nextPreviewRejectionNote) return;
            if (preview) _nextPreviewRejectionNote = Time.unscaledTime + 1f;
            _session.Note("Tactical " + CommandName(kind) + " request " + requestId
                + " rejected: " + ReasonText(result) + ".");
        }

        private static string CommandName(Kind kind)
        {
            switch (kind)
            {
                case Kind.FinishTurn: return "finish-turn";
                case Kind.DelayTurn: return "delay-turn";
                case Kind.PreviewDelay: return "delay preview";
                case Kind.PreviewRecovery: return "recovery preview";
                case Kind.PreviewLongCast: return "long-cast preview";
                case Kind.CancelPreviews: return "preview cancellation";
                case Kind.Resync: return "resync";
                default: return "command";
            }
        }

        private static string ReasonText(Result result)
        {
            switch (result)
            {
                case Result.Stale: return "the turn changed";
                case Result.NotOwner: return "you do not control that character";
                case Result.NotTurn: return "it is not that character's turn";
                case Result.Busy: return "the character is busy or has no action available";
                case Result.Delay: return "the turn cannot be delayed";
                default: return "the command is invalid";
            }
        }

        // ---- identity and attack data ----

        private static void ReadAbilityKey(MessageReader reader, out string name, out int displayId, out int occurrence)
        {
            name = reader.String();
            displayId = reader.Int();
            occurrence = reader.Int();
            if (name.Length > MaxAbilityNameLength || NormalizeAbilityName(name) != name
                || occurrence < 0 || occurrence > MaxAbilityOccurrence)
                throw new FormatException("invalid tactical preview ability");
        }

        private static AttackBase FindPreviewAttack(PartyMemberAI member, string name, int displayId, int occurrence)
        {
            if (name.Length == 0)
                return displayId == -1 && occurrence == 0 ? AIController.GetPrimaryAttack(member.gameObject) : null;
            CharacterStats stats = member.GetComponent<CharacterStats>();
            if (stats == null) return null;
            int current = 0;
            foreach (GenericAbility ability in stats.ActiveAbilities)
            {
                if (ability == null || NormalizeAbilityName(ability.name) != name
                    || ability.DisplayName.StringID != displayId) continue;
                if (current++ != occurrence) continue;
                return ability.Owner == member.gameObject ? ability.Attack : null;
            }
            return null;
        }

        private static string NormalizeAbilityName(string name) => (name ?? "").Replace("(Clone)", "");

        private static Guid IdOf(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            Guid id = EntityRegistry.NetworkId(obj);
            if (id != Guid.Empty) return id;
            InstanceID instance = obj.GetComponent<InstanceID>();
            return instance != null ? instance.Guid : Guid.Empty;
        }

        private static PartyMemberAI ResolveMember(Guid id)
        {
            GameObject obj = EntityRegistry.ResolveObject(id);
            if (obj == null) obj = InstanceID.GetObjectByID(id);
            return obj != null ? obj.GetComponent<PartyMemberAI>() : null;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
