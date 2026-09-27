using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Networking.Data.Items;

public static class StartingInventoryGrants
{
    public const string Key = "StartingItemGrants";
    public const string AuthoritativeLoadKey = "Multiplayer_AuthoritativeInventory";
    private static readonly string[] SteamOperatingItemPrefabs = { "shovel", "lighter", "Oiler" };

    // These reusable tools let every player tend and maintain a steam locomotive.
    // Coal and water are taken from the locomotive and service stations, not carried as inventory items.
    public static IEnumerable<PlayerItemSaveData> CreateSteamOperatingItems()
    {
        foreach (var prefab in SteamOperatingItemPrefabs)
            yield return new PlayerItemSaveData
            {
                ItemPrefabName = prefab,
                BelongsToPlayer = true,
                InventorySlotIndex = -1,
                ContainerSlotIndex = -1,
                ItemRotationW = 1,
                State = new JObject()
            };
    }

    // Return a replacement profile; callers commit it only after all validation succeeds.
    public static JObject Prepare(JObject profile, IEnumerable<PlayerItemSaveData> catalog, int capacity)
    {
        var replacement = (JObject)profile?.DeepClone() ?? new JObject();
        var items = (PlayerInventorySaveCodec.Read(replacement) ?? Array.Empty<PlayerItemSaveData>()).ToList();
        JObject grants;
        if (replacement[Key] == null) grants = new JObject();
        else
        {
            if (replacement[Key] is not JObject ledger || (int?)ledger["Version"] != 1 || ledger["Items"] is not JObject entries)
                throw new FormatException("Unsupported starting inventory grant ledger.");
            grants = (JObject)entries.DeepClone();
            foreach (var entry in grants.Properties())
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 256 ||
                    entry.Value.Type != JTokenType.String || !Guid.TryParse((string)entry.Value, out var id) || id == Guid.Empty)
                    throw new FormatException("Invalid starting inventory grant.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in catalog)
        {
            string prefab = candidate.ItemPrefabName;
            if (string.IsNullOrWhiteSpace(prefab) || prefab.Length > 256)
                throw new FormatException("Invalid starting item prefab.");
            if (!seen.Add(prefab) || grants[prefab] != null) continue;
            var existing = items.FindIndex(item => item.ItemPrefabName == prefab);
            if (existing >= 0)
            {
                grants[prefab] = PlayerInventorySaveCodec.Identity(items[existing].State).ToString("N");
                continue;
            }
            var added = candidate;
            added.NetId = 0;
            added.State = (JObject)candidate.State?.DeepClone() ?? new JObject();
            var identity = Guid.NewGuid();
            added.State[PlayerInventorySaveCodec.IdentityKey] = identity.ToString("N");
            // Preferred native slots can already be occupied in an existing profile.
            if (added.InventorySlotIndex >= capacity || items.Any(item => item.InventorySlotIndex == added.InventorySlotIndex))
                added.InventorySlotIndex = -1;
            items.Add(added);
            grants[prefab] = identity.ToString("N");
        }
        var allocated = InventorySlotAllocator.Allocate(items.ToArray(), capacity);
        // Steam tools are utility items: keep them in the backpack, never the belt.
        // Existing profiles may already contain these grants in a hotbar slot, so
        // normalize them on every preparation, not only when first granting them.
        var occupied = new HashSet<int>();
        foreach (var item in allocated)
            if (item.InventorySlotIndex >= 0) occupied.Add(item.InventorySlotIndex);
        for (int i = 0; i < allocated.Length; i++)
        {
            var item = allocated[i];
            if (!Array.Exists(SteamOperatingItemPrefabs, prefab => prefab == item.ItemPrefabName) ||
                item.InventorySlotIndex >= 12) continue;
            int slot = 12;
            while (slot < capacity && occupied.Contains(slot)) slot++;
            if (item.InventorySlotIndex >= 0) occupied.Remove(item.InventorySlotIndex);
            item.InventorySlotIndex = slot < capacity ? slot : -1;
            if (item.InventorySlotIndex >= 0) occupied.Add(item.InventorySlotIndex);
            allocated[i] = item;
        }
        PlayerInventorySaveCodec.Write(replacement, allocated);
        replacement[Key] = new JObject { ["Version"] = 1, ["Items"] = grants };
        return replacement;
    }
}
