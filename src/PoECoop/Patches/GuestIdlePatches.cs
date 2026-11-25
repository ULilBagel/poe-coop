using HarmonyLib;
using UnityEngine;

namespace PoECoop.Patches
{
    // Native costs that a session member pays for nothing, and native throttling
    // that makes one player's window state everybody's latency. The simulation
    // entry points a guest skips are in GuestFreezePatches.cs.

    /// <summary>
    /// Keeps a session member running at a steady rate while its window is in the
    /// background. A hidden window with vertical sync on presents about once a
    /// second (notes/code-survey.md, first hand test), which delays every snapshot
    /// a host sends and every one a guest applies. The player's own settings come
    /// back with focus and when the session ends.
    ///
    /// The game also pauses on every frame while it is fullscreen and unfocused.
    /// On a host that is a shared pause nobody else can lift, so in a session the
    /// game pauses once when focus is lost and can then be resumed by any player.
    /// </summary>
    [HarmonyPatch(typeof(GameState), "Update")]
    internal static class GameState_Update_SessionFocus
    {
        private const int BackgroundFrameRate = 60;

        private static bool s_inSession;
        private static bool s_savedRunInBackground;
        private static bool s_paced;
        private static int s_savedVSync;
        private static int s_savedFrameRate;
        private static bool s_pausedForFocusLoss;

        private static void Prefix(out bool __state)
        {
            __state = false;
            if (!Plugin.IsHost && !Plugin.IsGuest)
            {
                if (s_inSession) LeaveSession();
                return;
            }
            if (!s_inSession)
            {
                s_inSession = true;
                s_savedRunInBackground = Application.runInBackground;
            }
            if (!Application.runInBackground) Application.runInBackground = true;

            // Multi-instance testing has its own policy: DevTools.KeepRunningWhenHidden
            // and GameState_OnApplicationFocus in DevPatches.cs.
            if (GameInstanceLocker_Awake.MultiInstance) return;

            if (Application.isFocused)
            {
                if (s_paced) RestorePacing();
            }
            else
            {
                if (!s_paced)
                {
                    s_paced = true;
                    s_savedVSync = QualitySettings.vSyncCount;
                    s_savedFrameRate = Application.targetFrameRate;
                    Plugin.Log.LogInfo("Window in the background during a session: vertical sync off, frame rate capped");
                }
                int cap = s_savedFrameRate > 0 && s_savedFrameRate < BackgroundFrameRate
                    ? s_savedFrameRate : BackgroundFrameRate;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != cap) Application.targetFrameRate = cap;
                PerfStats.Count("guestidle.background_frames");
            }

            if (GameState.ApplicationIsFocused)
            {
                s_pausedForFocusLoss = false;
            }
            else if (!s_pausedForFocusLoss)
            {
                s_pausedForFocusLoss = true;
            }
            else
            {
                // Only this method's own focus check sees the game as focused.
                GameState.ApplicationIsFocused = true;
                __state = true;
                PerfStats.Count("guestidle.focus_pause_suppressed");
            }
        }

        private static void Finalizer(bool __state)
        {
            if (__state) GameState.ApplicationIsFocused = false;
        }

        private static void RestorePacing()
        {
            s_paced = false;
            QualitySettings.vSyncCount = s_savedVSync;
            Application.targetFrameRate = s_savedFrameRate;
            Plugin.Log.LogInfo("Frame pacing restored: vertical sync " + s_savedVSync + ", frame rate cap " + s_savedFrameRate);
        }

        private static void LeaveSession()
        {
            s_inSession = false;
            s_pausedForFocusLoss = false;
            if (s_paced) RestorePacing();
            if (!s_savedRunInBackground) Application.runInBackground = false;
        }
    }

    /// <summary>
    /// Each character that can notice stealth collects stealthed targets on every
    /// frame: a new list and a pass over every active faction. While nobody is in
    /// stealth that pass finds nothing and has no other effect, so a guest skips it.
    /// </summary>
    [HarmonyPatch(typeof(AIController), "HandleSuspicionDetection")]
    internal static class AIController_HandleSuspicionDetection_GuestIdle
    {
        private static bool Prefix()
        {
            if (!Plugin.IsMirroring || Stealth.AnyStealthInStealthMode()) return true;
            if (PerfStats.Enabled) PerfStats.Count("guestidle.skipped");
            return false;
        }
    }
}
