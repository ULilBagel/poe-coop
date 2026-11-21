using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    /// <summary>Captures every normal launch, including launches through the target and point overloads.</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), typeof(Vector3), typeof(Vector3))]
    internal static class Projectile_Launch_Visuals
    {
        private static bool Prefix(Projectile __instance, ref int __state)
        {
            if (Plugin.IsMirroring) return false;
            __state = ProjectileVisualSync.BeginImpactCapture(__instance);
            return true;
        }

        private static void Postfix(Projectile __instance) => ProjectileVisualSync.CaptureLaunch(__instance);

        private static void Finalizer(int __state) => ProjectileVisualSync.EndCapture(__state);
    }

    /// <summary>Captures impact removal before Unity's delayed destroy makes the object unavailable.</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.DestroyProjectile))]
    internal static class Projectile_DestroyProjectile_Visuals
    {
        private static void Postfix(Projectile __instance) => ProjectileVisualSync.CaptureRemoval(__instance);
    }

    /// <summary>Catches projectiles removed by scene changes or by code that bypasses DestroyProjectile.</summary>
    [HarmonyPatch(typeof(Projectile), "OnDestroy")]
    internal static class Projectile_OnDestroy_Visuals
    {
        private static void Prefix(Projectile __instance) => ProjectileVisualSync.CaptureDestroy(__instance);
    }

    /// <summary>Turn pulses are captured after the host has selected their exact impact effect and pose.</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.FirePulse))]
    internal static class Projectile_FirePulse_Visuals
    {
        private static bool Prefix(Projectile __instance, ref int __state)
        {
            if (Plugin.IsMirroring) return false;
            __state = ProjectileVisualSync.BeginPulseCapture(__instance);
            return true;
        }

        private static void Finalizer(int __state) => ProjectileVisualSync.EndPulseCapture(__state);
    }

    /// <summary>Captures sustained tactical VFX without ever running projectile simulation on a guest.</summary>
    [HarmonyPatch(typeof(Projectile), "Update")]
    internal static class Projectile_Update_Visuals
    {
        private static bool Prefix(Projectile __instance, ref int __state)
        {
            if (Plugin.IsMirroring) return false;
            __state = ProjectileVisualSync.BeginSustainedCapture(__instance);
            return true;
        }

        private static void Finalizer(int __state) => ProjectileVisualSync.EndCapture(__state);
    }

    [HarmonyPatch(typeof(Projectile), "HandleImpactHelper")]
    internal static class Projectile_HandleImpactHelper_Visuals
    {
        private static bool Prefix(Projectile __instance, ref int __state)
        {
            if (Plugin.IsMirroring) return false;
            __state = ProjectileVisualSync.BeginImpactCapture(__instance);
            return true;
        }

        private static void Finalizer(int __state) => ProjectileVisualSync.EndCapture(__state);
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnTimeElapsed))]
    internal static class Projectile_OnTimeElapsed_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnTriggerEnter))]
    internal static class Projectile_OnTriggerEnter_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.SetPulse))]
    internal static class Projectile_SetPulse_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Observes only the effect launch overload used by ranged and pulsed AOE impact presentation.</summary>
    [HarmonyPatch(typeof(GameUtilities), nameof(GameUtilities.LaunchEffect), typeof(GameObject), typeof(Vector3),
        typeof(Quaternion), typeof(float), typeof(Transform), typeof(GenericAbility), typeof(bool))]
    internal static class GameUtilities_LaunchEffect_ProjectileVisuals
    {
        private static void Postfix(GameObject effectBase, Vector3 position, Quaternion orientation, bool suppressAudio)
        {
            ProjectileVisualSync.CaptureEffect(effectBase, position, orientation, suppressAudio);
        }
    }
}
