using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using OEIFormats.FlowCharts;
using OEIFormats.FlowCharts.Conversations;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>Owns the native FlowChartPlayer used to present a host snapshot on a guest.</summary>
    public sealed class DialoguePresentation : IDisposable
    {
        private static readonly AccessTools.FieldRef<UIConversationManager, FlowChartPlayer> UiActivePlayer =
            AccessTools.FieldRefAccess<UIConversationManager, FlowChartPlayer>("m_ActiveFlowChart");
        private static readonly AccessTools.FieldRef<UIConversationManager, int> UiCurrentNode =
            AccessTools.FieldRefAccess<UIConversationManager, int>("m_CurrentNodeId");
        private static readonly AccessTools.FieldRef<UIConversationManager, bool> UiHandlingFade =
            AccessTools.FieldRefAccess<UIConversationManager, bool>("m_handlingFade");
        private static readonly AccessTools.FieldRef<ScriptedInteraction, int> InteractionPortrait =
            AccessTools.FieldRefAccess<ScriptedInteraction, int>("m_currentPortrait");
        private static readonly MethodInfo EndInteractionMethod =
            AccessTools.Method(typeof(UIConversationManager), "EndInteraction");
        private static readonly MethodInfo HandleFadeOutMethod =
            AccessTools.Method(typeof(UIConversationManager), "HandleFadeOut");

        [ThreadStatic]
        private static int s_applyDepth;
        [ThreadStatic]
        private static int s_hostExecutionDepth;
        private static DialoguePresentation s_instance;

        private readonly Session _session;
        private FlowChartPlayer _player;
        private DialogueSync.Snapshot _current;
        private int _endingConversationId;
        private float _nextWarning;

        public DialoguePresentation(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            s_instance = this;
        }

        public void Dispose()
        {
            Reset(false);
            if (s_instance == this)
            {
                s_instance = null;
                s_applyDepth = 0;
            }
        }

        public static bool ApplyingHostState => s_applyDepth > 0;
        internal static bool ExecutingHostCommand => s_hostExecutionDepth > 0;
        internal static int PortraitOf(ScriptedInteraction interaction) => InteractionPortrait(interaction);

        public void Update(float now)
        {
            if (!_session.IsGuest || _player == null) return;
            if (ConversationManager.Instance != null && ConversationManager.Instance.IsConversationActive(_player)) return;
            if (_endingConversationId != 0)
            {
                _player = null;
                _endingConversationId = 0;
                return;
            }

            // A native owner can be destroyed without a network end arriving. Drop
            // the stale player; DialogueSync will retry the latest full snapshot.
            _player = null;
            if (now >= _nextWarning)
            {
                _nextWarning = now + 2f;
                Plugin.Log.LogWarning("The native guest dialogue disappeared; waiting for a host snapshot");
            }
        }

        internal bool Apply(DialogueSync.Snapshot snapshot)
        {
            if (!_session.IsGuest) return false;
            if (!snapshot.Active)
            {
                Reset(true);
                return true;
            }
            if (_endingConversationId == snapshot.ConversationId && snapshot.Ending) return true;

            GameObject owner = snapshot.OwnerId != Guid.Empty ? EntityRegistry.ResolveObject(snapshot.OwnerId) : null;
            if (snapshot.OwnerId != Guid.Empty && owner == null)
            {
                if (_current != null && _current.ConversationId != snapshot.ConversationId) Reset(false);
                return false;
            }

            s_applyDepth++;
            try
            {
                if (_player != null && (_current == null || _current.ConversationId != snapshot.ConversationId))
                    ResetNativePlayer(false);
                _current = snapshot;

                Conversation conversation = _player != null ? _player.CurrentFlowChart as Conversation : null;
                if (_player == null || conversation == null)
                {
                    _player = ConversationManager.Instance.StartConversation(snapshot.Filename,
                        snapshot.CurrentNodeId, owner, snapshot.DisplayMode, snapshot.VoState == 0);
                    conversation = _player != null ? _player.CurrentFlowChart as Conversation : null;
                    if (_player == null || conversation == null || _player.CurrentNodeID != snapshot.CurrentNodeId)
                        throw new InvalidOperationException("the native conversation could not be created at the host node");
                }
                else if (_player.CurrentNodeID != snapshot.CurrentNodeId)
                {
                    _player = conversation.MoveToNode(snapshot.CurrentNodeId, _player);
                    conversation = _player != null ? _player.CurrentFlowChart as Conversation : null;
                    if (_player == null || conversation == null || _player.CurrentNodeID != snapshot.CurrentNodeId)
                        throw new InvalidOperationException("the native conversation did not reach the host node");
                }

                ValidateNativeNodes(conversation, snapshot);
                _player.FadeFromBlackOnExit = snapshot.FadeFromBlackOnExit;
                _player.DisableVO = snapshot.VoState == 0;
                _player.Timer = snapshot.Timer;

                ScriptedInteraction interaction = owner != null ? owner.GetComponent<ScriptedInteraction>() : null;
                if (interaction != null && snapshot.DisplayMode == FlowChartPlayer.DisplayMode.Interaction)
                {
                    ScriptedInteraction.ActiveInteraction = interaction;
                    if (snapshot.Portrait >= 0) InteractionPortrait(interaction) = snapshot.Portrait;
                }
                if (UIConversationManager.Instance != null) UIConversationManager.Instance.ForceRefresh = true;

                if (snapshot.Ending && _endingConversationId != snapshot.ConversationId)
                {
                    UIConversationManager ui = UIConversationManager.Instance;
                    if (ui == null || UiActivePlayer(ui) != _player) return false;
                    _endingConversationId = snapshot.ConversationId;
                    EndInteractionMethod.Invoke(ui, null);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextWarning)
                {
                    _nextWarning = Time.unscaledTime + 2f;
                    Plugin.Log.LogWarning("Could not present host dialogue: " + ex.Message);
                }
                ResetNativePlayer(false);
                _current = snapshot;
                return false;
            }
            finally
            {
                s_applyDepth--;
            }
        }

        private static void ValidateNativeNodes(Conversation conversation, DialogueSync.Snapshot snapshot)
        {
            if (!(conversation.GetNode(snapshot.CurrentNodeId) is DialogueNode))
                throw new InvalidOperationException("the current host dialogue node is missing locally");
            foreach (DialogueSync.ResponseState response in snapshot.Responses)
                if (!(conversation.GetNode(response.NodeId) is PlayerResponseNode))
                    throw new InvalidOperationException("a host response node is missing locally: " + response.NodeId);
            if (snapshot.NextNodeId >= 0 && conversation.GetNode(snapshot.NextNodeId) == null)
                throw new InvalidOperationException("the host next node is missing locally: " + snapshot.NextNodeId);
        }

        internal bool Matches(DialogueSync.Snapshot snapshot)
        {
            return _current != null && _current.WorldEpoch == snapshot.WorldEpoch
                && _current.Sequence == snapshot.Sequence && _current.ConversationId == snapshot.ConversationId
                && _player != null && _player.CurrentNodeID == snapshot.CurrentNodeId
                && (_endingConversationId == snapshot.ConversationId
                    || ConversationManager.Instance != null && ConversationManager.Instance.IsConversationActive(_player));
        }

        internal void Reset(bool restoreFade)
        {
            if (_player == null && _current == null)
            {
                _endingConversationId = 0;
                return;
            }
            s_applyDepth++;
            try { ResetNativePlayer(restoreFade); }
            finally { s_applyDepth--; }
            _current = null;
        }

        private void ResetNativePlayer(bool restoreFade)
        {
            FlowChartPlayer old = _player;
            bool wasEnding = _endingConversationId != 0;
            _endingConversationId = 0;
            _player = null;

            if (ConversationManager.Instance != null && old != null && !old.Completed
                && ConversationManager.Instance.IsConversationActive(old))
                ConversationManager.Instance.EndConversation(old);

            UIConversationManager ui = UIConversationManager.Instance;
            if (wasEnding && ui != null)
            {
                if (FadeManager.Instance != null && HandleFadeOutMethod != null)
                    FadeManager.Instance.OnFadeEnded = (FadeManager.OnFadeEnd)Delegate.Remove(
                        FadeManager.Instance.OnFadeEnded,
                        Delegate.CreateDelegate(typeof(FadeManager.OnFadeEnd), ui, HandleFadeOutMethod));
                UiHandlingFade(ui) = false;
            }
            if (ui != null && (old == null || UiActivePlayer(ui) == old))
            {
                ui.HideConversation();
                ui.HideInteraction();
            }
            if (old != null && old.OwnerObject != null)
            {
                ScriptedInteraction interaction = old.OwnerObject.GetComponent<ScriptedInteraction>();
                if (interaction != null && ScriptedInteraction.ActiveInteraction == interaction)
                    ScriptedInteraction.ActiveInteraction = null;
            }
            if (restoreFade && wasEnding && old != null && old.FadeFromBlackOnExit && FadeManager.Instance != null)
                FadeManager.Instance.FadeFromBlack(FadeManager.FadeType.Script, 0.35f);
        }

        // ---- native UI validation and host execution ----

        internal static void ReadHostVisibility(FlowChartPlayer player, out bool visible, out bool ending)
        {
            UIConversationManager ui = UIConversationManager.Instance;
            ending = ui != null && UiActivePlayer(ui) == player && UiHandlingFade(ui);
            visible = ui != null && UiActivePlayer(ui) == player && UiCurrentNode(ui) == player.CurrentNodeID
                && ui.WindowActive();
        }

        internal static bool HostUiMatches(FlowChartPlayer player)
        {
            ReadHostVisibility(player, out bool visible, out bool ending);
            return visible && !ending;
        }

        internal static bool TryHostResponseIndex(FlowChartPlayer player, DialogueSync.Snapshot snapshot,
            int responseNode, out int index)
        {
            index = -1;
            var conversation = player.CurrentFlowChart as Conversation;
            if (conversation == null) return false;
            List<PlayerResponseNode> native = conversation.GetResponseNodes(player);
            if (native.Count != snapshot.Responses.Count) return false;
            for (int i = 0; i < native.Count; i++)
            {
                if (native[i].NodeID != snapshot.Responses[i].NodeId) return false;
                if (native[i].NodeID != responseNode) continue;
                if (!conversation.PassesConditionalsEx(native[i], player)) return false;
                index = i;
            }
            return index >= 0;
        }

        internal static void HostPlayerInput(MethodInfo method, int index)
        {
            if (method == null) throw new MissingMethodException(typeof(UIConversationManager).Name, "PlayerInput");
            s_hostExecutionDepth++;
            try { method.Invoke(UIConversationManager.Instance, new object[] { index }); }
            finally { s_hostExecutionDepth--; }
        }

        internal static void HostContinue(MethodInfo method)
        {
            if (method == null) throw new MissingMethodException(typeof(UIConversationManager).Name, "Continue");
            s_hostExecutionDepth++;
            try { method.Invoke(UIConversationManager.Instance, null); }
            finally { s_hostExecutionDepth--; }
        }

        // ---- guest native-data overrides ----

        internal static bool OverrideResponses(FlowChartPlayer player, bool qualifiedOnly,
            out List<PlayerResponseNode> responses)
        {
            responses = null;
            DialoguePresentation self = SelfFor(player);
            if (self == null) return false;
            var conversation = player.CurrentFlowChart as Conversation;
            if (conversation == null) return true;
            responses = new List<PlayerResponseNode>();
            foreach (DialogueSync.ResponseState state in self._current.Responses)
            {
                if (qualifiedOnly && !state.Qualified) continue;
                if (conversation.GetNode(state.NodeId) is PlayerResponseNode node) responses.Add(node);
            }
            return true;
        }

        internal static bool OverrideConditional(FlowChartPlayer player, FlowChartNode node, out bool qualified)
        {
            qualified = false;
            DialoguePresentation self = SelfFor(player);
            if (self == null || node == null) return false;
            foreach (DialogueSync.ResponseState response in self._current.Responses)
                if (response.NodeId == node.NodeID)
                {
                    qualified = response.Qualified;
                    return true;
                }
            return false;
        }

        internal static bool OverrideActiveText(FlowChartPlayer player, out string text)
        {
            text = null;
            DialoguePresentation self = SelfFor(player);
            if (self == null) return false;
            text = self._current.Text;
            return true;
        }

        internal static bool OverrideNodeText(FlowChartPlayer player, FlowChartNode node, out string text)
        {
            text = null;
            DialoguePresentation self = SelfFor(player);
            if (self == null || node == null) return false;
            if (node.NodeID == self._current.CurrentNodeId)
            {
                text = self._current.Text;
                return true;
            }
            foreach (DialogueSync.ResponseState response in self._current.Responses)
                if (response.NodeId == node.NodeID)
                {
                    text = response.Text;
                    return true;
                }
            return false;
        }

        internal static bool OverrideQualifier(FlowChartPlayer player, FlowChartNode node, out string qualifier)
        {
            qualifier = null;
            DialoguePresentation self = SelfFor(player);
            if (self == null || node == null) return false;
            foreach (DialogueSync.ResponseState response in self._current.Responses)
                if (response.NodeId == node.NodeID)
                {
                    qualifier = response.Qualifier;
                    return true;
                }
            return false;
        }

        internal static bool OverrideNextNode(FlowChartPlayer player, out FlowChartNode next)
        {
            next = null;
            if (DialogueSync.TryGetHostNextNode(player, out int hostNext))
            {
                next = hostNext >= 0 && player.CurrentFlowChart != null ? player.CurrentFlowChart.GetNode(hostNext) : null;
                return true;
            }
            DialoguePresentation self = SelfFor(player);
            if (self == null) return false;
            int nodeId = self._current.NextNodeId;
            next = nodeId >= 0 && player.CurrentFlowChart != null ? player.CurrentFlowChart.GetNode(nodeId) : null;
            return true;
        }

        private static DialoguePresentation SelfFor(FlowChartPlayer player)
        {
            DialoguePresentation self = s_instance;
            if (self == null || !self._session.IsGuest || self._current == null || !self._current.Active
                || self._player != player || player.CurrentNodeID != self._current.CurrentNodeId) return null;
            return self;
        }

        internal static bool CanStartNative => !Plugin.IsGuest || ApplyingHostState;
        internal static bool CanMoveNative => !Plugin.IsGuest || ApplyingHostState;
        internal static bool CanRunScripts => !Plugin.IsGuest;

        internal static bool CanEndNative(FlowChartPlayer player)
        {
            if (!Plugin.IsGuest || ApplyingHostState) return true;
            DialoguePresentation self = s_instance;
            return self != null && self._player == player && self._endingConversationId != 0;
        }
    }
}
