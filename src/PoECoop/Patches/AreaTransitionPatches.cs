using System;
using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>Guests never start a native scene change, including while another load is active.</summary>
    [HarmonyPatch(typeof(GameState), nameof(GameState.ChangeLevel), typeof(MapData))]
    internal static class GameState_ChangeLevel_Area
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    /// <summary>Guest exits become semantic requests; host exits temporarily select the whole party.</summary>
    [HarmonyPatch(typeof(SceneTransition), nameof(SceneTransition.UseTransition))]
    internal static class SceneTransition_UseTransition_Area
    {
        private static bool Prefix(SceneTransition __instance, ref IDisposable __state)
        {
            if (Plugin.IsGuest)
            {
                AreaTransitionSync.GuestUseTransition(__instance);
                __state = null;
                return false;
            }

            __state = AreaTransitionSync.BeginWholePartySelection();
            return true;
        }

        private static void Postfix(IDisposable __state) => __state?.Dispose();

        private static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();
            return __exception;
        }
    }

    /// <summary>Only SceneTransition's synchronous whole-party dispatch may override ownership selection.</summary>
    [HarmonyPatch(typeof(Ownership), nameof(Ownership.MayLocalPlayerSelect))]
    internal static class Ownership_MayLocalPlayerSelect_Area
    {
        private static bool Prefix(ref bool __result)
        {
            if (!AreaTransitionSync.AllowWholePartySelection) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>The host chooses travel time and difficulty prompts; guests only name the destination.</summary>
    [HarmonyPatch(typeof(UIWorldMapManager), nameof(UIWorldMapManager.DoTransition))]
    internal static class UIWorldMapManager_DoTransition_Area
    {
        private static bool Prefix(string mapName)
        {
            if (!Plugin.IsGuest) return true;
            AreaTransitionSync.GuestWorldMapTravel(mapName);
            return false;
        }
    }

    /// <summary>Same-map travel has no level load, so explicitly notify and refresh the mirror.</summary>
    [HarmonyPatch(typeof(PartyWaypoint), nameof(PartyWaypoint.TeleportPartyToLocation))]
    internal static class PartyWaypoint_TeleportPartyToLocation_Area
    {
        private static void Postfix(PartyWaypoint __instance)
        {
            if (Plugin.IsHost) AreaTransitionSync.HostSameMapTeleported(__instance);
        }
    }
}
