using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Networking.Data.Items;

/// <summary>Versioned inventory storage; session network IDs never survive a server restart.</summary>
public static class PlayerInventorySaveCodec
{
    public const string InventoryKey = "Inventory";
    public const string IdentityKey = "__multiplayer_item_id";
    public const int MaxItems = 512;

    public static Guid Identity(JObject state)
    {
        if (state?[IdentityKey]?.Type != JTokenType.String ||
            !Guid.TryParse((string)state[IdentityKey], out var id) || id == Guid.Empty)
            throw new FormatException("Inventory item has no valid persistent identity.");
        return id;
    }

    // Null means this profile predates inventory persistence; an empty array means an empty inventory.
    public static PlayerItemSaveData[] Read(JObject profile)
    {
        var token = profile?[InventoryKey];
        if (token == null) return null;
        if (token is not JObject inventory || (int?)inventory["Version"] != 1 || inventory["Items"] is not JArray rows)
            throw new FormatException("Unsupported player inventory save format.");
        if (rows.Count > MaxItems) throw new FormatException("Inventory exceeds the save limit.");
        var items = new List<PlayerItemSaveData>();
        foreach (var row in rows)
        {
            if (row is not JObject item || item["Position"] is not JArray position || position.Count != 3 ||
                item["Rotation"] is not JArray rotation || rotation.Count != 4 || item["State"] is not JObject state)
                throw new FormatException("Malformed inventory entry.");
            items.Add(new PlayerItemSaveData
            {
                NetId = 0, ItemPrefabName = (string)item["Prefab"],
                ItemPositionX = (float)position[0], ItemPositionY = (float)position[1], ItemPositionZ = (float)position[2],
                ItemRotationX = (float)rotation[0], ItemRotationY = (float)rotation[1],
                ItemRotationZ = (float)rotation[2], ItemRotationW = (float)rotation[3],
                BelongsToPlayer = (bool?)item["Owned"] ?? false, IsGrabbed = (bool?)item["Held"] ?? false,
                CarGuid = (string)item["Car"], ContainerId = (string)item["Container"],
                InventorySlotIndex = (int?)item["Slot"] ?? -1, ContainerSlotIndex = (int?)item["ContainerSlot"] ?? -1,
                InLockedSlot = (bool?)item["Locked"] ?? false, IsDropped = (bool?)item["Dropped"] ?? false,
                State = (JObject)state.DeepClone()
            });
        }
        Validate(items);
        return items.ToArray();
    }

    public static void Write(JObject profile, IEnumerable<PlayerItemSaveData> source)
    {
        if (profile == null || source == null) throw new ArgumentNullException();
        var items = new List<PlayerItemSaveData>();
        foreach (var item in source)
        {
            if (items.Count == MaxItems) throw new FormatException("Inventory exceeds the save limit.");
            items.Add(item);
        }
        Validate(items);
        var rows = new JArray();
        foreach (var item in items)
        {
            rows.Add(new JObject
            {
                ["Prefab"] = item.ItemPrefabName,
                ["Position"] = new JArray(item.ItemPositionX, item.ItemPositionY, item.ItemPositionZ),
                ["Rotation"] = new JArray(item.ItemRotationX, item.ItemRotationY, item.ItemRotationZ, item.ItemRotationW),
                ["Owned"] = item.BelongsToPlayer, ["Held"] = item.IsGrabbed,
                ["Car"] = item.CarGuid, ["Container"] = item.ContainerId,
                ["Slot"] = item.InventorySlotIndex, ["ContainerSlot"] = item.ContainerSlotIndex,
                ["Locked"] = item.InLockedSlot, ["Dropped"] = item.IsDropped,
                ["State"] = item.State.DeepClone()
            });
        }
        // Commit only after the whole replacement has been validated and encoded.
        profile[InventoryKey] = new JObject { ["Version"] = 1, ["Items"] = rows };
    }

    private static void Validate(IEnumerable<PlayerItemSaveData> items)
    {
        var identities = new HashSet<Guid>();
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            try { _ = new ItemInventoryLocation(item.InventorySlotIndex, item.ContainerId, item.ContainerSlotIndex, item.InLockedSlot); }
            catch (ArgumentException ex) { throw new FormatException("Invalid saved inventory location.", ex); }
            if (!identities.Add(Identity(item.State))) throw new FormatException("Duplicate persistent item identity.");
            if (string.IsNullOrWhiteSpace(item.ItemPrefabName) || item.ItemPrefabName.Length > 256 ||
                item.InventorySlotIndex < -1 || item.InventorySlotIndex > 32767 ||
                item.ContainerSlotIndex < -1 || item.ContainerSlotIndex > 32767 || (item.ContainerId?.Length ?? 0) > 256 ||
                item.State.ToString(Formatting.None).Length > 64 * 1024)
                throw new FormatException("Invalid inventory item data.");
            foreach (float number in new[] { item.ItemPositionX, item.ItemPositionY, item.ItemPositionZ,
                item.ItemRotationX, item.ItemRotationY, item.ItemRotationZ, item.ItemRotationW })
                if (float.IsNaN(number) || float.IsInfinity(number)) throw new FormatException("Invalid inventory transform.");
            string slot = !string.IsNullOrEmpty(item.ContainerId) && item.ContainerSlotIndex >= 0
                ? $"container:{item.ContainerId.Length}:{item.ContainerId}:{item.ContainerSlotIndex}"
                : item.InventorySlotIndex >= 0 ? $"inventory:{item.InventorySlotIndex}" : null;
            if (slot != null && !slots.Add(slot)) throw new FormatException("Duplicate inventory slot.");
        }
    }
}
