using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PoECoop.Patches
{
    internal static class TacticalGuestGuard
    {
        internal static bool Active => Plugin.IsMirroring && TacticalModeManager.IsTacticalModeEnabled;
    }

    /// <summary>
    /// The host is the only tactical scheduler. Guest snapshots replace this state
    /// directly rather than invoking native lifecycle methods.
    /// </summary>
    [HarmonyPatch]
    internal static class TacticalModeManager_BlockGuestMutations
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names =
            {
                "Update",
                nameof(TacticalModeManager.NotifyEventOrderChanged),
                nameof(TacticalModeManager.EnterCombat),
                nameof(TacticalModeManager.ExitCombat),
                nameof(TacticalModeManager.PopEvent),
                nameof(TacticalModeManager.LockTurn),
                nameof(TacticalModeManager.UnlockTurn),
                nameof(TacticalModeManager.UpdateTurnActor),
                nameof(TacticalModeManager.FinishLongCast),
                nameof(TacticalModeManager.CreateDelayedTurnPreview),
                nameof(TacticalModeManager.CleansePreviewList),
                nameof(TacticalModeManager.CreateNextTurnPreviews),
                nameof(TacticalModeManager.ResetTurnPreviewInitative),
                nameof(TacticalModeManager.ResetTurnPreviews),
            };
            foreach (string name in names)
                yield return AccessTools.Method(typeof(TacticalModeManager), name);
        }

        private static bool Prefix() => !TacticalGuestGuard.Active;
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.CreateNextTurnPreview), typeof(AIController), typeof(float))]
    internal static class TacticalModeManager_CreateNextTurnPreview_GuestGuard
    {
        private static bool Prefix(ref TacticalTurnEvent __result)
        {
            if (!TacticalGuestGuard.Active) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.CreateLongCastPreview))]
    internal static class TacticalModeManager_CreateLongCastPreview_GuestGuard
    {
        private static bool Prefix(ref TacticalCastFinishedEvent __result)
        {
            if (!TacticalGuestGuard.Active) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.RemoveLongCastPreviews))]
    internal static class TacticalModeManager_RemoveLongCastPreviews_GuestGuard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!TacticalGuestGuard.Active) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.StartLongCast))]
    internal static class TacticalModeManager_StartLongCast_GuestGuard
    {
        private static bool Prefix(ref TacticalCastFinishedEvent __result)
        {
            if (!TacticalGuestGuard.Active) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// Do not enter combat, advance AI, or consume QueuedAbility on a tactical guest.
    /// TriggerAbility and the AI state stack remain available to Orders.Collect.
    /// </summary>
    [HarmonyPatch]
    internal static class AIController_TacticalUpdate_GuestGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AIController), nameof(AIController.Update));
            yield return AccessTools.Method(typeof(PartyMemberAI), nameof(PartyMemberAI.Update));
            yield return AccessTools.Method(typeof(AIPackageController), nameof(AIPackageController.Update));
        }

        private static bool Prefix() => !TacticalGuestGuard.Active;
    }

    [HarmonyPatch(typeof(AI.Confusion), nameof(AI.Confusion.Update))]
    internal static class Confusion_Update_TacticalGuestGuard
    {
        private static bool Prefix() => !TacticalGuestGuard.Active;
    }

    [HarmonyPatch(typeof(AI.Confusion), nameof(AI.Confusion.OnTurnStart))]
    internal static class Confusion_OnTurnStart_TacticalGuestGuard
    {
        private static bool Prefix() => !TacticalGuestGuard.Active;
    }

    /// <summary>
    /// Derived Update methods run their own gameplay before or after base.Update,
    /// so each declared implementation needs its own guard.
    /// </summary>
    [HarmonyPatch]
    internal static class AttackBase_Update_TacticalGuestGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in typeof(AttackBase).Assembly.GetTypes())
            {
                if (!typeof(AttackBase).IsAssignableFrom(type)) continue;
                MethodInfo update = type.GetMethod(nameof(AttackBase.Update),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, System.Type.EmptyTypes, null);
                if (update != null) yield return update;
            }
        }

        private static bool Prefix() => !TacticalGuestGuard.Active;
    }
}
