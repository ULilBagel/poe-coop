using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PoECoop.Patches
{
    /// <summary>Guest projectiles are presentation only; the host owns flight, impacts and pulses.</summary>
    [HarmonyPatch]
    internal static class Projectile_TacticalSimulation_GuestGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Projectile), "Update");
            yield return AccessTools.Method(typeof(Projectile), nameof(Projectile.OnTurnStart));
            yield return AccessTools.Method(typeof(Projectile), nameof(Projectile.OnTimeElapsed));
            yield return AccessTools.Method(typeof(Projectile), nameof(Projectile.FirePulse));
        }

        private static bool Prefix() => !TacticalGuestGuard.Active;
    }

    /// <summary>
    /// Registration both changes the turn schedule and can fire an immediate pulse.
    /// Removal remains native so destroying a local object can still clean up stale entries.
    /// </summary>
    [HarmonyPatch(typeof(TacticalModeManager), nameof(TacticalModeManager.AddNewProjectileToOwner))]
    internal static class TacticalModeManager_AddNewProjectileToOwner_GuestGuard
    {
        private static bool Prefix() => !TacticalGuestGuard.Active;
    }
}
