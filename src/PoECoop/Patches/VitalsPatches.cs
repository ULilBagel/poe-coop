using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>Effective maximums come from the latest host record, even when one is zero.</summary>
    [HarmonyPatch(typeof(Health), "get_MaxHealth")]
    internal static class Health_MaxHealth_Get
    {
        private static bool Prefix(Health __instance, ref float __result)
        {
            return !VitalsSync.TryGetMaxHealth(__instance, out __result);
        }
    }

    [HarmonyPatch(typeof(Health), "get_MaxStamina")]
    internal static class Health_MaxStamina_Get
    {
        private static bool Prefix(Health __instance, ref float __result)
        {
            return !VitalsSync.TryGetMaxStamina(__instance, out __result);
        }
    }

    /// <summary>
    /// The frozen AI cannot retry a reaction until the animator enters its tagged
    /// state, so do that after the controller has had its chance to clear it.
    /// </summary>
    [HarmonyPatch(typeof(AnimationController), "Update")]
    internal static class AnimationController_MaintainVitalsReaction
    {
        private static void Postfix(AnimationController __instance)
        {
            VitalsSync.MaintainReaction(__instance);
        }
    }

    /// <summary>Downed actors stay out of the native tactical schedule until they are revived.</summary>
    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.EnterCombat))]
    internal static class TacticalModeManager_EnterCombat_DownedGuard
    {
        private static bool Prefix(AIController character)
        {
            Health health = character != null ? character.GetComponent<Health>() : null;
            return health == null || !health.IsDeadOrUnconscious;
        }
    }

    /// <summary>No guest code may change the values owned by the host.</summary>
    [HarmonyPatch]
    internal static class Health_BlockVoidMutations
    {
        private static readonly HashSet<string> Names = new HashSet<string>
        {
            "Update",
            "OnTurnEnd",
            "Restored",
            "ApplyHealthChangeDirectly",
            "TransferStaminaFrom",
            "ApplyDamageDirectlyAsPlayer",
            "ApplyDamageDirectly",
            "AddStamina",
            "AddHealth",
            "DestroyKill",
            "OnRevive",
            "AddInjury",
            "HandleUnconscious",
            "HandleUnconsciousHelper",
            "HandleDeath",
            "HandlePartyMemberPermaDeath",
            "HandleGameOnResting",
            "HandleMaimOnCombatEnd",
            "CheckMaim",
            "CheckPartyDeath",
            "SuspendDecay",
            "SpawnGibs",
            "set_CurrentHealth",
            "set_CurrentStamina",
            "set_Dead",
            "set_MaxHealth",
            "set_MaxStamina",
            "set_ShouldGib",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(Health).GetMethods(BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].ReturnType == typeof(void) && Names.Contains(methods[i].Name)) yield return methods[i];
        }

        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Damage and stamina methods report that they applied nothing.</summary>
    [HarmonyPatch]
    internal static class Health_BlockFloatMutations
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(Health).GetMethods(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].ReturnType == typeof(float)
                    && (methods[i].Name == "DoDamage" || methods[i].Name == "ApplyStaminaChangeDirectly"))
                    yield return methods[i];
        }

        private static bool Prefix(ref float __result)
        {
            if (!Plugin.IsMirroring) return true;
            __result = 0f;
            return false;
        }
    }

    /// <summary>Existing callers still receive a valid, immediately completed coroutine.</summary>
    [HarmonyPatch]
    internal static class Health_BlockDelayedMutations
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Health), "HandleUnconsciousDelay");
            yield return AccessTools.Method(typeof(Health), "waitToRevive");
        }

        private static bool Prefix(ref IEnumerator __result)
        {
            if (!Plugin.IsMirroring) return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }
}
