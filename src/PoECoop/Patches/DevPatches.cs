using System;
using HarmonyLib;

namespace PoECoop.Patches
{
    /// <summary>
    /// The game holds a lock file so that only one copy runs. With
    /// --coop-multi-instance on the command line that check is skipped, so a
    /// host and a guest can run on one PC for testing. Unity has its own
    /// single-instance check as well; scripts/run-instance.sh turns that off.
    /// </summary>
    [HarmonyPatch(typeof(GameInstanceLocker), "Awake")]
    internal static class GameInstanceLocker_Awake
    {
        internal static readonly bool MultiInstance =
            Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-multi-instance") >= 0;

        private static bool Prefix() => !MultiInstance;
    }

    /// <summary>
    /// The game pauses itself every frame while its window is not focused. With two
    /// copies on one screen one of them never is, so in multi-instance mode the game
    /// is always told it has focus.
    /// </summary>
    [HarmonyPatch(typeof(GameState), "OnApplicationFocus")]
    internal static class GameState_OnApplicationFocus
    {
        private static void Prefix(ref bool status)
        {
            if (GameInstanceLocker_Awake.MultiInstance) status = true;
        }
    }
}
