using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Sync;
using UnityEngine;

namespace PoECoop.Patches
{
    [HarmonyPatch(typeof(Container), nameof(Container.Open))]
    internal static class Container_Open_SharedLoot
    {
        private static bool Prefix(Container __instance, GameObject user, ref bool __result)
        {
            if (Plugin.IsGuest)
            {
                __result = false;
                return false;
            }
            if (Plugin.IsHost)
            {
                if (GameState.InCombat && user != null && user.GetComponent<PartyMemberAI>() != null)
                {
                    __result = false;
                    return false;
                }
                if (LootSync.TryOpenExisting(__instance, user))
                {
                    __result = true;
                    return false;
                }
                LootSync.BeginContainerOpen(__instance, user);
            }
            return true;
        }

        private static void Finalizer(Container __instance, GameObject user, bool __result)
        {
            if (Plugin.IsHost) LootSync.EndContainerOpen(__instance, user, __result);
        }
    }

    [HarmonyPatch(typeof(OCL), nameof(OCL.Use))]
    internal static class OCL_Use_SharedLoot
    {
        private static bool Prefix(OCL __instance, GameObject user, ref bool __result)
        {
            Container container = __instance as Container;
            if (!Plugin.IsHost || container == null || container.CurrentState != OCL.State.Open
                && container.CurrentState != OCL.State.SealedOpen) return true;
            if (GameState.InCombat || !LootSync.TryOpenExisting(container, user)) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), "ShowSingleContainerLootUI")]
    internal static class Container_ShowSingleContainerLootUI_SharedLoot
    {
        private static bool Prefix(Container __instance, PartyMemberAI user)
        {
            if (LootSync.RemoteContainerOpening)
            {
                LootSync.CaptureRemoteSingle(__instance, user);
                return false;
            }
            return !Plugin.IsGuest;
        }
    }

    [HarmonyPatch(typeof(Container), "LootAllContainersInArea")]
    internal static class Container_LootAllContainersInArea_SharedLoot
    {
        private static readonly MethodInfo BaseOpen = AccessTools.Method(typeof(Container), "BaseOpen");
        private static readonly MethodInfo FindArea = AccessTools.Method(typeof(Container), "FindAreaLootableContainersInRangeOfUs");

        private static bool Prefix(Container __instance, PartyMemberAI partyMemberLooting, bool ignoreLock)
        {
            if (Plugin.IsGuest) return false;
            if (!LootSync.RemoteContainerOpening) return true;

            var containers = new List<Container>();
            var nearby = FindArea.Invoke(__instance, new object[] { __instance.transform.position }) as HashSet<Container>;
            if (nearby == null) return false;
            foreach (Container container in nearby)
            {
                if (container == null) continue;
                if (container != __instance && !(bool)BaseOpen.Invoke(container, new object[] { partyMemberLooting.gameObject, ignoreLock }))
                    continue;
                Loot loot = container.GetComponent<Loot>();
                if (loot != null && !container.HasInteracted) loot.PopulateInventory();
                container.HasInteracted = true;
                if (container.GetComponent<Inventory>() == null) continue;
                if (container == __instance) containers.Insert(0, container);
                else containers.Add(container);
                foreach (InventoryItem item in container.GetComponent<Inventory>().ItemList)
                    item.AreaLootSource = container == __instance;
            }
            if (!containers.Contains(__instance)) containers.Insert(0, __instance);
            LootSync.CaptureRemoteArea(__instance, partyMemberLooting, containers);
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.Close))]
    internal static class Container_Close_Guest
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsGuest) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.CloseInventoryCB))]
    internal static class Container_CloseInventoryCB_SharedLoot
    {
        private static bool Prefix(Container __instance)
        {
            if (Plugin.IsGuest) return false;
            return LootSync.HostContainerClose(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.CloseInventoryAreaLoot))]
    internal static class Container_CloseInventoryAreaLoot_SharedLoot
    {
        private static bool Prefix(Container __instance)
        {
            if (Plugin.IsGuest) return false;
            return LootSync.HostAreaClose(__instance);
        }
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.PopulateInventory))]
    internal static class Loot_PopulateInventory_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.Restored))]
    internal static class Loot_Restored_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), "Update")]
    internal static class Loot_Update_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.PrepareItemDrop))]
    internal static class Loot_PrepareItemDrop_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.OnGibbed))]
    internal static class Loot_OnGibbed_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.DropAllItems))]
    internal static class Loot_DropAllItems_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), nameof(Loot.CreateLootBag))]
    internal static class Loot_CreateLootBag_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(Loot), "CreateLootOnBody")]
    internal static class Loot_CreateLootOnBody_HostOnly
    {
        private static bool Prefix() => !Plugin.IsGuest;
    }

    [HarmonyPatch(typeof(AutoLootContainer), nameof(AutoLootContainer.Open))]
    internal static class AutoLootContainer_Open_HostOnly
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Plugin.IsGuest) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(UILootManager), "Hide")]
    internal static class UILootManager_Hide_SharedLoot
    {
        private static void Postfix(bool __result)
        {
            if (__result && Plugin.IsGuest) LootSync.GuestWindowClosing();
        }
    }

    [HarmonyPatch(typeof(UILootManager), nameof(UILootManager.TakeAll))]
    internal static class UILootManager_TakeAll_SharedLoot
    {
        private static bool Prefix(BaseInventory target)
        {
            if (!Plugin.IsGuest) return true;
            return !LootSync.GuestTakeAll(target);
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), nameof(UIInventoryGridItem.SendQuantity))]
    internal static class UIInventoryGridItem_SendQuantity_SharedLoot
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(UIInventoryGridItem __instance, int qty, BaseInventory target)
        {
            if (!Plugin.IsGuest) return true;
            return !LootSync.GuestSendQuantity(__instance, qty, target);
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "OnClick")]
    internal static class UIInventoryGridItem_OnClick_SharedLoot
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsGuest) return true;
            return !LootSync.GuestClick(__instance);
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "OnDoubleClick")]
    internal static class UIInventoryGridItem_OnDoubleClick_SharedLoot
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsGuest) return true;
            return !LootSync.GuestDoubleClick(__instance);
        }
    }

    [HarmonyPatch(typeof(UIInventoryGridItem), "DoClick")]
    internal static class UIInventoryGridItem_DoClick_StashLoot
    {
        private static bool Prefix(UIInventoryGridItem __instance)
        {
            if (!Plugin.IsGuest || !GameInput.GetShiftkey()) return true;
            return !LootSync.GuestShiftTake(__instance);
        }
    }

    [HarmonyPatch(typeof(UIGlobalInventory), nameof(UIGlobalInventory.BeginDrag))]
    internal static class UIGlobalInventory_BeginDrag_SharedLoot
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(UIInventoryGridItem item, int qty)
        {
            if (!Plugin.IsGuest) return true;
            return !LootSync.GuestBeginDrag(item, qty);
        }
    }
}
