using System;
using HarmonyLib;
using PoECoop.UI;

namespace PoECoop.Patches
{
    /// <summary>
    /// Player.IssueCommands turns clicks into move and attack orders (see
    /// notes/code-survey.md, section 2). Skipped while the mouse is over the
    /// co-op window, so clicking a button there does not also move the party.
    /// On a guest it runs as normal: the orders it creates are picked up and sent
    /// to the host by Sync/Orders.cs.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class Player_Update_TacticalProbe
    {
        private static readonly AccessTools.FieldRef<Player, bool> IsCasting =
            AccessTools.FieldRefAccess<Player, bool>("m_isCasting");
        private static readonly AccessTools.FieldRef<Player, PartyMemberAI> CastMember =
            AccessTools.FieldRefAccess<Player, PartyMemberAI>("m_castPartyMemberAI");

        private static void Prefix(Player __instance)
        {
            if (!TacticalMoveProbe.Enabled(__instance)) return;
            PartyMemberAI castMember = CastMember(__instance);
            Plugin.Log.LogInfo("TEST tactical Player.Update MOVE isCasting=" + IsCasting(__instance)
                + " castMember=" + (castMember != null ? castMember.name : "<null>")
                + " castSelected=" + (castMember != null && castMember.Selected)
                + " forceAttack=" + __instance.IsInForceAttackMode);
        }
    }

    [HarmonyPatch(typeof(Player), "IssueCommands")]
    internal static class Player_IssueCommands
    {
        private static readonly AccessTools.FieldRef<Player, bool> TrySelect =
            AccessTools.FieldRefAccess<Player, bool>("m_trySelect");
        private static readonly AccessTools.FieldRef<Player, bool> IsSelecting =
            AccessTools.FieldRefAccess<Player, bool>("m_isSelecting");

        private static bool Prefix(Player __instance)
        {
            if (TacticalMoveProbe.Enabled(__instance)) Log(__instance, "prefix");
            return !Overlay.MouseOver;
        }

        private static void Postfix(Player __instance)
        {
            if (TacticalMoveProbe.Enabled(__instance)) Log(__instance, "postfix");
        }

        private static void Log(Player player, string phase)
        {
            Plugin.Log.LogInfo("TEST tactical IssueCommands " + phase
                + " trySelect=" + TrySelect(player) + " isSelecting=" + IsSelecting(player)
                + " cursor=" + GameCursor.DesiredCursor
                + " selectedCount=" + PartyMemberAI.GetSelectedPartyMembers().Count);
            foreach (UnityEngine.GameObject selected in PartyMemberAI.GetSelectedPartyMembers())
            {
                PartyMemberAI member = selected.GetComponent<PartyMemberAI>();
                Plugin.Log.LogInfo("TEST tactical IssueCommands " + phase + " selected=" + member.name
                    + " isWhoseTurn=" + (TacticalModeManager.Instance.WhoseTurn == member)
                    + " currentEvent=" + (TacticalModeManager.Instance.CurrentEvent != null
                        ? TacticalModeManager.Instance.CurrentEvent.GetType().Name : "<null>")
                    + " state=" + (member.StateManager.CurrentState != null
                        ? member.StateManager.CurrentState.GetType().FullName : "<null>")
                    + " movement=" + member.AvailableMovement
                    + " desiredSpeed=" + (member.Mover != null ? member.Mover.DesiredSpeed : -1f)
                    + " moveDistance=" + member.AvailableMoveDistance
                    + " moverEnabled=" + (member.Mover != null && member.Mover.isActiveAndEnabled));
            }
        }
    }

    internal static class TacticalMoveProbe
    {
        internal static bool Enabled(Player player)
        {
            return player == GameState.s_playerCharacter && Plugin.IsMirroring
                && TacticalModeManager.IsInTacticalCombat() && GameInput.GetControlUp(MappedControl.MOVE)
                && Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-probe") >= 0;
        }
    }
}
