using HarmonyLib;

namespace PoECoop.Patches
{
    /// <summary>The host's CombatState snapshots replace all local combat detection.</summary>
    [HarmonyPatch(typeof(GameState), "UpdateIsInCombatAndGameOver")]
    internal static class GameState_UpdateIsInCombatAndGameOver
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Do not advance the mirrored duration between host snapshots.</summary>
    [HarmonyPatch(typeof(GameState), "OnTimeElapsed")]
    internal static class GameState_OnTimeElapsed
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(GameState), nameof(GameState.ForceIsInTrapTriggeredCombat))]
    internal static class GameState_ForceIsInTrapTriggeredCombat
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }
}
