using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>Captures explicit guest UI pass requests without running the native turn transition locally.</summary>
    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.FinishTurn))]
    internal static class TacticalModeManager_FinishTurn_Command
    {
        private static bool Prefix(AIController character, PassTurnStyle style)
        {
            if (!Plugin.IsMirroring || style != PassTurnStyle.UI) return true;
            if (!TacticalStateSync.ApplyingHostState) TacticalOrders.CaptureFinishTurn(character);
            return false;
        }
    }

    /// <summary>Covers the AIController UI path too; its original would also consume action and movement locally.</summary>
    [HarmonyPatch(typeof(AIController), nameof(AIController.PassTurn))]
    internal static class AIController_PassTurn_Command
    {
        private static bool Prefix(AIController __instance, PassTurnStyle style)
        {
            if (!Plugin.IsMirroring || style != PassTurnStyle.UI) return true;
            if (!TacticalStateSync.ApplyingHostState) TacticalOrders.CaptureFinishTurn(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIPassTurnButton), "CanEndTurn")]
    internal static class UIPassTurnButton_CanEndTurn_GuestWaitForTurn
    {
        private static bool Prefix(AIController actor, ref bool __result)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState || actor == null
                || actor.StateManager == null || !(actor.StateManager.CurrentState is AI.Achievement.WaitForTurn))
                return true;
            TacticalModeManager manager = TacticalModeManager.Instance;
            if (manager == null || manager.WhoseTurn != actor || !actor.IsControllablePartyMember()) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.DelayTurn))]
    internal static class TacticalModeManager_DelayTurn_Command
    {
        private static bool Prefix(AIController character, PassTurnStyle style)
        {
            if (!Plugin.IsMirroring || style != PassTurnStyle.UI) return true;
            if (!TacticalStateSync.ApplyingHostState) TacticalOrders.CaptureDelayTurn(character);
            return false;
        }
    }

    /// <summary>The native predicate includes host-only window state; remote validation deliberately excludes it.</summary>
    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.GetCanDelayTurn))]
    internal static class TacticalModeManager_GetCanDelayTurn_Remote
    {
        private static bool Prefix(AIController character, ref bool __result)
        {
            if (!TacticalOrders.ExecutingRemoteOrder) return true;
            __result = TacticalOrders.CanDelayTurnWithoutUi(character);
            return false;
        }
    }

    /// <summary>Delay hover is a semantic preview request, not a guest-side schedule edit.</summary>
    [HarmonyPatch(typeof(UIDelayTurnButton), "OnHover")]
    internal static class UIDelayTurnButton_OnHover_Command
    {
        private static void Postfix(bool over)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState) return;
            TacticalModeManager manager = TacticalModeManager.Instance;
            AIController actor = manager != null ? manager.WhoseTurn : null;
            if (actor == null || !actor.IsControllablePartyMember() || !manager.GetCanDelayTurn(actor)) return;
            if (over) TacticalOrders.CaptureDelayPreview(actor, TacticalModeSettingsComponent.DelayTurnStep);
            else TacticalOrders.CaptureCancelPreviews(actor);
        }
    }

    /// <summary>Capture one recovery/long-cast command after the game's own hover guards have run.</summary>
    [HarmonyPatch(typeof(UIAbilityBarButton), nameof(UIAbilityBarButton.TriggerHover))]
    internal static class UIAbilityBarButton_TriggerHover_Command
    {
        private static void Postfix(UIAbilityBarButton __instance, bool over)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState
                || !TacticalModeManager.IsInTacticalCombat()) return;
            GenericAbility ability = __instance.TargetAbilityGenerous;
            TacticalModeManager manager = TacticalModeManager.Instance;
            AIController actor = ability != null ? ability.OwnerAI : manager != null ? manager.WhoseTurn : null;
            if (!over)
            {
                if (actor != null) TacticalOrders.CaptureCancelPreviews(actor);
                return;
            }
            if (ability == null || ability.Attack == null || manager == null
                || manager.WhoseTurn != actor || actor == null || !actor.AvailableAction) return;
            if (ability.Attack.IsLongCast) TacticalOrders.CaptureLongCastPreview(actor, ability);
            else TacticalOrders.CaptureRecoveryPreview(actor, ability);
        }
    }

    // Guest UI keeps its native hover behavior, but preview schedule edits stay on
    // the host. Snapshot application may still drive native presentation objects.
    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.CreateDelayedTurnPreview))]
    internal static class TacticalModeManager_CreateDelayedTurnPreview_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || TacticalStateSync.ApplyingHostState;
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.CreateNextTurnPreviews))]
    internal static class TacticalModeManager_CreateNextTurnPreviews_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || TacticalStateSync.ApplyingHostState;
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.CreateLongCastPreview))]
    internal static class TacticalModeManager_CreateLongCastPreview_Guest
    {
        private static bool Prefix(ref TacticalCastFinishedEvent __result)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.RemoveLongCastPreviews))]
    internal static class TacticalModeManager_RemoveLongCastPreviews_Guest
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || TacticalStateSync.ApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.ResetTurnPreviews))]
    internal static class TacticalModeManager_ResetTurnPreviews_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || TacticalStateSync.ApplyingHostState;
    }
}
