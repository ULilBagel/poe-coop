using System;
using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(UIInventoryManager), nameof(UIInventoryManager.BeginDrag))]
    internal static class UIInventoryManager_BeginDrag_Intent
    {
        private static bool Prefix(UIInventoryGridItem item)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureBeginDrag(item, -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIGlobalInventory), nameof(UIGlobalInventory.BeginDrag))]
    internal static class UIGlobalInventory_BeginDrag_Intent
    {
        private static bool Prefix(UIInventoryGridItem item, int qty)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureBeginDrag(item, qty);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "OnClick", typeof(GameObject))]
    internal static class UIInventoryGridItem_OnClick_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            UIInventoryGridItem source = InventoryCommands.PendingSource();
            if (!InventoryCommands.PendingDragActive || source == __instance) return true;
            InventoryCommands.CaptureDrop(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "OnDrop", typeof(GameObject), typeof(GameObject))]
    internal static class UIInventoryGridItem_OnDrop_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (!InventoryCommands.PendingDragActive) return true;
            InventoryCommands.CaptureDrop(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.SendQuantity))]
    internal static class UIInventoryGridItem_SendQuantity_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance, int qty, BaseInventory target)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureQuantity(__instance, qty, target);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "TrySwap", typeof(UIInventoryGridItem), typeof(bool))]
    internal static class UIInventoryGridItem_TrySwap_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance, UIInventoryGridItem other, bool splitStack, ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (splitStack && __instance.InvItem == null && other != null && other.InvItem != null && other.InvItem.stackSize > 1)
            {
                UIMessageBox dialog = UIInventoryGridItem.ShowSplitDialog(other.InvItem);
                if (dialog != null)
                    dialog.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Combine(dialog.OnDialogEnd,
                        (UIMessageBox.OnEndDialog)((result, owner) =>
                        {
                            if (result == UIMessageBox.Result.AFFIRMATIVE)
                                InventoryCommands.CaptureSwap(__instance, other, owner.NumericValue);
                        }));
            }
            else InventoryCommands.CaptureSwap(__instance, other, other != null && other.InvItem != null ? other.InvItem.stackSize : 0);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "Swap", typeof(UIInventoryGridItem), typeof(int))]
    internal static class UIInventoryGridItem_Swap_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance, UIInventoryGridItem other, int quantity)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureSwap(__instance, other, quantity);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "TryDropItem", typeof(bool))]
    internal static class UIInventoryGridItem_TryDropItem_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance, bool splitStack, ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            UIInventoryGridItem source = InventoryCommands.PendingSource();
            if (splitStack && __instance.InvItem == null && source != null && source.InvItem != null && source.InvItem.stackSize > 1)
            {
                UIMessageBox dialog = UIInventoryGridItem.ShowSplitDialog(source.InvItem);
                if (dialog != null)
                    dialog.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Combine(dialog.OnDialogEnd,
                        (UIMessageBox.OnEndDialog)((result, owner) =>
                        {
                            if (result == UIMessageBox.Result.AFFIRMATIVE)
                                InventoryCommands.CaptureDrop(__instance, owner.NumericValue);
                        }));
            }
            else InventoryCommands.CaptureDrop(__instance);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.TryPutIn))]
    internal static class UIInventoryGridItem_TryPutIn_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance, UIInventoryItemZone to, ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (to != null && to.Inventory != null && __instance.InvItem != null)
                InventoryCommands.CaptureQuantity(__instance, __instance.InvItem.stackSize, to.Inventory);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryItemReciever), "DoPut", new Type[0])]
    internal static class UIInventoryItemReciever_DoPut_Intent
    {
        private static bool Prefix(UIInventoryItemReciever __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureReceiver(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIGlobalInventory), nameof(UIGlobalInventory.FinishMultidragAt), typeof(BaseInventory), typeof(Func<UIInventoryGridItem, bool>))]
    internal static class UIGlobalInventory_FinishMultidragAtInventory_Intent
    {
        private static readonly AccessTools.FieldRef<UIGlobalInventory, List<UIInventoryGridItem>> Selected =
            AccessTools.FieldRefAccess<UIGlobalInventory, List<UIInventoryGridItem>>("m_SelectedSlots");

        private static bool Prefix(UIGlobalInventory __instance, BaseInventory inventory, Func<UIInventoryGridItem, bool> valid)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            foreach (UIInventoryGridItem item in Selected(__instance))
                if (item != null && item.InvItem != null && (valid == null || valid(item)))
                    InventoryCommands.CaptureQuantity(item, item.InvItem.stackSize, inventory);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.Activate))]
    internal static class UIInventoryGridItem_Activate_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState || __instance.InvItem == null) return true;
            if (__instance.Owner != null && (__instance.Owner.IsExternalContainer || __instance.Owner.IsStore)) return true;
            if (__instance.InvItem.baseItem.MaxStackSize > 1 && __instance.InvItem.stackSize > 1)
            {
                UIMessageBox dialog = UIInventoryGridItem.ShowSplitDialog(__instance.InvItem);
                if (dialog != null)
                    dialog.OnDialogEnd = (UIMessageBox.OnEndDialog)Delegate.Combine(dialog.OnDialogEnd,
                        (UIMessageBox.OnEndDialog)((result, owner) =>
                        {
                            if (result == UIMessageBox.Result.AFFIRMATIVE)
                                InventoryCommands.CaptureBeginDrag(__instance, owner.NumericValue);
                        }));
                return false;
            }
            if (__instance.InvItem.baseItem is Consumable && UIInventoryManager.Instance != null)
            {
                PartyMemberAI member = UIInventoryManager.Instance.SelectedCharacter != null
                    ? UIInventoryManager.Instance.SelectedCharacter.GetComponent<PartyMemberAI>() : null;
                BaseInventory target = member == null ? null : __instance.Owner == UIInventoryManager.Instance.QuickItemsGrid
                    ? (BaseInventory)member.Inventory : member.QuickbarInventory;
                if (target != null) InventoryCommands.CaptureQuantity(__instance, __instance.InvItem.stackSize, target);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(UIInventoryManager), "EquipToQuickItems")]
    internal static class UIInventoryManager_EquipToQuickItems_Intent
    {
        private static bool Prefix(UIInventoryGridItem focusedGridItem, ref string __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            PartyMemberAI member = UIInventoryManager.Instance.SelectedCharacter != null
                ? UIInventoryManager.Instance.SelectedCharacter.GetComponent<PartyMemberAI>() : null;
            if (member != null && focusedGridItem != null && focusedGridItem.InvItem != null)
                InventoryCommands.CaptureQuantity(focusedGridItem, focusedGridItem.InvItem.stackSize, member.QuickbarInventory);
            __result = "";
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryActionsPopup), nameof(UIInventoryActionsPopup.OnItemClicked))]
    internal static class UIInventoryActionsPopup_OnItemClicked_Intent
    {
        private static readonly AccessTools.FieldRef<UIInventoryActionsPopup, UIInventoryGridItem> GridItem =
            AccessTools.FieldRefAccess<UIInventoryActionsPopup, UIInventoryGridItem>("m_GridItem");
        private static readonly System.Type ActionItemType = typeof(UIInventoryActionsPopup).Assembly.GetType("UIInventoryActionsPopupItem");
        private static readonly System.Reflection.FieldInfo Action = AccessTools.Field(ActionItemType, "Action");

        private static bool Prefix(UIInventoryActionsPopup __instance, GameObject sender)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            Component action = sender != null && ActionItemType != null ? sender.GetComponent(ActionItemType) : null;
            if (action == null || Action == null
                || (UIInventoryActionsPopup.ItemAction)Action.GetValue(action) != UIInventoryActionsPopup.ItemAction.Equip) return true;
            InventoryCommands.CaptureActionEquip(GridItem(__instance));
            __instance.Hide();
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.RemoveItem))]
    internal static class UIInventoryGridItem_RemoveItem_Guard
    {
        private static bool Prefix(UIInventoryGridItem __instance, ref InventoryItem __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (GameInput.GetShiftkey() && !UIGlobalInventory.Instance.DraggingItem && __instance.InvItem != null)
                InventoryCommands.CaptureQuantity(__instance, __instance.InvItem.stackSize, GameState.PlayerStashInventory);
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.PutItem))]
    internal static class UIInventoryGridItem_PutItem_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "TryMergeInto")]
    internal static class UIInventoryGridItem_TryMergeInto_Guard
    {
        private static bool Prefix(InventoryItem item, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = item != null ? item.stackSize : 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.DestroyItem))]
    internal static class UIInventoryGridItem_DestroyItem_Intent
    {
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureDestroy(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.SelectWeaponSet))]
    internal static class Equipment_SelectWeaponSet_Intent
    {
        private static bool Prefix(Equipment __instance, int index, bool enforceRecoveryPenalty)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (enforceRecoveryPenalty) InventoryCommands.CaptureSelectWeapon(__instance, index);
            return false;
        }
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.SelectNextWeaponSet))]
    internal static class Equipment_SelectNextWeaponSet_Intent
    {
        private static bool Prefix(Equipment __instance)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureNextWeapon(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(PartyMemberAI), nameof(PartyMemberAI.UpdateStateAfterWeaponSetChange))]
    internal static class PartyMemberAI_UpdateStateAfterWeaponSetChange_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.StartUse))]
    internal static class Consumable_StartUse_Intent
    {
        private static bool Prefix(Consumable __instance, GameObject owner)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureQuickItem(__instance, owner, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.UseImmediately))]
    internal static class Consumable_UseImmediately_Intent
    {
        private static bool Prefix(Consumable __instance, GameObject owner)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            InventoryCommands.CaptureQuickItem(__instance, owner, true);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIGrimoireKnownSpells), "OnChildClick")]
    internal static class UIGrimoireKnownSpells_OnChildClick_Intent
    {
        private static bool Prefix(GameObject sender)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            UIGrimoireSpell spell = sender != null ? sender.GetComponentInParent<UIGrimoireSpell>() : null;
            if (spell != null && UIGrimoireManager.Instance != null)
                InventoryCommands.CaptureGrimoire(UIGrimoireManager.Instance.LoadedGrimoire, spell.Spell, 0);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIGrimoireInSpellRow), "OnChildClick")]
    internal static class UIGrimoireInSpellRow_OnChildClick_Intent
    {
        private static bool Prefix(GameObject sender)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            UIGrimoireSpell spell = sender != null ? sender.GetComponentInParent<UIGrimoireSpell>() : null;
            if (spell != null && UIGrimoireManager.Instance != null)
                InventoryCommands.CaptureGrimoire(UIGrimoireManager.Instance.LoadedGrimoire, spell.Spell, 2);
            return false;
        }
    }

    [HarmonyPatch(typeof(UIGrimoireInSpellRow), "RemoveSpell")]
    internal static class UIGrimoireInSpellRow_RemoveSpell_Intent
    {
        private static bool Prefix(GenericSpell spell)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            if (UIGrimoireManager.Instance != null)
                InventoryCommands.CaptureGrimoire(UIGrimoireManager.Instance.LoadedGrimoire, spell, 2);
            return false;
        }
    }

    [HarmonyPatch(typeof(EquipmentSoulbind), "Update")]
    internal static class EquipmentSoulbind_Update_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(EquipmentSoulbind), nameof(EquipmentSoulbind.Bind))]
    internal static class EquipmentSoulbind_Bind_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(EquipmentSoulbind), nameof(EquipmentSoulbind.Unbind))]
    internal static class EquipmentSoulbind_Unbind_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(Consumable), "HandleGameUtilitiesOnCombatEnd")]
    internal static class Consumable_CombatReset_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(Consumable), "HandleGameOnResting")]
    internal static class Consumable_RestReset_Guest
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(InventoryItem), nameof(InventoryItem.SetStackSize))]
    internal static class InventoryItem_SetStackSize_Guard
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(UIInventoryItemZone), nameof(UIInventoryItemZone.Take))]
    internal static class UIInventoryItemZone_Take_Guard
    {
        private static bool Prefix(ref InventoryItem __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryItemZone), nameof(UIInventoryItemZone.Put))]
    internal static class UIInventoryItemZone_Put_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryItemZone), nameof(UIInventoryItemZone.Add))]
    internal static class UIInventoryItemZone_Add_Guard
    {
        private static bool Prefix(int quantity, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = quantity;
            return false;
        }
    }

    [HarmonyPatch(typeof(UIInventoryItemZone), nameof(UIInventoryItemZone.Remove))]
    internal static class UIInventoryItemZone_Remove_Guard
    {
        private static bool Prefix(ref Item __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.AddItem), typeof(Item), typeof(int), typeof(int), typeof(bool))]
    internal static class BaseInventory_AddItem_Guard
    {
        private static bool Prefix(int addCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = addCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddItem), typeof(Item), typeof(int), typeof(int), typeof(bool))]
    internal static class PlayerInventory_AddItem_Guard
    {
        private static bool Prefix(int addCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = addCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(QuestInventory), nameof(QuestInventory.AddItem), typeof(Item), typeof(int), typeof(int), typeof(bool))]
    internal static class QuestInventory_AddItem_Guard
    {
        private static bool Prefix(int addCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = addCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(CraftingInventory), nameof(CraftingInventory.AddItem), typeof(Item), typeof(int), typeof(int), typeof(bool))]
    internal static class CraftingInventory_AddItem_Guard
    {
        private static bool Prefix(int addCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = addCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.PutItem), typeof(InventoryItem), typeof(int))]
    internal static class BaseInventory_PutItem_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.PutItem), typeof(InventoryItem), typeof(int))]
    internal static class PlayerInventory_PutItem_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(QuestInventory), nameof(QuestInventory.PutItem), typeof(InventoryItem), typeof(int))]
    internal static class QuestInventory_PutItem_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(CraftingInventory), nameof(CraftingInventory.PutItem), typeof(InventoryItem), typeof(int))]
    internal static class CraftingInventory_PutItem_Guard
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.TakeItem))]
    internal static class BaseInventory_TakeItem_Guard
    {
        private static bool Prefix(ref InventoryItem __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.RemoveItem), typeof(Item), typeof(int))]
    internal static class BaseInventory_RemoveItem_Guard
    {
        private static bool Prefix(ref Item __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.RemoveItem), typeof(Item), typeof(int))]
    internal static class PlayerInventory_RemoveItem_Guard
    {
        private static bool Prefix(ref Item __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.DestroyItem), typeof(Item), typeof(int))]
    internal static class BaseInventory_DestroyItem_Guard
    {
        private static bool Prefix(int destroyCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = destroyCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.DestroyItem), typeof(Item), typeof(int))]
    internal static class PlayerInventory_DestroyItem_Guard
    {
        private static bool Prefix(int destroyCount, ref int __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = destroyCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInventory), nameof(BaseInventory.ClearInventory))]
    internal static class BaseInventory_ClearInventory_Guard
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.Equip), typeof(Equippable), typeof(Equippable.EquipmentSlot), typeof(bool))]
    internal static class Equipment_Equip_Guard
    {
        private static bool Prefix(ref Equippable __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.EquipWeapon))]
    internal static class Equipment_EquipWeapon_Guard
    {
        private static bool Prefix(ref Equippable __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.UnEquip), typeof(Equippable), typeof(Equippable.EquipmentSlot))]
    internal static class Equipment_UnEquip_Guard
    {
        private static bool Prefix(ref Equippable __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.UnEquipWeapon), typeof(Equippable), typeof(Equippable.EquipmentSlot), typeof(int))]
    internal static class Equipment_UnEquipWeapon_Guard
    {
        private static bool Prefix(ref Equippable __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Equippable), nameof(Equippable.Equip))]
    internal static class Equippable_Equip_Guard
    {
        private static bool Prefix() => !Plugin.IsMirroring || InventorySync.IsApplyingHostState;
    }

    [HarmonyPatch(typeof(Equippable), nameof(Equippable.UnEquip))]
    internal static class Equippable_UnEquip_Guard
    {
        private static bool Prefix(ref Equippable __result)
        {
            if (!Plugin.IsMirroring || InventorySync.IsApplyingHostState) return true;
            __result = null;
            return false;
        }
    }
}
