using System;
using Multiplayer.Networking.Data.Items;

internal static class InventorySlotAllocatorTests
{
    private static PlayerItemSaveData Item(int slot = -1) => new PlayerItemSaveData
        { InventorySlotIndex = slot, ContainerSlotIndex = -1 };
    private static void Check(bool value) { if (!value) throw new Exception("Inventory allocation failed."); }

    public static void FillsHolesWithoutMovingExistingItems()
    {
        var source = new[] { Item(1), Item(), Item() };
        var result = InventorySlotAllocator.Allocate(source, 3);
        Check(result[0].InventorySlotIndex == 1 && result[1].InventorySlotIndex == 0 && result[2].InventorySlotIndex == 2);
        Check(source[1].InventorySlotIndex == -1);
    }

    public static void FullAndZeroCapacityPreserveOverflow()
    {
        var result = InventorySlotAllocator.Allocate(new[] { Item(0), Item(), Item() }, 1);
        Check(result.Length == 3 && result[1].InventorySlotIndex == -1 && result[2].InventorySlotIndex == -1);
        Check(InventorySlotAllocator.Allocate(new[] { Item() }, 0)[0].InventorySlotIndex == -1);
    }

    public static void ContainerContentsDoNotConsumeSlots()
    {
        var child = Item(); child.ContainerId = "case"; child.ContainerSlotIndex = 0;
        var result = InventorySlotAllocator.Allocate(new[] { child, Item() }, 1);
        Check(result[0].InventorySlotIndex == -1 && result[0].ContainerId == "case" && result[1].InventorySlotIndex == 0);
    }

    public static void DuplicateSlotsFailWithoutMutatingInput()
    {
        var source = new[] { Item(), Item(0), Item(0) };
        try { InventorySlotAllocator.Allocate(source, 2); }
        catch (InvalidOperationException) { Check(source[0].InventorySlotIndex == -1); return; }
        throw new Exception("Duplicate slot accepted.");
    }
}
