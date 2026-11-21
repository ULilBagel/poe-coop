using HarmonyLib;

namespace PoECoop.Patches
{
    /// <summary>
    /// Combat and reaction events must not change the guest's frozen AI state.
    /// Order events still run so the game's own order states can be collected and
    /// sent to the host.
    /// </summary>
    [HarmonyPatch(typeof(AIController), nameof(AIController.OnEvent))]
    internal static class AIController_OnEvent
    {
        private static bool Prefix(GameEventArgs args)
        {
            if (!Plugin.IsMirroring) return true;
            switch (args.Type)
            {
                case GameEventType.Damaged:
                case GameEventType.Healed:
                case GameEventType.HitReact:
                case GameEventType.KnockedDown:
                case GameEventType.Unconscious:
                case GameEventType.Dead:
                case GameEventType.Revived:
                case GameEventType.MeleeEngaged:
                case GameEventType.RequestHelp:
                case GameEventType.Stunned:
                case GameEventType.Confused:
                case GameEventType.Killed:
                case GameEventType.Destroyed:
                case GameEventType.MeleeEngageBroken:
                case GameEventType.MeleeEngagementForceBreak:
                case GameEventType.Gibbed:
                case GameEventType.Paralyzed:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>Delayed triggers, pulses and self-destruction are host simulation.</summary>
    [HarmonyPatch(typeof(Trap), "Update")]
    internal static class Trap_Update
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Do not record victims, trap cooldowns or pulses from guest physics.</summary>
    [HarmonyPatch(typeof(Trap), nameof(Trap.OnTriggerEnter))]
    internal static class Trap_OnTriggerEnter
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Also covers activations through OCLs and PE_Collider2D.</summary>
    [HarmonyPatch(typeof(Trap), nameof(Trap.ActivateTrap))]
    internal static class Trap_ActivateTrap
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>The generic trap path applies health damage directly.</summary>
    [HarmonyPatch(typeof(TrapTriggerGeneric), "OnTriggerEnter")]
    internal static class TrapTriggerGeneric_OnTriggerEnter
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }
}
