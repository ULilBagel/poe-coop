using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>
    /// Guests display the host's ability state. Local updates would consume
    /// cooldowns/resources, launch autonomous attacks and advance chants, so
    /// only the dispatch for an actual local UI click runs while mirroring.
    /// </summary>
    [HarmonyPatch]
    internal static class GenericAbility_Update
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenericAbility), "Update");
            yield return AccessTools.Method(typeof(FocusTrait), "Update");
            yield return AccessTools.Method(typeof(WoundsTrait), "Update");
            yield return AccessTools.Method(typeof(ChanterTrait), "Update");
            yield return AccessTools.Method(typeof(Chant), "Update");
            yield return AccessTools.Method(typeof(Spiritshift), "Update");
        }

        private static bool Prefix(GenericAbility __instance)
        {
            if (!Plugin.IsMirroring || AbilityStateSync.IsApplyingHostState) return true;
            Orders.ProcessUiTrigger(__instance);
            return false;
        }
    }

    /// <summary>Keep the host's not-ready mask instead of recalculating it from the guest's presentation copy.</summary>
    [HarmonyPatch]
    internal static class GenericAbility_CalculateWhyNotReady
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenericAbility), "CalculateWhyNotReady");
            yield return AccessTools.Method(typeof(GenericSpell), "CalculateWhyNotReady");
            yield return AccessTools.Method(typeof(GenericCipherAbility), "CalculateWhyNotReady");
            yield return AccessTools.Method(typeof(GrimoireSlam), "CalculateWhyNotReady");
        }

        private static bool Prefix() => !Plugin.IsMirroring || AbilityStateSync.IsApplyingHostState;
    }
}
