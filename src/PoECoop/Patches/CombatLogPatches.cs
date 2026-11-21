using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>
    /// The game batches attacks inside FetchConsoleMessages. Only the returned,
    /// finalized list is captured; this patch never fetches or drains the console.
    /// </summary>
    [HarmonyPatch(typeof(Console), nameof(Console.FetchConsoleMessages))]
    internal static class Console_FetchConsoleMessages
    {
        private static void Postfix(List<Console.ConsoleMessage> __result)
        {
            CombatLogSync.Capture(__result);
            CombatLogSync.ProbeFetched(__result);
        }
    }

    /// <summary>
    /// A guest's combat text comes from the host. Suppress locally generated death,
    /// ability and other console lines, including lines produced while loading.
    /// </summary>
    [HarmonyPatch(typeof(Console), nameof(Console.AddMessage), typeof(Console.ConsoleMessage))]
    internal static class Console_AddMessage
    {
        private static bool Prefix() => !Plugin.IsGuest || CombatLogSync.ApplyingHostLog;
    }

    [HarmonyPatch(typeof(Console), nameof(Console.InsertMessage), typeof(Console.ConsoleMessage), typeof(int))]
    internal static class Console_InsertMessage
    {
        private static bool Prefix() => !Plugin.IsGuest || CombatLogSync.ApplyingHostLog;
    }
}
