using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Multiplayer.Components.Networking.World;

public class RespawnOnDrop
{
    public int MetadataUpdates, NativeEffects;
    public void UpdateSpawnParams() { MetadataUpdates++; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IEnumerator Checker(float interval) { NativeEffects++; yield return null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IEnumerator RespawnOrDestroy(float delay) { NativeEffects++; yield return null; }
}
public class NativeItem
{
    public Spec InventorySpecs = new Spec();
    public bool Essential, Grabbed;
    public int Releases;
    public RespawnOnDrop Respawn = new RespawnOnDrop();
    public bool IsEssential() => Essential;
    public bool IsGrabbed() => Grabbed;
    public void ForceEndInteraction() { Grabbed = false; Releases++; }
}
public class Spec { public string itemPrefabName = "office-item"; public bool BelongsToPlayer; }
public class FakeObject
{
    public NativeItem Item;
    public bool Active = true;
    public void SetActive(bool active)
    {
        if (!active && (Item.Grabbed || DV.InventorySystem.Inventory.Instance.Items.Contains(this)))
            throw new Exception("Disabled a still-interacting item");
        Active = active;
    }
}
public class Storage
{
    public HashSet<NativeItem> Items = new HashSet<NativeItem>();
    public bool ContainsItem(NativeItem item) => Items.Contains(item);
}
public class StorageController
{
    public static StorageController Instance = new StorageController();
    public Storage StorageInventory = new Storage(), StorageWorld = new Storage(), StorageLostAndFound = new Storage();
    public bool IsInStorageLostAndFound(NativeItem item) => StorageLostAndFound.ContainsItem(item);
    public void RemoveItemFromStorageItemList(NativeItem item)
    { StorageInventory.Items.Remove(item); StorageWorld.Items.Remove(item); StorageLostAndFound.Items.Remove(item); }
    public void AddItemToWorldStorage(NativeItem item)
    { StorageWorld.Items.Add(item); item.Respawn.UpdateSpawnParams(); } // Same unconditional native contract.
}
namespace DV.InventorySystem
{
    public class Inventory
    {
        public static Inventory Instance = new Inventory();
        public HashSet<FakeObject> Items = new HashSet<FakeObject>();
        public ContainerRegistry ItemContainerRegistry = new ContainerRegistry();
        public int Drops;
        public bool Contains(FakeObject item, bool includeDropped) => Items.Contains(item);
        public void DropItemFromHandsOrInventory(FakeObject item) { Items.Remove(item); Drops++; }
    }
    public class ContainerRegistry
    {
        public HashSet<FakeObject> Contained = new HashSet<FakeObject>();
        public (string ContainerId, int Slot) GetItemContainerIdAndIndex(FakeObject item)
            => (Contained.Contains(item) ? "bag" : null, 0);
    }
}
namespace Multiplayer.Components.Networking
{
    public class NetworkLifecycle
    {
        public static NetworkLifecycle Instance = new NetworkLifecycle();
        public bool Host, IsClientRunning = true;
        public bool IsHost() => Host;
        public FakeClient Client = new FakeClient();
    }
    public class FakeClient { public void LogError(string text) { throw new Exception(text); } }
}
namespace Multiplayer.Components.Networking.World
{
    public class JobMarker { }
    public partial class NetworkedItem
    {
        public static List<NetworkedItem> All = new List<NetworkedItem>();
        public static IEnumerable<NetworkedItem> GetAll() => All;
        public NativeItem Item = new NativeItem();
        public FakeObject gameObject;
        public ushort NetId;
        public bool IsCached;
        public Guid? DormantOwner;
        public Type TrackedItemType;
        public NetworkedItem() { gameObject = new FakeObject { Item = Item }; All.Add(this); }
        public void ResetForCache() { IsCached = true; }
    }
    public partial class NetworkedItemManager
    {
        private bool ClientInitialised;
        private Dictionary<string, List<NetworkedItem>> CachedItems = new Dictionary<string, List<NetworkedItem>>();
        public bool DoNotCreateItem(Type type) => type == typeof(JobMarker);
        public void Tombstone(NetworkedItem item) => SendToCache(item);
        public NetworkedItem CreateReplica(ushort id)
        {
            var item = GetFromCache("office-item") ?? new NetworkedItem();
            item.NetId = id; item.IsCached = false; item.gameObject.SetActive(true); return item;
        }
    }
}
