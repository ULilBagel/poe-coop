using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(Encounter), "Spawn")]
    internal static class Encounter_Spawn
    {
        private static bool Prefix(Encounter __instance)
        {
            if (!Plugin.IsGuest || EntityRegistry.IsApplying) return true;
            if (EntityRegistry.Probing) EntityRegistry.ProbeBlocked("guest-block-encounter-spawn", __instance.gameObject);
            return false;
        }

        private static void Postfix(Encounter __instance)
        {
            if (!Plugin.IsGuest) EntityRegistry.CaptureEncounter(__instance);
        }
    }

    [HarmonyPatch(typeof(Encounter), nameof(Encounter.ForceSpawn))]
    internal static class Encounter_ForceSpawn
    {
        private static bool Prefix(Encounter __instance)
        {
            if (!Plugin.IsGuest || EntityRegistry.IsApplying) return true;
            if (EntityRegistry.Probing) EntityRegistry.ProbeBlocked("guest-block-encounter-force-spawn", __instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Encounter), nameof(Encounter.DeSpawn))]
    internal static class Encounter_DeSpawn
    {
        private static bool Prefix(Encounter __instance, ref List<GameObject> __state)
        {
            if (Plugin.IsGuest && !EntityRegistry.IsApplying)
            {
                if (EntityRegistry.Probing) EntityRegistry.ProbeBlocked("guest-block-encounter-despawn", __instance.gameObject);
                return false;
            }
            __state = __instance.GetSpawnedList();
            return true;
        }

        private static void Postfix(List<GameObject> __state)
        {
            EntityRegistry.CaptureDespawn(__state);
        }
    }

    [HarmonyPatch(typeof(Summon), "PerformSummoning")]
    internal static class Summon_PerformSummoning
    {
        private static bool Prefix(Summon __instance)
        {
            if (!Plugin.IsGuest || EntityRegistry.IsApplying) return true;
            if (EntityRegistry.Probing) EntityRegistry.ProbeBlocked("guest-block-perform-summoning", __instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Summon), "Update")]
    internal static class Summon_Update
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Summon), "SummonCreature")]
    internal static class Summon_SummonCreature
    {
        private static bool Prefix(Summon __instance, ref List<Health> __state)
        {
            if (Plugin.IsGuest && !EntityRegistry.IsApplying)
            {
                if (EntityRegistry.Probing) EntityRegistry.ProbeBlocked("guest-block-summon-creature", __instance.gameObject);
                return false;
            }
            __state = Plugin.IsGuest ? null : new List<Health>(EntityRegistry.Summons(__instance));
            return true;
        }

        private static void Postfix(Summon __instance, CharacterStats character, List<Health> __state)
        {
            if (!Plugin.IsGuest) EntityRegistry.CaptureSummon(__instance, character, __state);
        }
    }
}
