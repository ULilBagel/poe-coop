using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    // The guest halves of these methods are blocked in LootPatches.cs. These only tell the
    // host's ground loot capture that a native drop or removal just ran, so guests do not
    // wait for the next poll.

    [HarmonyPatch(typeof(Loot), nameof(Loot.CreateLootBag))]
    internal static class Loot_CreateLootBag_GroundLoot
    {
        private static void Postfix()
        {
            if (Plugin.IsHost) GroundLootSync.NoteHostChange();
        }
    }

    [HarmonyPatch(typeof(Loot), "CreateLootOnBody")]
    internal static class Loot_CreateLootOnBody_GroundLoot
    {
        private static void Postfix()
        {
            if (Plugin.IsHost) GroundLootSync.NoteHostChange();
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.CloseInventoryCB))]
    internal static class Container_CloseInventoryCB_GroundLoot
    {
        private static void Postfix()
        {
            if (Plugin.IsHost) GroundLootSync.NoteHostChange();
        }
    }
}
