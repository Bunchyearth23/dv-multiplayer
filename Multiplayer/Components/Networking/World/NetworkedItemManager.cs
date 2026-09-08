using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.Interaction;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
{
    /*
     * Server 
     */

    //Culling distance for items
    public const float MAX_DISTANCE_TO_ITEM = 100f;
    public const float MAX_DISTANCE_TO_ITEM_SQR = MAX_DISTANCE_TO_ITEM * MAX_DISTANCE_TO_ITEM;
    public const float NEARBY_REMOVAL_DELAY = 3f; // 3 seconds delay
    public const float REACH_DISTANCE_BUFFER = 0.5f;
    public const int MaxClientBatchItems = 128;
    public const int MaxServerBatchItems = 512;
    private const int MaxQueuedSnapshots = 8192;
    private const int MaxSnapshotsPerTick = 512;
    public float MAX_REACH_DISTANCE = 4f + REACH_DISTANCE_BUFFER;         //from the game, but we should try to look up the value

    //caches for item snapshots
    private List<ItemUpdateData> DestroyedItems = new(64);

    //Item ownership
    //private Dictionary<ushort, PlayerInventory> playerInventories = new Dictionary<ushort, PlayerInventory>();
    //private Dictionary<NetworkedItem, ushort> itemToPlayerMap = new Dictionary<NetworkedItem, ushort>();


    /*
     * Client
     */

    //cache for client-sided items & spawns
    private Dictionary<string, List<NetworkedItem>> CachedItems = new(1024); //Client cached items
    private Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs
    private bool ClientInitialised = false;
    private global::Multiplayer.Networking.Managers.Server.NetworkServer inventoryServer;


    /* 
     * Common
     */
    private readonly BoundedBatchQueue<Tuple<ItemUpdateData, ServerPlayer>> ReceivedSnapshots = new(MaxQueuedSnapshots);

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        inventoryServer = NetworkLifecycle.Instance.Server;
        inventoryServer.PlayerDisconnected += PlayerDisconnected;

        try
        {
            MAX_REACH_DISTANCE = GrabberRaycasterDV.RAYCAST_MAX_DIST + REACH_DISTANCE_BUFFER;
        }
        catch (Exception ex)
        {
            NetworkLifecycle.Instance.Server.LogWarning($"NatworkedItemManager.Awake() Failed to find GrabberRaycasterDV\r\n{ex.Message}");
        }
    }

    protected void Start()
    {
        NetworkLifecycle.Instance.OnTick += Common_OnTick;

        BuildPrefabLookup();
    }

    protected override void OnDestroy()
    {
        if (inventoryServer != null) inventoryServer.PlayerDisconnected -= PlayerDisconnected;
        CancelInventoryRestores();
        ReceivedSnapshots.Clear();
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;

        NetworkLifecycle.Instance.OnTick -= Common_OnTick;
    }

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot, bool permanentlyDestroyed = true)
    {
        if (permanentlyDestroyed)
            foreach (var inventory in offlineInventories.Values) inventory.Remove(netItem);
        DestroyedItems.Add(snapshot);

        foreach(var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            player.RemoveOwnedItem(netItem.NetId);
            if(player.KnownItems.ContainsKey(netItem))
                player.KnownItems.Remove(netItem);

            if(player.NearbyItems.ContainsKey(netItem))
                player.NearbyItems.Remove(netItem);
        }
    }

    public bool ReceiveSnapshots(List<ItemUpdateData> snapshots, ServerPlayer sender)
    {
        if (snapshots == null) return false;
        var batch = snapshots.Select(snapshot => Tuple.Create(snapshot, sender)).ToArray();
        return ReceivedSnapshots.TryEnqueue(batch);

        //Multiplayer.LogDebug(() => $"NetworkItemManager.ReceiveSnapshots() count: {ReceivedSnapshots.Count}, from: ");
    }

    #region Common

    private void Common_OnTick(uint tick)
    {
        if (!NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsServerRunning) return;
        ProcessReceived();
        foreach (var item in NetworkedItem.GetAll().ToArray())
        {
            if (item == null || item.NetId == 0) continue;
            try { item.RefreshRemotePresentation(); }
            catch (Exception ex) { Multiplayer.LogError($"Item presentation failed for {item.NetId}: {ex}"); }
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            UpdatePlayerItemLists();
            ProcessChanged(tick);
        }
        else
        {
            ProcessClientChanges(tick);
        }
    }

    private void ProcessReceived()
    {
        int remaining = MaxSnapshotsPerTick;
        while (remaining-- > 0 && ReceivedSnapshots.TryDequeue(out var snapshotInfo))
        {
            ItemUpdateData snapshot = snapshotInfo.Item1;
            try
            {
                //Multiplayer.LogDebug(() => $"ProcessReceived: {snapshot.UpdateType}");

                if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
                {
                    Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Invalid Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
                    continue;
                }

                if (NetworkLifecycle.Instance.IsHost())
                {
                    ProcessReceivedAsHost(snapshot, snapshotInfo.Item2);
                }
                else
                {
                    ProcessReceivedAsClient(snapshot);
                }
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Error! {ex.Message}\r\n{ex.StackTrace}");
            }
        }
    }

    #endregion

    #region Server

    private void UpdatePlayerItemLists()
    {
        float currentTime = Time.time;

        var allItems = NetworkedItem.GetAll().ToArray();

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            foreach (var item in allItems)
            {
                if (item == null || item.NetId == 0 || !item.CanApplySnapshot)
                {
                    NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Null item found in allItems!");
                    continue;
                }

                Vector3 itemPosition = item.transform.position;
                if (item.OwnerId >= 0 && NetworkLifecycle.Instance.Server.TryGetServerPlayer((byte)item.OwnerId, out var owner))
                    itemPosition = owner.WorldPosition;
                float sqrDistance = (player.WorldPosition - itemPosition).sqrMagnitude;

                if (item.OwnerId == player.PlayerId || sqrDistance <= MAX_DISTANCE_TO_ITEM_SQR)
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Adding for player: {player?.Username}, Nearby Item: {item?.NetId}, {item?.name}");
                    player.NearbyItems[item] = currentTime;
                }
            }

            // Remove items that are no longer nearby
            foreach (var kvp in player.NearbyItems.ToArray())
            {
                if (currentTime - kvp.Value > NEARBY_REMOVAL_DELAY)
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Removing for player: {player?.Username}, Nearby Item: {kvp.Key?.NetId}, {kvp.Key?.name}");
                    player.NearbyItems.Remove(kvp.Key);
                }
            }
        }
    }

    internal List<ItemUpdateData> CaptureInitialWorldItems(ServerPlayer player)
    {
        return NetworkedItem.GetAll().Where(item => item != null && item.NetId != 0 && item.CanApplySnapshot &&
            !DoNotCreateItem(item.TrackedItemType) &&
            (item.OwnerId == player.PlayerId || (item.transform.position - player.WorldPosition).sqrMagnitude <= MAX_DISTANCE_TO_ITEM_SQR))
            .Select(item => item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create))
            .Where(snapshot => snapshot != null).ToList();
    }

    private void ProcessChanged(uint tick)
    {
        var dirtyItems = new Dictionary<ushort, ItemUpdateData>();
        foreach (var item in NetworkedItem.GetAll().ToArray())
        {
            if (item == null || item.NetId == 0 || !item.CanApplySnapshot) continue;
            try
            {
                var snapshot = item.GetSnapshot();
                if (snapshot != null) dirtyItems[item.NetId] = snapshot;
            }
            catch (Exception ex) { Multiplayer.LogError($"Item snapshot failed for {item.NetId}: {ex}"); }
        }
        bool allDelivered = true;
        var destroyedIds = new HashSet<ushort>(DestroyedItems.Select(item => item.ItemNetId));
        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers.ToArray())
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems) continue;
            // Tombstones precede new creates, including when an ID has already been reused.
            var updates = new List<ItemUpdateData>(DestroyedItems);
            var revisions = new Dictionary<NetworkedItem, ulong>();
            var forgotten = new List<NetworkedItem>();
            foreach (var known in player.KnownItems.Keys.ToArray())
            {
                if (known == null)
                {
                    forgotten.Add(known);
                    continue;
                }
                if (!player.NearbyItems.ContainsKey(known) && !DoNotCreateItem(known.TrackedItemType))
                {
                    updates.Add(new ItemUpdateData { ItemNetId = known.NetId, UpdateType = ItemUpdateData.ItemUpdateType.Destroy });
                    forgotten.Add(known);
                }
            }
            foreach (var item in player.NearbyItems.Keys.ToArray())
            {
                if (item == null || item.NetId == 0 || !item.CanApplySnapshot) continue;
                ItemUpdateData snapshot = null;
                bool known = player.KnownItems.TryGetValue(item, out var revision) && !destroyedIds.Contains(item.NetId);
                if (!known)
                {
                    if (!DoNotCreateItem(item.TrackedItemType))
                        snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
                }
                else if (revision != item.Revision)
                {
                    if (!dirtyItems.TryGetValue(item.NetId, out snapshot))
                        snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
                }
                if (snapshot != null) updates.Add(snapshot);
                else if (!known && !DoNotCreateItem(item.TrackedItemType)) continue;
                revisions[item] = item.Revision;
            }
            try
            {
                if (updates.Count > 0)
                    NetworkLifecycle.Instance.Server.SendItemsChangePacket(updates, player);
                foreach (var item in forgotten) player.KnownItems.Remove(item);
                foreach (var entry in revisions) player.KnownItems[entry.Key] = entry.Value;
            }
            catch (Exception ex)
            {
                allDelivered = false;
                Multiplayer.LogError($"Item delivery failed for player {player.PlayerId}: {ex}");
            }
        }
        if (allDelivered) DestroyedItems.Clear();
    }

    private void ProcessReceivedAsHost(ItemUpdateData snapshot, ServerPlayer player)
    {
        if (snapshot == null || player == null ||
            !NetworkLifecycle.Instance.Server.TryGetServerPlayer(player.PlayerId, out var connectedPlayer) ||
            connectedPlayer != player || player.LoadingState != PlayerLoadingState.Complete)
            return;

        if ((snapshot.UpdateType & (ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy)) != 0)
        {
            NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() Host received Create snapshot! ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            return;
        }

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem))
        {
            if (ValidatePlayerAction(snapshot, player)) //Ensure the player can do this
            {
                // Drops and attachments do not carry Player on the wire.
                // Never use the packet's default value as the acting identity.
                snapshot.Player = player.PlayerId;
                NetworkLifecycle.Instance.Server.LogWarning($"NetworkedItemManager.ProcessReceivedAsHost() ItemNetId: {snapshot.ItemNetId}, snapshot type: {snapshot.UpdateType}");
                netItem.ReceiveSnapshot(snapshot);
            }
            else
            {
                NetworkLifecycle.Instance.Server.LogWarning($"NetworkedItemManager.ProcessReceivedAsHost() Player action validation failed for ItemNetId: {snapshot.ItemNetId}");
                // Include native host interactions that have not reached the normal sampling pass yet.
                netItem.GetSnapshot();
                var correction = netItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
                if (correction != null)
                    NetworkLifecycle.Instance.Server.SendItemsChangePacket(new List<ItemUpdateData> { correction }, player);
            }
        }
        else
        {
            NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() NetworkedItem not found! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }

    private bool ValidatePlayerAction(ItemUpdateData snapshot, ServerPlayer player)
    {
        if (!NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem networkedItem) ||
            networkedItem == null || !networkedItem.CanApplySnapshot)
            return false;
        if (snapshot.States != null && snapshot.States.Keys.Any(ItemInventoryLocation.IsKey))
        {
            if (!ItemInventoryLocation.TryRead(snapshot.States, out var location)) return false;
            if (snapshot.ItemState == ItemState.InInventory || snapshot.ItemState == ItemState.InHand)
            {
                if (location.Slot >= DV.InventorySystem.Inventory.Instance.GetItemsArray().Length) return false;
                if (location.ContainerId != null)
                {
                    var container = DV.InventorySystem.Inventory.Instance.ItemContainerRegistry.GetContainer(location.ContainerId);
                    var holder = container != null ? container.GetComponentInParent<NetworkedItem>() : null;
                    if (holder == null || holder == networkedItem || holder.OwnerId != player.PlayerId ||
                        location.ContainerSlot >= container.Capacity) return false;
                }
                foreach (var itemId in player.OwnedItems)
                {
                    if (itemId == snapshot.ItemNetId || !NetworkedItem.TryGet(itemId, out var other)) continue;
                    var occupied = other.InventoryLocation;
                    if (location.ContainerId != null && occupied.ContainerId == location.ContainerId && occupied.ContainerSlot == location.ContainerSlot)
                        return false;
                    if (location.ContainerId == null && location.Slot >= 0 && occupied.ContainerId == null && occupied.Slot == location.Slot)
                        return false;
                }
            }
        }
        byte? ownerId = networkedItem.OwnerId >= 0 ? (byte?)networkedItem.OwnerId : null;
        // The host's locally held items may not yet have produced a network snapshot.
        if (networkedItem.Item.IsGrabbed() || StorageController.Instance.StorageInventory.ContainsItem(networkedItem.Item))
            ownerId = NetworkLifecycle.Instance.Server.SelfId;

        float destinationDistance = Vector3.Distance(player.AbsoluteWorldPosition, snapshot.ItemPosition);
        bool validAttachment = false;
        if (snapshot.ItemState == ItemState.Attached &&
            NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar))
        {
            var snapPoint = trainCar?.physicsLod?.GetCouplerSnapPoints()
                .FirstOrDefault(point => point.IsFront == snapshot.AttachedFront);
            validAttachment = snapPoint != null && networkedItem.Item.SnappableItem != null;
            if (snapPoint != null)
                destinationDistance = Vector3.Distance(player.WorldPosition, snapPoint.transform.position);
        }
        return ItemActionPolicy.CanApply(snapshot, player.PlayerId, ownerId,
            Vector3.Distance(player.WorldPosition, networkedItem.transform.position), MAX_REACH_DISTANCE,
            destinationDistance, validAttachment);
    }

    private bool GetItemOwner(ushort itemNetId, out ServerPlayer owner)
    {
        owner = NetworkLifecycle.Instance.Server.ServerPlayers.FirstOrDefault(p => p.OwnsItem(itemNetId));
        return owner != null;
    }
    #endregion

    #region Client

    private void ProcessClientChanges(uint tick)
    {
        List<ItemUpdateData> changedItems = new List<ItemUpdateData>();

        if(!ClientInitialised)
            return;

        foreach (var item in NetworkedItem.GetAll().ToArray())
        {
            if (item == null || item.NetId == 0 || !item.CanApplySnapshot) continue;
            try
            {
                ItemUpdateData snapshot = item.GetSnapshot();
                if (snapshot != null) changedItems.Add(snapshot);
            }
            catch (Exception ex) { Multiplayer.LogError($"Client item snapshot failed for {item.NetId}: {ex}"); }
        }

        if (changedItems.Count > 0)
        {
            NetworkLifecycle.Instance.Client.SendItemsChangePacket(changedItems);
        }
    }

    private void ProcessReceivedAsClient(ItemUpdateData snapshot)
    {
        NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem);

        NetworkLifecycle.Instance.Client.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            // A repeated creation is an authoritative refresh, not a second object.
            bool sameIdentity = snapshot.States == null ||
                !snapshot.States.TryGetValue(PlayerInventorySaveCodec.IdentityKey, out var identity) ||
                (identity is string idText && Guid.TryParse(idText, out var id) && netItem != null && netItem.PersistentId == id);
            if (netItem != null && sameIdentity && netItem.Item?.InventorySpecs?.ItemPrefabName == snapshot.PrefabName)
                netItem.ReceiveSnapshot(snapshot);
            else
            {
                if (netItem != null) SendToCache(netItem);
                CreateItem(snapshot);
            }
        }
        else if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            SendToCache(netItem);
            NetworkLifecycle.Instance.Client.NotifyInitialWorldItemApplied(snapshot.ItemNetId);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }
    #endregion

    #region Item Cache And Management
    private void CreateItem(ItemUpdateData snapshot)
    {
        if(snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return;
        }

        if (snapshot.States != null && snapshot.States.TryGetValue(PlayerInventorySaveCodec.IdentityKey, out var identity))
        {
            if (identity is not string text || !Guid.TryParse(text, out var persistentId) || persistentId == Guid.Empty)
                throw new FormatException("Invalid persistent identity in item creation.");
            var restored = NetworkedItem.FindPersistentItem(persistentId);
            if (restored != null)
            {
                if (restored.Item?.InventorySpecs?.ItemPrefabName != snapshot.PrefabName)
                    throw new InvalidOperationException("Restored item identity has a different prefab.");
                restored.NetId = snapshot.ItemNetId;
                restored.ReceiveSnapshot(snapshot);
                return;
            }
        }
        NetworkedItem newItem = GetFromCache(snapshot.PrefabName);

        if(newItem == null)
        {
            //GameObject prefabObj = Resources.Load(snapshot.PrefabName) as GameObject;
            
            if (!ItemPrefabs.TryGetValue(snapshot.PrefabName, out InventoryItemSpec spec))
            {
                Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
                return;
            }

            //create a new item
            GameObject gameObject = Instantiate(spec.gameObject, snapshot.ItemPosition + WorldMover.currentMove, snapshot.ItemRotation);

            //Make sure we have a NetworkedItem
            newItem = gameObject.GetOrAddComponent<NetworkedItem>();
        }

        newItem.NetId = snapshot.ItemNetId;
        newItem.BeginReplica();
        newItem.gameObject.SetActive(true);

        newItem.ReceiveSnapshot(snapshot);
    }

    private void BuildPrefabLookup()
    {
        Multiplayer.LogDebug(() => $"BuildPrefabLookup()");

        foreach (var item in Globals.G.Items.items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
            {
                ItemPrefabs[item.itemPrefabName] = item;
            }
        }
    }
    public void CacheWorldItems()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        // Remove all spawned world items and place them into a cache for later use
        foreach (var item in NetworkedItem.GetAll().ToArray())
        {
            try
            {
                if (item.Item != null && !item.Item.IsEssential() && !item.Item.IsGrabbed() &&
                    !StorageController.Instance.StorageInventory.ContainsItem(item.Item) &&
                    !StorageController.Instance.IsInStorageLostAndFound(item.Item) &&
                    string.IsNullOrEmpty(DV.InventorySystem.Inventory.Instance.ItemContainerRegistry.GetItemContainerIdAndIndex(item.gameObject).ContainerId))
                {
                    SendToCache(item);
                }
                //else
                //{
                //    NetworkLifecycle.Instance.Client.LogDebug(() => $"CacheWorldItems() Not caching: {item.Item.InventorySpecs.previewPrefab} is in Inventory: {StorageController.Instance.StorageInventory.ContainsItem(item.Item)}");
                //}
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"Error Caching Spawned Item: {ex.Message}");
            }
        }

        ClientInitialised = true;
    }

    private NetworkedItem GetFromCache(string prefabName)
    {
        if (CachedItems.TryGetValue(prefabName, out var items))
        {
            while (items.Count > 0)
            {
                var cachedItem = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (cachedItem != null && cachedItem.Item != null) return cachedItem;
            }
            CachedItems.Remove(prefabName);
        }

        return null;
    }

    private void SendToCache(NetworkedItem netItem)
    {
        string prefabName = netItem?.Item?.InventorySpecs?.itemPrefabName;
        if (netItem == null || string.IsNullOrEmpty(prefabName)) return;
        if (CachedItems.TryGetValue(prefabName, out var cached) && cached.Contains(netItem)) return;

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}");

        netItem.ResetForCache();
        netItem.gameObject.SetActive(false);
        RespawnOnDrop respawn = netItem.Item.GetComponent<RespawnOnDrop>();

        Destroy(respawn);

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}: checkWhileDisabled {respawn.checkWhileDisabled}, ignoreDistanceFromSpawnPosition {respawn.ignoreDistanceFromSpawnPosition}, respawnOnDropThroughFloor {respawn.respawnOnDropThroughFloor}");

        //respawn.checkWhileDisabled = false;
        //respawn.ignoreDistanceFromSpawnPosition = true;
        //respawn.respawnOnDropThroughFloor = false;

        if (SingletonBehaviour<StorageController>.Instance.StorageWorld.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageInventory.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageLostAndFound.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        netItem.Item.InventorySpecs.BelongsToPlayer = false;
        netItem.NetId = 0;
        
        if (!CachedItems.ContainsKey(prefabName))
        {
            CachedItems[prefabName] = new List<NetworkedItem>();
        }
        CachedItems[prefabName].Add(netItem);
    }

    #endregion

    public bool DoNotCreateItem(Type itemType)
    {
        if (
            itemType == typeof(JobOverview) ||
            itemType == typeof(JobBooklet) ||
            itemType == typeof(JobReport) ||
            itemType == typeof(JobExpiredReport) ||
            itemType == typeof(JobMissingLicenseReport)
           )
        {
            return true;
        }

            return false;
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedItemManager)}]";
    }
}
