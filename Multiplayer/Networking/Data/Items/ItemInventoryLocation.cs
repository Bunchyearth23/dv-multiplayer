using System;
using System.Collections.Generic;

namespace Multiplayer.Networking.Data.Items;

public readonly struct ItemInventoryLocation : IEquatable<ItemInventoryLocation>
{
    public const string SlotKey = "__mp_slot", ContainerKey = "__mp_container",
        ContainerSlotKey = "__mp_container_slot", LockedKey = "__mp_locked";
    public readonly int Slot, ContainerSlot;
    public readonly string ContainerId;
    public readonly bool Locked;
    public static ItemInventoryLocation Unplaced => new(-1, null, -1, false);

    public ItemInventoryLocation(int slot, string containerId, int containerSlot, bool locked)
    {
        if (slot < -1 || slot > 32767 || containerSlot < -1 || containerSlot > 32767 ||
            (containerId?.Length ?? 0) > 256 || (string.IsNullOrEmpty(containerId) != (containerSlot == -1)))
            throw new ArgumentException("Invalid inventory location.");
        Slot = slot; ContainerId = string.IsNullOrEmpty(containerId) ? null : containerId;
        ContainerSlot = containerSlot; Locked = locked;
    }

    public void Write(Dictionary<string, object> states)
    {
        states[SlotKey] = Slot; states[ContainerKey] = ContainerId ?? "";
        states[ContainerSlotKey] = ContainerSlot; states[LockedKey] = Locked;
    }

    public static bool TryRead(Dictionary<string, object> states, out ItemInventoryLocation location)
    {
        location = Unplaced;
        if (states == null || !states.TryGetValue(SlotKey, out var slot) || slot is not int index ||
            !states.TryGetValue(ContainerKey, out var container) || container is not string id ||
            !states.TryGetValue(ContainerSlotKey, out var containerSlot) || containerSlot is not int child ||
            !states.TryGetValue(LockedKey, out var locked) || locked is not bool isLocked) return false;
        try { location = new ItemInventoryLocation(index, id, child, isLocked); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsKey(string key) => key == SlotKey || key == ContainerKey || key == ContainerSlotKey || key == LockedKey;
    public bool Equals(ItemInventoryLocation other) => Slot == other.Slot && ContainerSlot == other.ContainerSlot &&
        ContainerId == other.ContainerId && Locked == other.Locked;
    public override bool Equals(object obj) => obj is ItemInventoryLocation other && Equals(other);
    public override int GetHashCode() => Slot ^ ContainerSlot ^ (ContainerId?.GetHashCode() ?? 0) ^ Locked.GetHashCode();
}
