using System;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    public void CacheWorldItems()
    {
        if (NetworkLifecycle.Instance.IsHost()) return;
        foreach (var item in NetworkedItem.GetAll().ToArray())
            CacheLocalWorldItem(item);
        ClientInitialised = true;
    }

    // Called once from each item's Start, after synchronous replica/job binding.
    // Streaming a new station must not introduce a second, client-owned world.
    internal void OnItemStarted(NetworkedItem item)
    {
        if (ClientInitialised && NetworkLifecycle.Instance.IsClientRunning &&
            !NetworkLifecycle.Instance.IsHost())
            CacheLocalWorldItem(item);
    }

    private void CacheLocalWorldItem(NetworkedItem item)
    {
        if (item == null || item.Item == null || item.IsCached || item.NetId != 0 ||
            item.DormantOwner.HasValue || DoNotCreateItem(item.TrackedItemType)) return;
        try
        {
            var native = item.Item;
            // Keep the restored personal inventory, not every essential prefab in the world.
            if ((native.IsEssential() && native.InventorySpecs.BelongsToPlayer) || native.IsGrabbed() ||
                Inventory.Instance.Contains(item.gameObject, false) ||
                StorageController.Instance.StorageInventory.ContainsItem(native) ||
                StorageController.Instance.IsInStorageLostAndFound(native) ||
                !string.IsNullOrEmpty(Inventory.Instance.ItemContainerRegistry
                    .GetItemContainerIdAndIndex(item.gameObject).ContainerId)) return;
            SendToCache(item);
        }
        catch (Exception ex)
        {
            NetworkLifecycle.Instance.Client.LogError($"Error caching local world item: {ex}");
        }
    }

    private NetworkedItem GetFromCache(string prefabName)
    {
        if (CachedItems.TryGetValue(prefabName, out var items))
        {
            while (items.Count > 0)
            {
                var item = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (item != null && item.Item != null && item.IsCached) return item;
            }
            CachedItems.Remove(prefabName);
        }
        return null;
    }

    private void SendToCache(NetworkedItem item)
    {
        string prefabName = item?.Item?.InventorySpecs?.itemPrefabName;
        if (item == null || string.IsNullOrEmpty(prefabName)) return;
        if (CachedItems.TryGetValue(prefabName, out var cached) && cached.Contains(item)) return;

        // A tombstone can arrive while the client is grabbing the item. End the
        // native interaction before disabling or reusing its object.
        item.ReleaseLocalInteraction();
        item.ResetForCache();
        StorageController.Instance.RemoveItemFromStorageItemList(item.Item);
        item.gameObject.SetActive(false);
        // Keep RespawnOnDrop: native storage calls UpdateSpawnParams unconditionally.
        // RespawnOnDropPatch suppresses autonomous client respawns without removing it.
        item.Item.InventorySpecs.BelongsToPlayer = false;
        item.NetId = 0;
        if (!CachedItems.TryGetValue(prefabName, out cached))
            CachedItems[prefabName] = cached = new List<NetworkedItem>();
        cached.Add(item);
    }
}
