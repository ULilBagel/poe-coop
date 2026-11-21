using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(TimeController), nameof(TimeController.UpdateTimeScale))]
    internal static class TimeController_UpdateTimeScale_Shared
    {
        private static bool Prefix() => !TimeSync.TryApplyGuestTimeScale();

        private static void Postfix() => TimeSync.OnHostTimeScaleUpdated();
    }

    [HarmonyPatch(typeof(TimeController), "Paused", MethodType.Setter)]
    internal static class TimeController_Paused_SharedGuard
    {
        private static bool Prefix(bool value)
        {
            if (Plugin.IsGuest) return TimeSync.ApplyingHostState;
            return !Plugin.IsHost || value || GameState.IsLoading
                || !TimeSync.SharedPaused || TimeSync.ApplyingHostState;
        }
    }

    [HarmonyPatch(typeof(TimeController), "TimeScale", MethodType.Setter)]
    internal static class TimeController_TimeScale_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(TimeController), "Slow", MethodType.Setter)]
    internal static class TimeController_Slow_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(TimeController), "Fast", MethodType.Setter)]
    internal static class TimeController_Fast_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(TimeController), nameof(TimeController.ToggleSlow))]
    internal static class TimeController_ToggleSlow_FlowCommand
    {
        private static bool Prefix()
        {
            if (!Plugin.IsGuest) return true;
            TimeSync.RequestSlowToggle();
            return false;
        }
    }

    [HarmonyPatch(typeof(TimeController), nameof(TimeController.ToggleFast))]
    internal static class TimeController_ToggleFast_FlowCommand
    {
        private static bool Prefix()
        {
            if (!Plugin.IsGuest) return true;
            TimeSync.RequestFastToggle();
            return false;
        }
    }

    [HarmonyPatch(typeof(TimeController), nameof(TimeController.SetNormal))]
    internal static class TimeController_SetNormal_FlowCommand
    {
        private static bool Prefix()
        {
            if (!Plugin.IsGuest) return true;
            if (TimeSync.SharedPaused)
            {
                TimeSync.RequestPause(false);
                TimeSync.RequestNormalSpeed();
            }
            else
            {
                TimeSync.RequestPause(true);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(TimeController), "Update")]
    internal static class TimeController_Update_FlowCommand
    {
        private static void Prefix()
        {
            if (!Plugin.IsGuest || GameState.IsLoading || !UIWindowManager.KeyInputAvailable) return;
            bool gamepadWindow = GameInput.UsingGamepad && UIWindowManager.Instance != null
                && UIWindowManager.Instance.IsAnyWindowOpen();
            if (gamepadWindow) return;
            if (GameInput.GetControlUp(MappedControl.RESTORE_SPEED, handle: true))
            {
                TimeSync.RequestNormalSpeed();
            }
            else if (GameInput.GetControlUp(MappedControl.GAME_SPEED_CYCLE, handle: true))
            {
                TimeController time = TimeController.Instance;
                if ((time != null && (time.Fast || time.Slow)) || GameState.InCombat)
                    TimeSync.RequestSlowToggle();
                else
                    TimeSync.RequestFastToggle();
            }
        }
    }

    [HarmonyPatch(typeof(GameInput), "Update")]
    internal static class GameInput_Update_SharedPause
    {
        private static readonly FieldInfo HandledDown = AccessTools.Field(typeof(GameInput), "s_handledDown");
        private static readonly FieldInfo HandledUp = AccessTools.Field(typeof(GameInput), "s_handledUp");
        private static readonly FieldInfo KeyMap = AccessTools.Field(typeof(GameInput), "s_keyMap");

        private static bool PauseKeyDown()
        {
            bool[] handledDown = HandledDown.GetValue(null) as bool[];
            bool[] handledUp = HandledUp.GetValue(null) as bool[];
            Dictionary<int, int> keyMap = KeyMap.GetValue(null) as Dictionary<int, int>;
            if (handledDown == null || handledUp == null || keyMap == null || GameState.Controls == null)
                return GameInput.GetControlDown(MappedControl.PAUSE, handle: true);
            bool pressedAny = false;
            foreach (KeyControl control in GameState.Controls.Controls[(int)MappedControl.PAUSE])
            {
                int index;
                if (!keyMap.TryGetValue((int)control.KeyCode, out index)) continue;
                bool wasHandled = handledDown[index];
                handledDown[index] = false;
                bool pressed = GameInput.GetControlDown(control, true);
                if (pressed) handledUp[index] = true;
                else handledDown[index] = wasHandled;
                pressedAny |= pressed;
            }
            return pressedAny;
        }

        private static void Prefix()
        {
            if (!Plugin.IsGuest || GameInput.UsingGamepad || GameState.IsLoading
                || UIConsole.Instance != null && UIConsole.Instance.Focused
                || GameInput_HandlePause_FlowCommand.RadialMenuShowing()) return;
            bool inventoryOpen = UIInventoryManager.Instance != null && UIInventoryManager.Instance.WindowActive();
            if (UIWindowManager.Instance != null && UIWindowManager.Instance.IsAnyWindowOpen() && !inventoryOpen) return;
            if (TacticalModeManager.IsTacticalModeEnabled && (inventoryOpen
                || TacticalModeManager.Instance.WhoseTurn != null
                && TacticalModeManager.Instance.WhoseTurn.IsControllablePartyMember())) return;
            if (PauseKeyDown())
            {
                TimeSync.RequestPause(!TimeSync.SharedPaused);
            }
        }
    }

    [HarmonyPatch(typeof(GameInput), "HandlePause")]
    internal static class GameInput_HandlePause_FlowCommand
    {
        private static readonly PropertyInfo RadialInstance = AccessTools.Property(
            typeof(GameInput).Assembly.GetType("InGameUIRadialMenu"), "Instance");
        private static readonly PropertyInfo RadialShowing = AccessTools.Property(
            typeof(GameInput).Assembly.GetType("InGameUIRadialMenu"), "IsShowing");

        internal static bool RadialMenuShowing()
        {
            object menu = RadialInstance != null ? RadialInstance.GetValue(null, null) : null;
            return menu != null && RadialShowing != null && (bool)RadialShowing.GetValue(menu, null);
        }

        private static bool Prefix()
        {
            if (!Plugin.IsGuest && !Plugin.IsHost) return true;
            if (!GameState.IsLoading && (UIConsole.Instance == null || !UIConsole.Instance.Focused)
                && !RadialMenuShowing()
                && (UIWindowManager.Instance == null || !UIWindowManager.Instance.IsAnyWindowOpen())
                && (!TacticalModeManager.IsTacticalModeEnabled || TacticalModeManager.Instance.WhoseTurn == null
                    || !TacticalModeManager.Instance.WhoseTurn.IsControllablePartyMember()))
            {
                if (Plugin.IsHost) TimeSync.HostPauseControl(!TimeSync.SharedPaused);
                else TimeSync.RequestPause(!TimeSync.SharedPaused);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(UIActionBarOnClick), "HandlePause")]
    internal static class UIActionBarOnClick_HandlePause_FlowCommand
    {
        private static bool Prefix()
        {
            if (!Plugin.IsGuest && !Plugin.IsHost) return true;
            if (UIConsole.Instance == null || !UIConsole.Instance.Focused)
            {
                if (Plugin.IsHost) TimeSync.HostPauseControl(!TimeSync.SharedPaused);
                else TimeSync.RequestPause(!TimeSync.SharedPaused);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(UIWindowManager), nameof(UIWindowManager.SavePausedState))]
    internal static class UIWindowManager_SavePausedState_CoopGuard
    {
        private static readonly AccessTools.FieldRef<UIWindowManager, bool?> PreviousPausedState =
            AccessTools.FieldRefAccess<UIWindowManager, bool?>("previousPausedState");

        private static bool Prefix(UIWindowManager __instance)
        {
            if (!Plugin.IsGuest && !Plugin.IsHost) return true;
            PreviousPausedState(__instance) = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIWindowManager), nameof(UIWindowManager.RestorePausedState))]
    internal static class UIWindowManager_RestorePausedState_CoopGuard
    {
        private static readonly AccessTools.FieldRef<UIWindowManager, bool?> PreviousPausedState =
            AccessTools.FieldRefAccess<UIWindowManager, bool?>("previousPausedState");

        private static bool Prefix(UIWindowManager __instance)
        {
            if (!Plugin.IsGuest && !Plugin.IsHost) return true;
            PreviousPausedState(__instance) = null;
            if (TimeController.Instance != null) TimeController.Instance.UiPaused = false;
            return false;
        }
    }

    /// <summary>The guest clock is replaced by snapshots, never advanced locally.</summary>
    [HarmonyPatch]
    internal static class WorldTime_GuestClockGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorldTime), "Update");
            yield return AccessTools.Method(typeof(WorldTime), "OnTimeElapsed");
            yield return AccessTools.Method(typeof(WorldTime), nameof(WorldTime.AdvanceTime));
            yield return AccessTools.Method(typeof(WorldTime), nameof(WorldTime.AdvanceTimeByHours));
            yield return AccessTools.Method(typeof(WorldTime), nameof(WorldTime.AdvanceTimeToHour));
        }

        private static bool Prefix() => !Plugin.IsGuest;
    }
}
