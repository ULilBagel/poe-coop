using System;
using System.Collections.Generic;
using HarmonyLib;
using OEIFormats.FlowCharts;
using OEIFormats.FlowCharts.Conversations;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(ConversationManager), nameof(ConversationManager.StartConversation),
        typeof(string), typeof(int), typeof(GameObject), typeof(FlowChartPlayer.DisplayMode), typeof(bool))]
    internal static class ConversationManager_StartConversation
    {
        private static bool Prefix() => DialoguePresentation.CanStartNative;
        private static void Postfix(FlowChartPlayer __result) => DialogueSync.ConversationStarted(__result);
    }

    [HarmonyPatch(typeof(ConversationManager), nameof(ConversationManager.EndConversation))]
    internal static class ConversationManager_EndConversation
    {
        private static bool Prefix(FlowChartPlayer player) => DialoguePresentation.CanEndNative(player);
        private static void Postfix(FlowChartPlayer player) => DialogueSync.ConversationEnded(player);
    }

    [HarmonyPatch(typeof(FlowChart), nameof(FlowChart.MoveToNode))]
    internal static class FlowChart_MoveToNode
    {
        private static bool Prefix(FlowChartPlayer player, ref FlowChartPlayer __result, ref FlowChartPlayer __state)
        {
            __state = DialogueSync.BeginNativeMove(player);
            if (DialoguePresentation.CanMoveNative) return true;
            __result = player;
            return false;
        }

        private static void Postfix(FlowChartPlayer __result) => DialogueSync.ConversationMoved(__result);

        private static Exception Finalizer(Exception __exception, FlowChartPlayer __state)
        {
            DialogueSync.EndNativeMove(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.StartFlowChart))]
    internal static class Conversation_StartFlowChart
    {
        private static bool Prefix(FlowChartPlayer player, ref FlowChartPlayer __result)
        {
            if (!Plugin.IsGuest || !DialoguePresentation.ApplyingHostState) return true;
            __result = player;
            return false;
        }
    }

    /// <summary>Node enter/exit/update scripts never execute on a guest, including loads.</summary>
    [HarmonyPatch(typeof(FlowChart), "ExecuteScripts")]
    internal static class FlowChart_ExecuteScripts
    {
        private static bool Prefix() => DialoguePresentation.CanRunScripts;
    }

    [HarmonyPatch(typeof(FlowChart), nameof(FlowChart.TriggerOnExitScripts))]
    internal static class FlowChart_TriggerOnExitScripts
    {
        private static bool Prefix() => DialoguePresentation.CanRunScripts;
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.TriggerTalkEvent))]
    internal static class QuestManager_TriggerTalkEvent
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(TriggerConversation), nameof(TriggerConversation.OnTriggerEnter))]
    internal static class TriggerConversation_OnTriggerEnter
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(NPCDialogue), nameof(NPCDialogue.Use))]
    internal static class NPCDialogue_Use
    {
        private static bool Prefix(GameObject user, ref bool __result, ref GameObject __state)
        {
            if (Plugin.IsGuest)
            {
                __result = false;
                return false;
            }
            __state = DialogueSync.CaptureUse(user);
            return true;
        }

        private static Exception Finalizer(Exception __exception, GameObject __state)
        {
            DialogueSync.ReleaseUse(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ScriptedInteraction), nameof(ScriptedInteraction.Use))]
    internal static class ScriptedInteraction_Use
    {
        private static bool Prefix(GameObject user, ref bool __result, ref GameObject __state)
        {
            if (Plugin.IsGuest)
            {
                __result = false;
                return false;
            }
            __state = DialogueSync.CaptureUse(user);
            return true;
        }

        private static Exception Finalizer(Exception __exception, GameObject __state)
        {
            DialogueSync.ReleaseUse(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ScriptedInteraction), nameof(ScriptedInteraction.StartConversation))]
    internal static class ScriptedInteraction_StartConversation
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(UIConversationManager), "PlayerKeyInput")]
    internal static class UIConversationManager_PlayerKeyInput
    {
        private static bool Prefix(int number)
        {
            if (!Plugin.IsGuest) return DialogueSync.CanLocalInput;
            DialogueSync.LocalPlayerInput(number);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIConversationManager), "PlayerInput")]
    internal static class UIConversationManager_PlayerInput
    {
        private static bool Prefix(int number)
        {
            if (!Plugin.IsGuest) return DialogueSync.CanLocalInput;
            DialogueSync.LocalPlayerInput(number);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIConversationManager), "Continue")]
    internal static class UIConversationManager_Continue
    {
        private static bool Prefix()
        {
            if (!Plugin.IsGuest) return DialogueSync.CanLocalInput;
            DialogueSync.LocalContinue();
            return false;
        }
    }

    /// <summary>Also stops CheckRecreateContent from auto-advancing interaction response nodes.</summary>
    [HarmonyPatch(typeof(UIConversationManager), "MoveToNode")]
    internal static class UIConversationManager_MoveToNode
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(UIConversationManager), "End")]
    internal static class UIConversationManager_End
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.GetResponseNodes), typeof(FlowChartPlayer), typeof(bool))]
    internal static class Conversation_GetResponseNodes
    {
        private static bool Prefix(FlowChartPlayer player, bool qualifiedOnly, ref List<PlayerResponseNode> __result)
        {
            if (!DialoguePresentation.OverrideResponses(player, qualifiedOnly, out List<PlayerResponseNode> responses))
                return true;
            __result = responses;
            return false;
        }
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.PassesConditionalsEx))]
    internal static class Conversation_PassesConditionalsEx
    {
        private static bool Prefix(FlowChartPlayer player, FlowChartNode node, ref bool __result)
        {
            if (!DialoguePresentation.OverrideConditional(player, node, out bool qualified)) return true;
            __result = qualified;
            return false;
        }
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.GetActiveNodeText))]
    internal static class Conversation_GetActiveNodeText
    {
        private static bool Prefix(FlowChartPlayer player, ref string __result)
        {
            if (!DialoguePresentation.OverrideActiveText(player, out string text)) return true;
            __result = text;
            return false;
        }
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.GetNodeText))]
    internal static class Conversation_GetNodeText
    {
        private static bool Prefix(FlowChartPlayer player, FlowChartNode node, ref string __result)
        {
            if (!DialoguePresentation.OverrideNodeText(player, node, out string text)) return true;
            __result = text;
            return false;
        }
    }

    [HarmonyPatch(typeof(Conversation), nameof(Conversation.GetNodeQualifier))]
    internal static class Conversation_GetNodeQualifier
    {
        private static bool Prefix(FlowChartPlayer player, FlowChartNode node, ref string __result)
        {
            if (!DialoguePresentation.OverrideQualifier(player, node, out string qualifier)) return true;
            __result = qualifier;
            return false;
        }
    }

    /// <summary>Host reads reuse the captured random choice; guests use only the host's next node.</summary>
    [HarmonyPatch(typeof(FlowChart), nameof(FlowChart.GetNextNode), typeof(FlowChartPlayer))]
    internal static class FlowChart_GetNextNode
    {
        private static bool Prefix(FlowChartPlayer player, ref FlowChartNode __result)
        {
            if (!DialoguePresentation.OverrideNextNode(player, out FlowChartNode next)) return true;
            __result = next;
            return false;
        }
    }
}
