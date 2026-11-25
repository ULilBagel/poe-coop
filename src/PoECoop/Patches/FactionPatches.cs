using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    // Hostility belongs to the host (Sync/FactionStateSync.cs). These patches do
    // two things: tell the sync that a native mutator ran, so the host sends the
    // change at once and a guest re-asserts the host's state at once; and refuse
    // the mutators a guest must never run by itself. They use Plugin.IsGuest, not
    // IsMirroring, because level-load scripts run while a guest is still loading.

    /// <summary>
    /// Every team relationship change in the game ends here: SetIsHostile,
    /// SetTeamRelationship, Faction.RelationshipToPlayer, the AI's reaction to an
    /// attack and a witnessed theft. A guest's table is written from the host's.
    /// </summary>
    [HarmonyPatch(typeof(Team), nameof(Team.SetRelationship))]
    internal static class Team_SetRelationship
    {
        private static bool Prefix()
        {
            FactionStateSync.MarkDirty();
            return !Plugin.IsGuest;
        }
    }

    /// <summary>The personal "hostile to the player" flag, set by the AI when a party member attacks.</summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.UnitHostileToPlayer), MethodType.Setter)]
    internal static class Faction_set_UnitHostileToPlayer
    {
        private static bool Prefix()
        {
            FactionStateSync.MarkDirty();
            return !Plugin.IsGuest;
        }
    }

    /// <summary>
    /// Team changes stay allowed on a guest (loading a save restores them this way);
    /// the sync puts the host's team back on its next update.
    /// </summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.CurrentTeamInstance), MethodType.Setter)]
    internal static class Faction_set_CurrentTeamInstance
    {
        private static void Postfix() => FactionStateSync.MarkDirty();
    }

    /// <summary>Start assigns the prefab's team, which can differ from the host's current one.</summary>
    [HarmonyPatch(typeof(Faction), "Start")]
    internal static class Faction_Start
    {
        private static void Postfix() => FactionStateSync.MarkDirty();
    }

    /// <summary>Turns the witness hostile and costs reputation: host only.</summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.NotifyAttackWitnessed))]
    internal static class Faction_NotifyAttackWitnessed
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    /// <summary>The murder reputation penalty: host only.</summary>
    [HarmonyPatch(typeof(Faction), "healthComponent_OnDeath")]
    internal static class Faction_healthComponent_OnDeath
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }
}
