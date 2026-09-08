using DV.CabControls;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

public class NetworkedItem : IdMonoBehaviour<ushort, NetworkedItem>
{
    #region Lookup Cache
    private static readonly Dictionary<ItemBase, NetworkedItem> itemBaseToNetworkedItem = new(4096);

    public static Dictionary<ItemBase, NetworkedItem>.ValueCollection GetAll() => itemBaseToNetworkedItem.Values;
    
    public static bool Get(ushort netId, out NetworkedItem obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool TryGet(ushort netId, out NetworkedItem obj)
    {
        bool b = TryGet(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool GetItem(ushort netId, out ItemBase obj)
    {
        bool b = Get(netId, out NetworkedItem networkedItem);
        obj = b ? networkedItem.Item : null;
        return b;
    }

    public static bool TryGetNetworkedItem(ItemBase item, out NetworkedItem networkedItem)
    {
        return itemBaseToNetworkedItem.TryGetValue(item, out networkedItem);
    }

    public static bool TryGetNetId(ItemBase item, out ushort netID)
    {
        if (itemBaseToNetworkedItem.TryGetValue(item, out var networkedItem))
        {
            netID = networkedItem.NetId;
            return true;
        }

        netID = 0;
        return false;
    }
    #endregion

    private const float PositionThreshold = 0.1f;
    private const float RotationThreshold = 0.1f;

    public ItemBase Item { get; private set; }
    private GrabHandlerItem grabHandler;
    private SnappableItem snappableItem;
    private Component trackedItem;
    private List<object> trackedValues = new List<object>();
    public bool UsefulItem { get; private set; } = false;
    public Type TrackedItemType { get; private set; }
    public uint LastDirtyTick { get; private set; }
    private bool initialised;
    private bool registrationComplete = false;
    private Queue<ItemUpdateData> pendingSnapshots = new Queue<ItemUpdateData>();

    //Track dirty states
    private bool createdDirty = true;   //if set, we created this item dirty and have not sent an update
    private ItemState lastState;
    private bool stateDirty;
    private bool authoritativeDirty;
    private NetworkedPlayer presentationOwner;
    private ItemState presentedState;
    private Vector3 lastPosition;
    private Quaternion lastRotation = Quaternion.identity;
    public ulong Revision { get; private set; }
    public Guid PersistentId { get; private set; } = Guid.NewGuid();
    public bool IsCached { get; private set; }
    public Guid? DormantOwner { get; private set; }
    public ItemInventoryLocation InventoryLocation { get; private set; } = ItemInventoryLocation.Unplaced;
    public bool RegistrationComplete => registrationComplete && Item != null;

    public static NetworkedItem FindPersistentItem(Guid id) => GetAll().FirstOrDefault(item =>
        item != null && !item.IsCached && item.PersistentId == id);

    public void RestorePersistentIdentity(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An item identity cannot be empty.");
        var existing = FindPersistentItem(id);
        if (existing != null && existing != this)
            throw new InvalidOperationException($"Duplicate persistent item identity: {id}");
        PersistentId = id;
    }

    public void BeginReplica() => IsCached = false;
    private bool IsRemoteOwned => OwnerId >= 0 && OwnerId != (NetworkLifecycle.Instance.Client?.PlayerId ?? -1);
    private bool wasThrown;

    private Vector3 thrownPosition;
    private Quaternion thrownRotation;
    private Vector3 throwDirection;

    //Handle ownership
    public int OwnerId { get; private set; } = -1; // All byte player IDs, including 0, are valid.
    public bool CanApplySnapshot => RegistrationComplete && !DormantOwner.HasValue;

    //public void SetOwner(ushort playerId)
    //{
    //    if (OwnerId != playerId)
    //    {
    //        if (OwnerId != 0)
    //        {
    //            NetworkedItemManager.Instance.RemoveItemFromPlayerInventory(this);
    //        }
    //        OwnerId = playerId;
    //        if (playerId != 0)
    //        {
    //            NetworkedItemManager.Instance.AddItemToPlayerInventory(playerId, this);
    //        }
    //    }
    //}

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();
        //Multiplayer.LogDebug(() => $"NetworkedItem.Awake() {name}");
        NetworkedItemManager.Instance.CheckInstance(); //Ensure the NetworkedItemManager is initialised

        Register();
    }

    protected void Start()
    {
        if (!initialised)
            Register();

        // Mark registration as complete for items that don't need tracked values
        if (!registrationComplete && !UsefulItem)
            FinaliseTrackedValues();
    }

    public T GetTrackedItem<T>() where T : Component
    {
        return UsefulItem ? trackedItem as T : null;
    }

    public void Initialize<T>(T item, ushort netId = 0, bool createDirty = true) where T : Component
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.Initialize<{typeof(T)}>(netId: {netId}, name: {name}, createDirty: {createdDirty})");

        if (netId != 0)
            NetId = netId;

        trackedItem = item;
        TrackedItemType = typeof(T);
        UsefulItem = true;

        createdDirty = createDirty;

        if (Item == null)
            Register();

    }

    private bool Register()
    {
        if (initialised)
            return false;

        try
        {

            if (!TryGetComponent(out ItemBase itemBase))
            {
                Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}");
                return false;
            }

            Item = itemBase;
            itemBaseToNetworkedItem[Item] = this;

            Item.Grabbed += OnGrabbed;
            Item.Ungrabbed += OnUngrabbed;

            //Find special interaction components
            TryGetComponent<GrabHandlerItem>(out grabHandler);
            TryGetComponent<SnappableItem>(out snappableItem);

            lastState = GetItemState();
            stateDirty = false;

            initialised = true;
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}\r\n{ex.Message}");
            return false;
        }
    }

    private void OnUngrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnUngrabbed() NetID: {NetId}, {name}");
        stateDirty = true;
    }

    private void OnGrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnGrabbed() NetID: {NetId}, {name}");
        stateDirty = true;
    }

    public void OnThrow(Vector3 direction)
    {
        //block a received throw from 
        if (wasThrown)
        {
            wasThrown = false;
            return;
        }

        throwDirection = direction;
        thrownPosition = Item.transform.position - WorldMover.currentMove;
        thrownRotation = Item.transform.rotation;

        //Multiplayer.LogDebug(() => $"NetworkedItem.OnThrow() netId: {NetId}, Name: {name}, Raw Position: {Item.transform.position}, Position: {thrownPosition}, Rotation: {thrownRotation}, Direction: {throwDirection}");

        wasThrown = true;
        stateDirty = true;
    }


    #region Item Value Tracking
    public void RegisterTrackedValue<T>(string key, Func<T> valueGetter, Action<T> valueSetter, Func<T, T, bool> thresholdComparer = null, bool serverAuthoritative = false)
    {
        if (key == PlayerInventorySaveCodec.IdentityKey || ItemInventoryLocation.IsKey(key) || trackedValues.Count >= ItemUpdateData.MaxTrackedValues - 5)
            throw new ArgumentException("Reserved item key or tracked value limit exceeded.");
        //Multiplayer.LogDebug(() => $"NetworkedItem.RegisterTrackedValue(\"{key}\", {valueGetter != null}, {valueSetter != null}, {thresholdComparer != null}, {serverAuthoritative}) itemNetId {NetId}, item name: {name}");
        trackedValues.Add(new TrackedValue<T>(key, valueGetter, valueSetter, thresholdComparer, serverAuthoritative));
    }

    public void FinaliseTrackedValues()
    {
        if (Item == null && !Register())
            return;

        registrationComplete = true;

        while (pendingSnapshots.Count > 0)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}. Dequeuing");
            ApplySnapshot(pendingSnapshots.Dequeue());
        }

    }

    private bool HasDirtyValues()
    {
        //clients should only send values that are not server authoritative
        if (!NetworkLifecycle.Instance.IsHost())
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty && !((dynamic)tv).ServerAuthoritative);
        else
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty);
    }

    private Dictionary<string, object> GetDirtyStateData()
    {
        var dirtyData = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            if (((dynamic)trackedValue).IsDirty)
            {
                dirtyData[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
            }
        }
        return dirtyData;
    }
    private Dictionary<string, object> GetAllStateData()
    {
        var data = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            data[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
        }
        return data;
    }

    private void MarkValuesClean()
    {
        foreach (var trackedValue in trackedValues)
        {
            ((dynamic)trackedValue).MarkClean();
        }
    }

    #endregion

    public ItemUpdateData GetSnapshot()
    {
        if (NetId == 0 || !CanApplySnapshot)
            return null;
        bool host = NetworkLifecycle.Instance.IsHost();
        // Hidden remote inventory/hand objects must not emit local state or tracked-value echoes.
        if (!host && IsRemoteOwned)
            return null;

        ItemState currentState = authoritativeDirty ? lastState : GetItemState();
        bool hasDirtyVals = HasDirtyValues();
        bool locationChanged = false;
        if (!IsRemoteOwned && (currentState == ItemState.InInventory || currentState == ItemState.InHand))
        {
            int slot = Inventory.Instance.IndexOf(gameObject);
            var (containerId, containerSlot) = Inventory.Instance.ItemContainerRegistry.GetItemContainerIdAndIndex(gameObject);
            var location = new ItemInventoryLocation(slot, containerId, containerSlot,
                slot >= 0 && Inventory.Instance.GetSlotLockState(slot));
            locationChanged = !InventoryLocation.Equals(location);
            InventoryLocation = location;
        }
        bool moved = host && (currentState == ItemState.Dropped || currentState == ItemState.Thrown) &&
            ((transform.position - WorldMover.currentMove - lastPosition).sqrMagnitude > PositionThreshold * PositionThreshold ||
             Quaternion.Angle(transform.rotation, lastRotation) > RotationThreshold);
        var updateType = ItemUpdateData.ItemUpdateType.None;
        if (createdDirty && host)
            updateType = ItemUpdateData.ItemUpdateType.Create;
        else if (authoritativeDirty)
            updateType = ItemUpdateData.ItemUpdateType.FullSync;
        else
        {
            if (lastState != currentState || stateDirty)
                updateType |= ItemUpdateData.ItemUpdateType.ItemState;
            if (hasDirtyVals || locationChanged || (stateDirty && (currentState == ItemState.InHand || currentState == ItemState.InInventory)))
                updateType |= ItemUpdateData.ItemUpdateType.ObjectState;
            if (moved)
                updateType |= ItemUpdateData.ItemUpdateType.ItemPosition;
        }
        if (updateType == ItemUpdateData.ItemUpdateType.None)
            return null;

        if (!authoritativeDirty && !IsRemoteOwned)
            SetOwner(currentState == ItemState.InHand || currentState == ItemState.InInventory
                ? (NetworkLifecycle.Instance.Client?.PlayerId ?? -1) : -1);
        lastState = currentState;
        var snapshot = CreateUpdateData(updateType);
        if (snapshot == null) return null;
        LastDirtyTick = NetworkLifecycle.Instance.Tick;
        Revision++;
        lastPosition = transform.position - WorldMover.currentMove;
        lastRotation = transform.rotation;
        createdDirty = stateDirty = authoritativeDirty = wasThrown = false;
        MarkValuesClean();
        return snapshot;
    }

    private void SetOwner(int ownerId)
    {
        if (OwnerId == ownerId) return;
        ReleasePresentation();
        if (NetworkLifecycle.Instance.IsHost())
        {
            var server = NetworkLifecycle.Instance.Server;
            if (OwnerId >= 0 && server.TryGetServerPlayer((byte)OwnerId, out var previous))
                previous.RemoveOwnedItem(NetId);
            if (ownerId >= 0 && server.TryGetServerPlayer((byte)ownerId, out var next))
                next.AddOwnedItem(NetId);
        }
        OwnerId = ownerId;
    }

    public void ResetForCache()
    {
        pendingSnapshots.Clear();
        ReleasePresentation();
        SetOwner(-1);
        createdDirty = stateDirty = authoritativeDirty = wasThrown = false;
        IsCached = true;
        DormantOwner = null;
        InventoryLocation = ItemInventoryLocation.Unplaced;
        PersistentId = Guid.NewGuid();
    }

    public void SuspendInventory(Guid owner)
    {
        try
        {
            ReleasePresentation();
            SetOwner(-1);
        }
        finally
        {
            // Never leave a disconnected player's reusable session ID on an item after a callback failure.
            OwnerId = -1;
            DormantOwner = owner;
            lastState = ItemState.InInventory;
            gameObject.SetActive(false);
        }
        StorageController.Instance.RemoveItemFromStorageItemList(Item);
        transform.SetParent(WorldMover.OriginShiftParent, true);
    }

    public void StageInventory(Guid owner)
    {
        DormantOwner = owner;
        if (Item?.ItemRigidbody != null) Item.ItemRigidbody.isKinematic = true;
    }

    public void RestoreInventoryOwner(ServerPlayer player, PlayerItemSaveData saved)
    {
        if (!RegistrationComplete) throw new InvalidOperationException("Inventory item is not initialized.");
        DormantOwner = null;
        InventoryLocation = new ItemInventoryLocation(saved.InventorySlotIndex, saved.ContainerId,
            saved.ContainerSlotIndex, saved.InLockedSlot);
        SetOwner(player.PlayerId);
        lastState = ItemState.InInventory;
        authoritativeDirty = true;
        StorageController.Instance.RemoveItemFromStorageItemList(Item);
        RefreshRemotePresentation();
    }

    public PlayerItemSaveData CaptureInventory()
    {
        if (!RegistrationComplete) throw new InvalidOperationException($"Cannot save uninitialized item {NetId}.");
        var state = GetComponent<ItemSaveData>()?.SaveItemData() ?? new Newtonsoft.Json.Linq.JObject();
        state = (Newtonsoft.Json.Linq.JObject)state.DeepClone();
        state[PlayerInventorySaveCodec.IdentityKey] = PersistentId.ToString("N");
        return new PlayerItemSaveData
        {
            ItemPrefabName = Item.InventorySpecs.ItemPrefabName, NetId = NetId,
            ItemRotationW = 1, BelongsToPlayer = Item.InventorySpecs.BelongsToPlayer,
            IsGrabbed = false, InventorySlotIndex = InventoryLocation.Slot,
            ContainerId = InventoryLocation.ContainerId, ContainerSlotIndex = InventoryLocation.ContainerSlot,
            InLockedSlot = InventoryLocation.Locked, State = state
        };
    }

    public void ReceiveSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
            return;

        if (!registrationComplete)
        {
            Multiplayer.Log($"NetworkedItem.ReceiveSnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}. Queuing");
            pendingSnapshots.Enqueue(snapshot);
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(ItemUpdateData snapshot)
    {
        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
        {
            Multiplayer.Log($"NetworkedItem.ApplySnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, Active state: {gameObject.activeInHierarchy}");

            switch (snapshot.ItemState)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    HandleDroppedOrThrownState(snapshot);
                    break;

                case ItemState.InHand:
                case ItemState.InInventory:
                    HandleInventoryOrHandState(snapshot);
                    break;

                case ItemState.Attached:
                    if (!HandleAttachedState(snapshot))
                        return;
                    break;

                default:
                    throw new Exception($"NetworkedItem.ApplySnapshot() Item state not implemented: {snapshot?.ItemState}");

            }
        }

        Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} About to process states");

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, States: {snapshot?.States?.Count}");

            if (snapshot.States != null)
            {
                ApplyTrackedValues(snapshot.States);
            }
        }

        Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} states processed");

        //mark values as clean
        if ((snapshot.UpdateType & (ItemUpdateData.ItemUpdateType.ItemState | ItemUpdateData.ItemUpdateType.Create)) != 0)
            lastState = snapshot.ItemState;
        createdDirty = false;
        stateDirty = false;

        MarkValuesClean();
        lastPosition = transform.position - WorldMover.currentMove;
        lastRotation = transform.rotation;
        authoritativeDirty = NetworkLifecycle.Instance.IsHost();
        if (!authoritativeDirty && snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
            NetworkLifecycle.Instance.Client?.NotifyInitialWorldItemApplied(snapshot.ItemNetId);
        return;
    }

    public ItemUpdateData CreateUpdateData(ItemUpdateData.ItemUpdateType updateType)
    {
        if (transform == null || Item == null || Item?.InventorySpecs == null || Item?.InventorySpecs?.ItemPrefabName == null)
        {
            Multiplayer.LogDebug(()=>$"NetworkedItem.CreateUpdateData({updateType}) NetId: {NetId}, name: {name}. Transform is null: {transform == null}, Item is null: {Item == null}, Inventory Specs: {Item?.InventorySpecs == null}, ItemPrefabName is null: {Item?.InventorySpecs?.ItemPrefabName == null}");
            return null;
        }

        Vector3 position;
        Quaternion rotation;
        Dictionary<string, object> states;
        ushort carId = 0;
        bool frontCoupler = true;

        if (wasThrown)
        {
            position = thrownPosition;
            rotation = thrownRotation;
        }
        else
        {
            position = transform.position - WorldMover.currentMove;
            rotation = transform.rotation;
        }

        if (updateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || updateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync))
        {
            states = GetAllStateData();
            states[PlayerInventorySaveCodec.IdentityKey] = PersistentId.ToString("N");
        }
        else
        {
            states = GetDirtyStateData();
        }

        if (lastState == ItemState.Attached)
        {
            ItemSnapPointCoupler itemSnapPointCoupler = snappableItem.SnappedTo as ItemSnapPointCoupler;

            if (itemSnapPointCoupler != null)
            {
                carId = itemSnapPointCoupler.Car.GetNetId();
                frontCoupler = itemSnapPointCoupler.IsFront;
            }
        }

        if ((updateType & (ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.ObjectState)) != 0)
            InventoryLocation.Write(states);

        var updateData = new ItemUpdateData
        {
            UpdateType = updateType,
            ItemNetId = NetId,
            PrefabName = Item.InventorySpecs.ItemPrefabName,
            ItemState = lastState,
            Player = OwnerId >= 0 ? (byte)OwnerId : (NetworkLifecycle.Instance.Client?.PlayerId ?? 0),
            ItemPosition = position,
            ItemRotation = rotation,
            ThrowDirection = throwDirection,
            CarNetId = carId,
            AttachedFront = frontCoupler,
            States = states,
        };

        return updateData;
    }

    private ItemState GetItemState()
    {
        //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}, isGrabbed: {Item.IsGrabbed()} Inventory.Contains(): {Inventory.Instance.Contains(this.gameObject, false)} Storage.Contains: {StorageController.Instance.StorageInventory.ContainsItem(Item)}");


        if (IsRemoteOwned && (lastState == ItemState.InHand || lastState == ItemState.InInventory))
            return lastState;

        if (wasThrown)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Thrown;
        }

        if (Item.IsGrabbed())
            return ItemState.InHand;

        if (StorageController.Instance.IsInStorageLostAndFound(Item) || Inventory.Instance.Contains(this.gameObject, false) ||
            !string.IsNullOrEmpty(Inventory.Instance.ItemContainerRegistry.GetItemContainerIdAndIndex(gameObject).ContainerId))
            return ItemState.InInventory;

        if (snappableItem != null && snappableItem.IsSnapped)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, snapped! {this.transform.parent}");
            return ItemState.Attached;
        }

        //do we need a condition to check if it's attached to something else (last attach vs current attach)?
        return ItemState.Dropped;

    }

    private void ApplyTrackedValues(Dictionary<string, object> newValues)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Null checks");

        if (newValues == null || newValues.Count == 0)
            return;


        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Registration complete: {registrationComplete}");

        if (newValues.Keys.Any(ItemInventoryLocation.IsKey))
        {
            if (!ItemInventoryLocation.TryRead(newValues, out var location))
                throw new FormatException("Invalid item inventory metadata.");
            InventoryLocation = location;
        }
        foreach (var newValue in newValues)
        {
            if (ItemInventoryLocation.IsKey(newValue.Key)) continue;
            if (newValue.Key == PlayerInventorySaveCodec.IdentityKey)
            {
                if (!NetworkLifecycle.Instance.IsHost())
                {
                    if (newValue.Value is not string identity || !Guid.TryParse(identity, out var id))
                        throw new FormatException("Invalid persistent item identity in snapshot.");
                    RestorePersistentIdentity(id);
                }
                continue;
            }
            var trackedValue = trackedValues.Find(tv => ((dynamic)tv).Key == newValue.Key);
            if (trackedValue != null)
            {
                if (!NetworkLifecycle.Instance.IsHost() || !((dynamic)trackedValue).ServerAuthoritative)
                {
                    try
                    {
                        ((dynamic)trackedValue).SetValueFromObject(newValue.Value);
                        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}, Updated tracked value: {newValue.Key}, value: {newValue.Value} ");
                    }
                    catch (Exception ex)
                    {
                        Multiplayer.LogError($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Error updating tracked value {newValue.Key}: {ex.Message}");
                    }
                }
                else
                {
                    Multiplayer.LogWarning($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Skipped server-authoritative value update from client: {newValue.Key}");
                }
            }
            else
            {
                Multiplayer.LogWarning($"Tracked value not found: {newValue.Key}\r\n {String.Join(", ", trackedValues.Select(val => ((dynamic)val).Key))}");
            }
        }
    }

    #region Item State Update Handlers

    private void HandleDroppedOrThrownState(ItemUpdateData snapshot)
    {
        //resolve attachment
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        //resolve ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        ReleasePresentation();
        if (Item.IsGrabbed() || Inventory.Instance.Contains(gameObject, false))
            Inventory.Instance.DropItemFromHandsOrInventory(gameObject);
        grabHandler?.TogglePhysics(true);
        //activate and relocate item
        transform.SetParent(WorldMover.OriginShiftParent, true);
        gameObject.SetActive(true);
        transform.position = snapshot.ItemPosition + WorldMover.currentMove;
        transform.rotation = snapshot.ItemRotation;
        SetOwner(-1);
        if (Item.InventorySpecs.BelongsToPlayer && !StorageController.Instance.StorageWorld.ContainsItem(Item))
            StorageController.Instance.AddItemToWorldStorage(Item);

        //handle throwing of the item
        if (snapshot.ItemState == ItemState.Thrown)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Thrown. Position: {transform.position}, Direction: {snapshot?.ThrowDirection}");

            throwDirection = snapshot.ThrowDirection;
            thrownPosition = snapshot.ItemPosition;
            thrownRotation = snapshot.ItemRotation;
            wasThrown = true;
            grabHandler?.Throw(snapshot.ThrowDirection);
        }
        else
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Dropped. Position: {transform.position}");
        }
    }

    private bool HandleAttachedState(ItemUpdateData snapshot)
    {
        //handle attaching the item
        gameObject.SetActive(true);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId} attempting attachment to car {snapshot.CarNetId}, at the front {snapshot.AttachedFront}");

        if (!NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() CarNetId: {snapshot?.CarNetId} not found for ItemNetId: {snapshot?.ItemNetId}");
            return false;
        }

        //Try to find the coupler snap point for the car and correct end to snap to
        var snapPoint = trainCar?.physicsLod?.GetCouplerSnapPoints()
            .FirstOrDefault(sp => sp.IsFront == snapshot.AttachedFront);

        if (snapPoint == null)
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId}. No valid snap point found for car {snapshot.CarNetId}");
            return false;
        }

        //Attempt attachment to car
        ReleasePresentation();
        if (Item.IsGrabbed() || Inventory.Instance.Contains(gameObject, false))
            Inventory.Instance.DropItemFromHandsOrInventory(gameObject);
        Item.ItemRigidbody.isKinematic = false;
        if (!snapPoint.SnapItem(Item, false))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() Attachment failed for item {snapshot?.ItemNetId} to car {snapshot.CarNetId}");
            return false;
        }

        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);
        SetOwner(-1);
        return true;
    }

    private void HandleInventoryOrHandState(ItemUpdateData snapshot)
    {
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && !player.OwnsItem(NetId))
                player.AddOwnedItem(NetId);

        SetOwner(snapshot.Player);
        Item.InventorySpecs.BelongsToPlayer = true;

        if (snapshot.Player == NetworkLifecycle.Instance.Client?.PlayerId)
        {
            // An acknowledgement must not hide the item in the local player's hands.
            if (snapshot.ItemState == ItemState.InHand && Item.IsGrabbed()) return;
            if (InventoryLocation.Slot < 0 && StorageController.Instance.IsInStorageLostAndFound(Item)) return;
            if (!string.IsNullOrEmpty(Inventory.Instance.ItemContainerRegistry.GetItemContainerIdAndIndex(gameObject).ContainerId)) return;
            if (!Inventory.Instance.Contains(gameObject, false))
            {
                Item.InventorySpecs.BelongsToPlayer = true;
                if (Inventory.Instance.AddItemToInventory(gameObject) < 0)
                    throw new InvalidOperationException($"Unable to restore owned item {NetId} to local inventory.");
            }
            return;
        }
        if (Item.IsGrabbed() || Inventory.Instance.Contains(gameObject, false))
            Inventory.Instance.DropItemFromHandsOrInventory(gameObject);
        StorageController.Instance.RemoveItemFromStorageItemList(Item);
        lastState = snapshot.ItemState;
        RefreshRemotePresentation();
    }
    private void ReleasePresentation()
    {
        if (presentationOwner != null) presentationOwner.ReleaseItem(gameObject);
        presentationOwner = null;
    }

    public void RefreshRemotePresentation()
    {
        if (!IsRemoteOwned || (lastState != ItemState.InHand && lastState != ItemState.InInventory)) return;
        var players = NetworkLifecycle.Instance.Client?.ClientPlayerManager;
        if (players == null || !players.TryGetPlayer((byte)OwnerId, out var player) || player == null)
        {
            ReleasePresentation();
            gameObject.SetActive(false);
            return;
        }
        if (presentationOwner == player && presentedState == lastState) return;
        ReleasePresentation();
        if (lastState == ItemState.InHand) player.HoldItem(gameObject);
        else player.AddItemToInventory(gameObject);
        presentationOwner = player;
        presentedState = lastState;
    }
    #endregion

    protected override void OnDestroy()
    {
        if (UnloadWatcher.isQuitting)
            return;

        if (UnloadWatcher.isUnloading)
        {
            itemBaseToNetworkedItem.Clear();
            base.OnDestroy();
            return;
        }

        ReleasePresentation();
        SetOwner(-1);

        if (NetworkLifecycle.Instance.IsHost())
        {
            var updateData = CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (updateData != null)
                NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, updateData);
        }

        if (Item != null)
        {
            Item.Grabbed -= OnGrabbed;
            Item.Ungrabbed -= OnUngrabbed;
            itemBaseToNetworkedItem.Remove(Item);
        }
        else
        {
            Multiplayer.LogWarning($"NetworkedItem.OnDestroy({name}, {NetId}) Item is null!");
        }

        base.OnDestroy();

    }

    public string GetDirtyValuesDebugString()
    {
        var dirtyValues = trackedValues.Where(tv => ((dynamic)tv).IsDirty).ToList();
        if (dirtyValues.Count == 0)
        {
            return "No dirty values";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Dirty values for NetworkedItem: {name}, NetId: {NetId}:");
        foreach (var value in dirtyValues)
        {
            sb.AppendLine(((dynamic)value).GetDebugString());
        }
        return sb.ToString();
    }
}
