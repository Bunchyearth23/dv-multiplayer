using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using DV.Shops;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    private readonly Dictionary<ServerPlayer, GuardedRoutine> inventoryRestores = new();
    private readonly Dictionary<Guid, List<NetworkedItem>> offlineInventories = new();

    public void CancelInventoryRestores()
    {
        foreach (var routine in inventoryRestores.Values.ToArray())
        {
            try { routine.Dispose(); }
            catch (Exception ex) { Multiplayer.LogError($"Inventory cancellation callback failed: {ex}"); }
        }
        inventoryRestores.Clear();
    }

    public void RestorePlayerInventory(ServerPlayer player, Action<PlayerItemSaveData[]> onReady, Action<Exception> onError)
    {
        if (inventoryRestores.ContainsKey(player)) return;
        var routine = new GuardedRoutine(RestoreInventory(player, onReady), error =>
        {
            inventoryRestores.Remove(player);
            onError(error);
        });
        inventoryRestores.Add(player, routine);
        CoroutineManager.Instance.StartCoroutine(routine);
    }

    private bool IsConnected(ServerPlayer player) => NetworkLifecycle.Instance.Server != null &&
        NetworkLifecycle.Instance.Server.TryGetServerPlayer(player.PlayerId, out var current) && current == player;

    private IEnumerator RestoreInventory(ServerPlayer player, Action<PlayerItemSaveData[]> onReady)
    {
        var saved = player.InventoryRestoreData ?? Array.Empty<PlayerItemSaveData>();
        var restored = new List<(NetworkedItem Item, PlayerItemSaveData Data)>();
        var created = new List<NetworkedItem>();
        bool completed = false;
        try
        {
            foreach (var data in saved)
            {
                if (!IsConnected(player)) yield break;
                var identity = PlayerInventorySaveCodec.Identity(data.State);
                var item = NetworkedItem.FindPersistentItem(identity);
                if (item != null)
                {
                    if (item.DormantOwner != player.Guid || item.Item?.InventorySpecs?.ItemPrefabName != data.ItemPrefabName)
                        throw new InvalidOperationException($"Inventory identity {identity} is already active elsewhere.");
                }
                else
                {
                    var prefab = Resources.Load<GameObject>(data.ItemPrefabName);
                    if (prefab == null) throw new InvalidOperationException($"Inventory prefab is unavailable: {data.ItemPrefabName}");
                    var instance = Instantiate(prefab, new Vector3(0, 5000 + created.Count * 2, 0), Quaternion.identity);
                    item = instance.GetOrAddComponent<NetworkedItem>();
                    created.Add(item);
                    item.StageInventory(player.Guid);
                    instance.SetActive(true);
                    item.StageInventory(player.Guid);
                    item.RestorePersistentIdentity(identity);
                    if (item.Item?.InventorySpecs == null) throw new InvalidOperationException("Inventory prefab has no item specification.");
                    item.Item.InventorySpecs.BelongsToPlayer = data.BelongsToPlayer;
                }
                restored.Add((item, data));
            }
            // Native item components install their state listeners in Awake/Start.
            yield return null;
            if (!IsConnected(player)) yield break;
            foreach (var entry in restored)
                if (created.Contains(entry.Item))
                    entry.Item.GetComponent<ItemSaveData>().LoadItemData((Newtonsoft.Json.Linq.JObject)entry.Data.State.DeepClone());
            foreach (var entry in restored)
                if (created.Contains(entry.Item)) entry.Item.GetComponent<ItemSaveData>().PostLoadItemData();
            var deadline = new LoadingDeadline("server inventory initialization", 60, 180);
            while (restored.Any(entry => entry.Item == null || !entry.Item.RegistrationComplete))
            {
                if (!IsConnected(player)) yield break;
                deadline.Check($"{restored.Count(entry => entry.Item != null && entry.Item.RegistrationComplete)}/{restored.Count} items ready");
                yield return null;
            }
            if (!IsConnected(player)) yield break;
            foreach (var entry in restored) entry.Item.RestoreInventoryOwner(player, entry.Data);
            var snapshot = CaptureInventoryItems(restored.Select(entry => entry.Item));
            player.InventoryRestoreComplete = true;
            offlineInventories.Remove(player.Guid);
            completed = true;
            inventoryRestores.Remove(player);
            onReady(snapshot);
        }
        finally
        {
            inventoryRestores.Remove(player);
            if (!completed)
            {
                player.InventoryRestoreComplete = false;
                // A later item/state callback can fail after some reused replicas acquired ownership.
                // Put those replicas back in the offline inventory before reporting failure.
                foreach (var entry in restored)
                {
                    if (entry.Item == null || created.Contains(entry.Item)) continue;
                    try { entry.Item.SuspendInventory(player.Guid); }
                    catch (Exception ex) { Multiplayer.LogError($"Inventory rollback failed for {entry.Item.PersistentId}: {ex}"); }
                }
                foreach (var item in created)
                {
                    if (item == null) continue;
                    try
                    {
                        var restocker = item.GetComponent<ShopRestocker>();
                        if (restocker != null) restocker.restockOnItemDestroyed = false;
                        item.SuspendInventory(player.Guid);
                        Object.Destroy(item.gameObject);
                    }
                    catch (Exception ex) { Multiplayer.LogError($"Inventory staging cleanup failed for {item.PersistentId}: {ex}"); }
                }
            }
        }
    }

    public PlayerItemSaveData[] CapturePlayerInventory(ServerPlayer player)
    {
        if (!player.InventoryRestoreComplete) throw new InvalidOperationException("Inventory restoration has not completed.");
        var items = new List<NetworkedItem>();
        foreach (ushort id in player.OwnedItems)
        {
            if (!NetworkedItem.TryGet(id, out var item) || item.OwnerId != player.PlayerId)
                throw new InvalidOperationException($"Inventory item {id} is missing or has a different owner.");
            items.Add(item);
        }
        return CaptureInventoryItems(items);
    }

    private PlayerItemSaveData[] CaptureInventoryItems(IEnumerable<NetworkedItem> items)
    {
        var saved = items.OrderBy(item => item.PersistentId).Select(item => item.CaptureInventory()).ToArray();
        return InventorySlotAllocator.Allocate(saved, Inventory.Instance.GetItemsArray().Length);
    }

    private void PlayerDisconnected(ServerPlayer player)
    {
        if (inventoryRestores.TryGetValue(player, out var routine))
        {
            try { routine.Dispose(); }
            catch (Exception ex) { Multiplayer.LogError($"Inventory restore cancellation failed for {player.Guid}: {ex}"); }
        }
        inventoryRestores.Remove(player);
        var items = player.OwnedItems.Select(id => NetworkedItem.TryGet(id, out var item) ? item : null)
            .Where(item => item != null && item.OwnerId == player.PlayerId).ToList();
        if (player.InventoryRestoreComplete) offlineInventories[player.Guid] = items;
        foreach (var item in items)
        {
            try
            {
                var tombstone = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
                if (tombstone != null) AddDirtyItemSnapshot(item, tombstone, false);
            }
            catch (Exception ex) { Multiplayer.LogError($"Inventory removal notification failed for {item.PersistentId}: {ex}"); }
            try { item.SuspendInventory(player.Guid); }
            catch (Exception ex) { Multiplayer.LogError($"Inventory suspension failed for {item.PersistentId}: {ex}"); }
        }
        player.ClearOwnedItems();
    }

    public IEnumerable<KeyValuePair<Guid, PlayerItemSaveData[]>> CaptureOfflineInventories()
    {
        foreach (var entry in offlineInventories.ToArray())
        {
            PlayerItemSaveData[] items;
            try { items = CaptureInventoryItems(entry.Value); }
            catch (Exception ex)
            {
                Multiplayer.LogError($"Offline inventory retained for {entry.Key}: {ex}");
                continue;
            }
            yield return new KeyValuePair<Guid, PlayerItemSaveData[]>(entry.Key, items);
        }
    }
}
