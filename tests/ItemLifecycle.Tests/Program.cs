using System;
using HarmonyLib;
using DV.InventorySystem;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static NetworkedItemManager Reset()
    {
        NetworkedItem.All.Clear(); Inventory.Instance = new Inventory();
        StorageController.Instance = new StorageController(); NetworkLifecycle.Instance = new NetworkLifecycle();
        return new NetworkedItemManager();
    }
    private static int Main()
    {
        try
        {
            new Harmony("item-lifecycle-regressions").PatchAll(typeof(Program).Assembly);
            var manager = Reset();
            var office = new NetworkedItem(); office.Item.Essential = true;
            var personal = new NetworkedItem(); personal.Item.Essential = true; personal.Item.InventorySpecs.BelongsToPlayer = true;
            var held = new NetworkedItem(); held.Item.Grabbed = true;
            var inventory = new NetworkedItem(); Inventory.Instance.Items.Add(inventory.gameObject);
            var lost = new NetworkedItem(); StorageController.Instance.StorageLostAndFound.Items.Add(lost.Item);
            var contained = new NetworkedItem(); Inventory.Instance.ItemContainerRegistry.Contained.Add(contained.gameObject);
            var job = new NetworkedItem { TrackedItemType = typeof(JobMarker) };
            var bound = new NetworkedItem { NetId = 21 };
            manager.CacheWorldItems();
            Check(office.IsCached && !office.gameObject.Active, "World essential left duplicated");
            Check(!personal.IsCached && !held.IsCached && !inventory.IsCached && !lost.IsCached && !contained.IsCached,
                "Personal inventory was cached");
            Check(!job.IsCached && !bound.IsCached, "Authoritative item/job cached");
            var replica = manager.CreateReplica(1);
            Check(replica == office, "Initial world item not reused");
            StorageController.Instance.AddItemToWorldStorage(replica.Item);
            Check(replica.Item.Respawn.MetadataUpdates == 1, "Native storage contract broken after reuse");
            var late = new NetworkedItem(); manager.OnItemStarted(late);
            Check(late.IsCached, "Streamed local object remained active");
            manager.OnItemStarted(replica);
            Check(!replica.IsCached, "Replica Start cached authoritative object");
            manager.OnItemStarted(late);
            Check(manager.CreateReplica(2) == late && manager.CreateReplica(3) != late, "Cache returned one object twice");
            replica.Item.Grabbed = true; Inventory.Instance.Items.Add(replica.gameObject);
            manager.Tombstone(replica);
            Check(!replica.Item.Grabbed && replica.Item.Releases == 1 && Inventory.Instance.Drops == 1 && !replica.gameObject.Active,
                "Tombstone did not end interaction before hiding item");
            Check(!StorageController.Instance.StorageWorld.ContainsItem(replica.Item), "Cached item remains in storage");
            manager.Tombstone(replica);
            Check(replica.Item.Releases == 1, "Repeated tombstone released twice");
            var direct = new NetworkedItem(); direct.Item.Grabbed = true;
            direct.ReleaseLocalInteraction();
            Check(!direct.Item.Grabbed && direct.Item.Releases == 1, "Direct VR/world grab remains active outside inventory");
            var respawn = replica.Item.Respawn;
            Check(!respawn.Checker(.2f).MoveNext() && !respawn.RespawnOrDestroy(1).MoveNext() && respawn.NativeEffects == 0,
                "Client native respawn/physics still authoritative");
            respawn.UpdateSpawnParams();
            Check(respawn.MetadataUpdates == 2, "Client metadata suppressed");
            NetworkLifecycle.Instance.Host = true;
            Check(respawn.Checker(.2f).MoveNext() && respawn.RespawnOrDestroy(1).MoveNext(), "Host routines suppressed");
            var hostItem = new NetworkedItem(); manager.OnItemStarted(hostItem); manager.CacheWorldItems();
            Check(!hostItem.IsCached, "Host item cached");
            NetworkLifecycle.Instance.Host = false; NetworkLifecycle.Instance.IsClientRunning = false;
            Check(respawn.Checker(.2f).MoveNext() && respawn.RespawnOrDestroy(1).MoveNext(), "Offline routines suppressed");
            Console.WriteLine($"{checks} item lifecycle checks passed (production cache/release + Harmony, simulated native surfaces).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
