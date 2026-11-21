using HarmonyLib;
using PoECoop.Sync;

namespace PoECoop.Patches
{
    /// <summary>Marks the ability whose own UI button changed its trigger state.</summary>
    [HarmonyPatch(typeof(GenericAbility), nameof(GenericAbility.TriggerFromUI))]
    internal static class GenericAbility_TriggerFromUI
    {
        private static void Postfix(GenericAbility __instance) => Orders.MarkUiTrigger(__instance);
    }

    /// <summary>
    /// An immediate ability without an attack lives in PartyMemberAI.QueuedAbility
    /// until the next update. Capture it synchronously so the guest cannot consume
    /// it before Orders.Collect sees it.
    /// </summary>
    [HarmonyPatch(typeof(PartyMemberAI), nameof(PartyMemberAI.TriggerAbility))]
    internal static class PartyMemberAI_TriggerAbility
    {
        private static void Postfix(PartyMemberAI __instance, GameEventArgs args)
        {
            if (!Plugin.IsMirroring || GameInput.GetControl(MappedControl.QUEUE)) return;
            if (args == null || args.GameObjectData == null || args.GameObjectData.Length == 0
                || args.GameObjectData[0] == null || __instance.QueuedAbility == null) return;

            GenericAbility ability = args.GameObjectData[0].GetComponent<GenericAbility>();
            AttackBase attack = ability != null
                ? ability.gameObject.GetComponent<AttackBase>()
                : args.GameObjectData[0].GetComponent<AttackBase>();
            if (attack != null || (ability != null && __instance.QueuedAbility != ability)) return;

            Orders.CaptureImmediateAbility(__instance, __instance.QueuedAbility);
            __instance.QueuedAbility = null;
        }
    }
}
