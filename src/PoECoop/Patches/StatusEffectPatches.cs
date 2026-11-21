using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    internal static class StatusEffectPatchGuard
    {
        internal static bool RunOriginal => !Plugin.IsMirroring || StatusEffectSync.ApplyingHostStatus;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Update))]
    internal static class StatusEffect_Update
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.OnTimeElapsed))]
    internal static class StatusEffect_OnTimeElapsed
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.TimeJumpUpdate))]
    internal static class StatusEffect_TimeJumpUpdate
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.OnTurnEnd))]
    internal static class StatusEffect_OnTurnEnd
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.ApplyEffect), typeof(GameObject))]
    internal static class StatusEffect_ApplyEffect
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.NotifyAIApplyEffectHelper))]
    internal static class StatusEffect_NotifyAIApplyEffectHelper
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.ClearEffect), typeof(GameObject))]
    internal static class StatusEffect_ClearEffect
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.ClearEffect), typeof(GameObject), typeof(bool))]
    internal static class StatusEffect_ClearEffectWithTriggers
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Duration), MethodType.Setter)]
    internal static class StatusEffect_SetDuration
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.TemporaryDurationAdjustment), MethodType.Setter)]
    internal static class StatusEffect_SetTemporaryDurationAdjustment
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.RemoveEffect))]
    internal static class StatusEffect_RemoveEffect
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Reset))]
    internal static class StatusEffect_Reset
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.ResetTimer))]
    internal static class StatusEffect_ResetTimer
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Suspend))]
    internal static class StatusEffect_Suspend
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Unsuspend))]
    internal static class StatusEffect_Unsuspend
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Suppress))]
    internal static class StatusEffect_Suppress
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Unsuppress))]
    internal static class StatusEffect_Unsuppress
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.IncrementGeneralCounterForDestroy))]
    internal static class StatusEffect_IncrementGeneralCounterForDestroy
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.HandleClearingEffectOnResting))]
    internal static class StatusEffect_HandleClearingEffectOnResting
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.OnTrigger))]
    internal static class StatusEffect_OnTrigger
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.OffTrigger))]
    internal static class StatusEffect_OffTrigger
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.FocusBreak))]
    internal static class StatusEffect_FocusBreak
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.DeactivateAttackPrefabAbility))]
    internal static class StatusEffect_DeactivateAttackPrefabAbility
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), "Update")]
    internal static class CharacterStats_StatusUpdate
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.OnTimeElapsed))]
    internal static class CharacterStats_StatusOnTimeElapsed
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.OnTurnEnd))]
    internal static class CharacterStats_StatusOnTurnEnd
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), "HandleGameOnTimeJump")]
    internal static class CharacterStats_StatusTimeJump
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ApplyStatusEffect))]
    internal static class CharacterStats_ApplyStatusEffect
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ApplyStatusEffectImmediate))]
    internal static class CharacterStats_ApplyStatusEffectImmediate
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ApplyAffliction), typeof(Affliction))]
    internal static class CharacterStats_ApplyAffliction
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ApplyAffliction), typeof(Affliction), typeof(GameObject),
        typeof(GenericAbility.AbilityType), typeof(DamageInfo), typeof(bool), typeof(float), typeof(List<StatusEffect>))]
    internal static class CharacterStats_ApplyAfflictionFull
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearAllStatusEffects))]
    internal static class CharacterStats_ClearAllStatusEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffect), typeof(StatusEffect))]
    internal static class CharacterStats_ClearEffect
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffect), typeof(string), typeof(GameObject))]
    internal static class CharacterStats_ClearEffectByTag
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectRange))]
    internal static class CharacterStats_ClearEffectRange
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffects))]
    internal static class CharacterStats_ClearEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectsFromAfflictions))]
    internal static class CharacterStats_ClearEffectsFromAfflictions
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectFromAffliction), typeof(Affliction))]
    internal static class CharacterStats_ClearEffectFromAffliction
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectFromAffliction), typeof(string))]
    internal static class CharacterStats_ClearEffectFromAfflictionTag
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectFromAbility))]
    internal static class CharacterStats_ClearEffectFromAbility
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearRestingAffliction))]
    internal static class CharacterStats_ClearRestingAffliction
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearEffectInSlot))]
    internal static class CharacterStats_ClearEffectInSlot
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearWildstrikeEffects))]
    internal static class CharacterStats_ClearWildstrikeEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffects), new System.Type[0])]
    internal static class CharacterStats_ClearStatusEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffects), typeof(string))]
    internal static class CharacterStats_ClearStatusEffectsByTag
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffects), typeof(StatusEffect.ModifiedStat))]
    internal static class CharacterStats_ClearStatusEffectsByStat
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffects), typeof(CharacterStats.EffectType))]
    internal static class CharacterStats_ClearStatusEffectsByType
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffectsOnDeath))]
    internal static class CharacterStats_ClearStatusEffectsOnDeath
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearStatusEffectsByKeyword))]
    internal static class CharacterStats_ClearStatusEffectsByKeyword
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.AdjustStatusEffectDuration), typeof(StatusEffect), typeof(float))]
    internal static class CharacterStats_AdjustStatusEffectDuration
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.AdjustStatusEffectDuration), typeof(StatusEffect), typeof(float), typeof(bool))]
    internal static class CharacterStats_AdjustStatusEffectDurationFull
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.SuspendEffects))]
    internal static class CharacterStats_SuspendEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.UnsuspendEffects))]
    internal static class CharacterStats_UnsuspendEffects
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }

    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.ClearAllSuspensions))]
    internal static class CharacterStats_ClearAllSuspensions
    {
        private static bool Prefix() => StatusEffectPatchGuard.RunOriginal;
    }
}
