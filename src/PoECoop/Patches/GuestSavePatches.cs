using HarmonyLib;

namespace PoECoop.Patches
{
    // A guest's game is a mirror of the host's. It must not write that state into
    // the guest's own save folder: no autosave after a level load, and no Trial of
    // Iron save (which would also delete the save it replaces).

    [HarmonyPatch(typeof(GameState), nameof(GameState.Autosave))]
    internal static class GameState_Autosave
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(GameState), nameof(GameState.TrialOfIronSave))]
    internal static class GameState_TrialOfIronSave
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }
}
