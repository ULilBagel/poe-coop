using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    /// <summary>
    /// A player can only select characters they control. Everything the game does
    /// with orders, formations and the ability bar works on the selected set, so
    /// this one rule keeps the local UI inside the player's own characters. The
    /// setter is the only place selection is written (code-survey.md, section 1).
    /// The host still checks every order it receives.
    /// </summary>
    [HarmonyPatch(typeof(PartyMemberAI), nameof(PartyMemberAI.Selected), MethodType.Setter)]
    internal static class PartyMemberAI_set_Selected
    {
        private static bool Prefix(PartyMemberAI __instance, bool value)
        {
            return !value || Ownership.MayLocalPlayerSelect(__instance);
        }
    }

    /// <summary>On a guest, stop goes to the host instead of the local frozen copy.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CancelAction))]
    internal static class Player_CancelAction
    {
        private static bool Prefix(GameObject member) => !Orders.GuestStop(member);
    }
}
