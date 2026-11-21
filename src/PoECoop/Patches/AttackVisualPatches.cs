using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    /// <summary>Captures the action after the object-target Launch family has initialized it.</summary>
    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.Launch), typeof(GameObject), typeof(int))]
    internal static class AttackBase_Launch_Enemy_Visuals
    {
        private static void Postfix(AttackBase __instance, GameObject enemy)
        {
            GameObject owner = __instance.Owner;
            Vector3 position = enemy != null ? enemy.transform.position : owner != null ? owner.transform.position : Vector3.zero;
            AttackVisualSync.HostLaunched(__instance, enemy, position, false);
        }
    }

    /// <summary>Captures the action after the location-target Launch family has initialized it.</summary>
    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.Launch), typeof(Vector3), typeof(GameObject), typeof(int))]
    internal static class AttackBase_Launch_Location_Visuals
    {
        private static void Postfix(AttackBase __instance, Vector3 location, GameObject enemy)
        {
            AttackVisualSync.HostLaunched(__instance, enemy, location, true);
        }
    }

    /// <summary>Tracks action changes and the normal return to no desired action.</summary>
    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.Update))]
    internal static class AttackBase_Update_Visuals
    {
        private static bool Prefix() => !Plugin.IsMirroring;
        private static void Postfix(AttackBase __instance) => AttackVisualSync.HostVisualUpdate(__instance);
    }

    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.Interrupt))]
    internal static class AttackBase_Interrupt_Visuals
    {
        private static void Postfix(AttackBase __instance) => AttackVisualSync.HostInterrupted(__instance);
    }

    // Impact methods are simulation entry points, even when a particular override
    // only creates VFX. Patch every declared override so its pre/post base work is
    // skipped too. Animation events still run; they simply cannot cause combat.
    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackBase_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackBase_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class AttackBase_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackMelee), nameof(AttackMelee.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackMelee_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackMelee), nameof(AttackMelee.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackMelee_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackMelee), nameof(AttackMelee.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class AttackMelee_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackRanged), nameof(AttackRanged.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackRanged_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackRanged), nameof(AttackRanged.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackRanged_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackAOE), nameof(AttackAOE.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackAOE_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackAOE), nameof(AttackAOE.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackAOE_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackAOE), nameof(AttackAOE.OnImpactShared))]
    internal static class AttackAOE_OnImpactShared_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBeam), nameof(AttackBeam.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackBeam_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBeam), nameof(AttackBeam.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class AttackBeam_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackPulsedAOE), nameof(AttackPulsedAOE.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackPulsedAOE_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackPulsedAOE), nameof(AttackPulsedAOE.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackPulsedAOE_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackRoar), nameof(AttackRoar.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackRoar_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackRoar), nameof(AttackRoar.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class AttackRoar_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackAOEWithMarkerClear), nameof(AttackAOEWithMarkerClear.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class AttackAOEWithMarkerClear_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackAOEWithMarkerClear), nameof(AttackAOEWithMarkerClear.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class AttackAOEWithMarkerClear_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(FlagellentsPathAttack), nameof(FlagellentsPathAttack.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class FlagellentsPathAttack_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(MastersCallAttack), nameof(MastersCallAttack.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class MastersCallAttack_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Summon), nameof(Summon.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class Summon_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Summon), nameof(Summon.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class Summon_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Summon), nameof(Summon.OnImpact), typeof(GameObject), typeof(GameObject), typeof(bool))]
    internal static class Summon_OnImpact_Enemy_MainTarget
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(HazardAttack), nameof(HazardAttack.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class HazardAttack_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(HazardAttack), nameof(HazardAttack.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class HazardAttack_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(TeleportAbility), nameof(TeleportAbility.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class TeleportAbility_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(TeleportAbility), nameof(TeleportAbility.OnImpact), typeof(GameObject), typeof(GameObject))]
    internal static class TeleportAbility_OnImpact_Enemy
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AssemblyCSharp.Distraction), nameof(AssemblyCSharp.Distraction.OnImpact), typeof(GameObject), typeof(Vector3))]
    internal static class Distraction_OnImpact_Position
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    // These entry points apply ticks independently of OnImpact, so guarding only
    // impacts would still let a guest run beam or pulsed-AOE combat.
    [HarmonyPatch(typeof(AttackBeam), nameof(AttackBeam.Update))]
    internal static class AttackBeam_Update_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBeam), nameof(AttackBeam.OnTimeElapsed))]
    internal static class AttackBeam_OnTimeElapsed_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBeam), "ApplyPulse")]
    internal static class AttackBeam_ApplyPulse_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackBase), nameof(AttackBase.HandleBeam))]
    internal static class AttackBase_HandleBeam_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(AttackPulsedAOE), nameof(AttackPulsedAOE.Pulse))]
    internal static class AttackPulsedAOE_Pulse_GuestGuard
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }
}
