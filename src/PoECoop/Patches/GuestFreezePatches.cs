using Assets.Code.SDK.Toolbox;
using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    // A guest inside a level runs none of the game's simulation. Each patch here
    // skips one entry point while Plugin.IsMirroring is true. The reasoning for
    // each is in notes/code-survey.md, section 8.

    /// <summary>All AI thinking, for party members and everyone else.</summary>
    [HarmonyPatch(typeof(AIStateManager), nameof(AIStateManager.Update))]
    internal static class AIStateManager_Update
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Shared creatures must not copy health, stats, abilities or effects locally.</summary>
    [HarmonyPatch(typeof(SharedStats), "Update")]
    internal static class SharedStats_Update
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Every data-driven script action: area scripts, quest and variable changes.</summary>
    [HarmonyPatch(typeof(ScriptEvent), nameof(ScriptEvent.RunScriptHelper))]
    internal static class ScriptEvent_RunScriptHelper
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Trigger volumes, including their saved trigger counts.</summary>
    [HarmonyPatch(typeof(Trigger), nameof(Trigger.NotifyTriggerEnter))]
    internal static class Trigger_NotifyTriggerEnter
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Trigger), nameof(Trigger.NotifyTriggerExit))]
    internal static class Trigger_NotifyTriggerExit
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>Cutscenes run C# puppet scripts outside the normal script dispatcher.</summary>
    [HarmonyPatch(typeof(Cutscene), nameof(Cutscene.StartCutscene))]
    internal static class Cutscene_StartCutscene
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>RealtimeCutscene overrides StartCutscene without calling the base implementation.</summary>
    [HarmonyPatch(typeof(RealtimeCutscene), nameof(RealtimeCutscene.StartCutscene))]
    internal static class RealtimeCutscene_StartCutscene
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    /// <summary>
    /// Character movement. On a guest the character is placed from the host's
    /// positions instead of walking by itself.
    /// </summary>
    [HarmonyPatch(typeof(Mover), "Update")]
    internal static class Mover_Update
    {
        private static bool Prefix(Mover __instance)
        {
            if (!Plugin.IsMirroring) return true;
            PositionSync.Drive(__instance);
            return false;
        }
    }
}
