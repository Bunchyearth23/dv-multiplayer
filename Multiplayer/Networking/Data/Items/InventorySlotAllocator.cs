using System;
using System.Collections.Generic;

namespace Multiplayer.Networking.Data.Items;

public static class InventorySlotAllocator
{
    // Unplaced overflow stays at -1 and is loaded into personal Lost and Found.
    public static PlayerItemSaveData[] Allocate(PlayerItemSaveData[] items, int capacity)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (capacity < 0 || capacity > 32768) throw new ArgumentOutOfRangeException(nameof(capacity));
        var result = (PlayerItemSaveData[])items.Clone();
        var occupied = new HashSet<int>();
        foreach (var item in result)
            if (item.InventorySlotIndex >= 0 && !occupied.Add(item.InventorySlotIndex))
                throw new InvalidOperationException("Duplicate inventory slot.");
        int next = 0;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i].InventorySlotIndex >= 0 || !string.IsNullOrEmpty(result[i].ContainerId)) continue;
            while (next < capacity && occupied.Contains(next)) next++;
            if (next == capacity) continue;
            result[i].InventorySlotIndex = next;
            occupied.Add(next++);
        }
        return result;
    }
}
