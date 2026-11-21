using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(RestZone), nameof(RestZone.ShowCampUI))]
    internal static class RestZone_ShowCampUI_Shared
    {
        private static bool Prefix()
        {
            if (!RestSync.InSession) return true;
            RestSync.OpenCampNative();
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), "ShowRestUI")]
    internal static class RestZone_ShowRestUI_Shared
    {
        private static bool Prefix(RestZone.Mode restMode, RestMovieMode overrideMovie)
        {
            if (!RestSync.InSession || RestSync.Presenting) return true;
            if (restMode == RestZone.Mode.Scripted) RestSync.OpenScriptedNative(overrideMovie);
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), "OnConfirmCamp")]
    internal static class RestZone_OnConfirmCamp_Shared
    {
        private static bool Prefix(UIRestConfirmBox.Result result)
        {
            if (!RestSync.InSession) return true;
            RestSync.ConfirmBoxNative(result);
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), "OnConfirmDialog")]
    internal static class RestZone_OnConfirmDialog_Shared
    {
        private static bool Prefix(UIMessageBox.Result result)
        {
            if (!RestSync.InSession) return true;
            RestSync.ScriptedPromptNative(result);
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), nameof(RestZone.CampFromStash))]
    internal static class RestZone_CampFromStash_Shared
    {
        private static bool Prefix()
        {
            if (!RestSync.InSession) return true;
            RestSync.StashFinishedNative();
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), nameof(RestZone.Camp))]
    internal static class RestZone_Camp_HostOnly
    {
        private static bool Prefix()
        {
            if (!RestSync.InSession || RestSync.NativeHostRest) return true;
            RestSync.CampNative();
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), nameof(RestZone.Rest))]
    internal static class RestZone_Rest_HostOnly
    {
        private static bool Prefix(RestZone.Mode formode)
        {
            if (!RestSync.InSession || RestSync.NativeHostRest) return true;
            RestSync.RestNative(formode);
            return false;
        }
    }

    [HarmonyPatch(typeof(RestZone), nameof(RestZone.TriggerOnResting))]
    internal static class RestZone_TriggerOnResting_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(RestInteraction), nameof(RestInteraction.Use))]
    internal static class RestInteraction_Use_Shared
    {
        private static bool Prefix(RestInteraction __instance, ref bool __result)
        {
            if (!RestSync.InSession) return true;
            RestSync.OpenZoneNative(__instance);
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inn), nameof(Inn.PurchaseRoom))]
    internal static class Inn_PurchaseRoom_HostOnly
    {
        private static bool Prefix(Inn __instance, Inn.InnRoom room)
        {
            if (!RestSync.InSession || RestSync.NativeHostRest) return true;
            RestSync.PurchaseRoomNative(__instance, room);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIStoreInnRow), "OnClick")]
    internal static class UIStoreInnRow_OnClick_Shared
    {
        private static readonly AccessTools.FieldRef<UIStoreInnRow, Inn> RowInn =
            AccessTools.FieldRefAccess<UIStoreInnRow, Inn>("m_Inn");
        private static readonly AccessTools.FieldRef<UIStoreInnRow, Inn.InnRoom> RowRoom =
            AccessTools.FieldRefAccess<UIStoreInnRow, Inn.InnRoom>("m_Room");

        private static bool Prefix(UIStoreInnRow __instance)
        {
            if (!RestSync.InSession) return true;
            RestSync.OpenInnNative(RowInn(__instance), RowRoom(__instance));
            return false;
        }
    }

    [HarmonyPatch(typeof(UIStoreInnRow), "OnConfirmCamp")]
    internal static class UIStoreInnRow_OnConfirmCamp_Shared
    {
        private static bool Prefix(UIRestConfirmBox.Result result)
        {
            if (!RestSync.InSession) return true;
            RestSync.ConfirmBoxNative(result);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIStoreInnRow), "CampFromStash")]
    internal static class UIStoreInnRow_CampFromStash_Shared
    {
        private static bool Prefix()
        {
            if (!RestSync.InSession) return true;
            RestSync.StashFinishedNative();
            return false;
        }
    }

    [HarmonyPatch(typeof(UIDropdownCampBonus), "NotifySelectionChanged")]
    internal static class UIDropdownCampBonus_NotifySelectionChanged_Presentation
    {
        private static void Prefix(CharacterStats character, out int[] __state)
        {
            RestSync.PreserveSurvivalSelection(character, out __state);
        }

        private static void Postfix(CharacterStats character, int[] __state)
        {
            if (__state != null && character != null)
            {
                character.LastSelectedSurvivalBonus = __state[0];
                character.LastSelectedSurvivalSubBonus = __state[1];
            }
        }
    }

    [HarmonyPatch(typeof(UIDropdownCampBonus), "OnDropdownChanged")]
    internal static class UIDropdownCampBonus_OnDropdownChanged_Command
    {
        private static readonly AccessTools.FieldRef<UIDropdownCampBonus, CharacterStats> LoadedStats =
            AccessTools.FieldRefAccess<UIDropdownCampBonus, CharacterStats>("m_loadedStats");

        private static bool Prefix(UIDropdownCampBonus __instance, object selected)
        {
            if (!RestSync.InSession) return true;
            RestSync.SelectSurvivalNative(LoadedStats(__instance), selected);
            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.HandleGameOnResting))]
    internal static class CharacterStats_HandleGameOnResting_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(GameState), nameof(GameState.TriggerRestMode))]
    internal static class GameState_TriggerRestMode_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(WorldTime), nameof(WorldTime.AdvanceTimeByHours))]
    internal static class WorldTime_AdvanceTimeByHours_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(WorldTime), nameof(WorldTime.AdvanceTime))]
    internal static class WorldTime_AdvanceTime_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(WorldTime), nameof(WorldTime.AdvanceTimeToHour))]
    internal static class WorldTime_AdvanceTimeToHour_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Stronghold), "Update")]
    internal static class Stronghold_Update_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Stronghold), nameof(Stronghold.AdvanceOneTurn))]
    internal static class Stronghold_AdvanceOneTurn_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(Stronghold), "HandleGameOnTimeJump")]
    internal static class Stronghold_HandleGameOnTimeJump_HostOnly
    {
        private static bool Prefix() => !Plugin.IsMirroring;
    }

    [HarmonyPatch(typeof(ScriptEvent), nameof(ScriptEvent.BroadcastEvent))]
    internal static class ScriptEvent_RestFinished_HostOnly
    {
        private static bool Prefix(ScriptEvent.ScriptEvents eventType)
        {
            return !Plugin.IsMirroring || eventType != ScriptEvent.ScriptEvents.OnRestFinished;
        }
    }
}
