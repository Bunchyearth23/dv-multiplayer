using System;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;

internal static class StartingInventoryGrantsTests
{
    private static PlayerItemSaveData Item(string prefab, int slot = -1) => new PlayerItemSaveData
    {
        ItemPrefabName = prefab, InventorySlotIndex = slot, ContainerSlotIndex = -1,
        BelongsToPlayer = true, ItemRotationW = 1,
        State = new JObject { [PlayerInventorySaveCodec.IdentityKey] = Guid.NewGuid().ToString("N") }
    };
    private static void Check(bool value) { if (!value) throw new Exception("Starting inventory grant invariant failed."); }

    public static void FirstJoinAndRetryKeepIdentities()
    {
        var catalog = new[] { Item("radio", 1), Item("license") };
        var prepared = StartingInventoryGrants.Prepare(null, catalog, 2);
        var retried = StartingInventoryGrants.Prepare(JObject.Parse(prepared.ToString()), catalog, 2);
        Check(JToken.DeepEquals(prepared, retried));
        var items = PlayerInventorySaveCodec.Read(retried);
        Check(items.Length == 2 && items[0].InventorySlotIndex == 1 && items[1].InventorySlotIndex == 0);
        Check(items[0].NetId == 0 && items[1].BelongsToPlayer);
    }

    public static void DroppedGrantsAreNotRecreated()
    {
        var catalog = new[] { Item("radio") };
        var prepared = StartingInventoryGrants.Prepare(null, catalog, 1);
        PlayerInventorySaveCodec.Write(prepared, Array.Empty<PlayerItemSaveData>());
        var restored = StartingInventoryGrants.Prepare(JObject.Parse(prepared.ToString()), catalog, 1);
        Check(PlayerInventorySaveCodec.Read(restored).Length == 0);
    }

    public static void ExistingItemsAreAdoptedWithoutDuplication()
    {
        var owned = Item("radio", 0);
        var profile = new JObject { ["other"] = 42 };
        PlayerInventorySaveCodec.Write(profile, new[] { owned });
        var prepared = StartingInventoryGrants.Prepare(profile, new[] { Item("radio"), Item("license", 0) }, 1);
        var items = PlayerInventorySaveCodec.Read(prepared);
        Check(items.Length == 2 && PlayerInventorySaveCodec.Identity(items[0].State) == PlayerInventorySaveCodec.Identity(owned.State));
        Check(items[1].InventorySlotIndex == -1 && (int)prepared["other"] == 42);
        Check(profile[StartingInventoryGrants.Key] == null && PlayerInventorySaveCodec.Read(profile).Length == 1);
    }

    public static void NewlyUnlockedLicenseIsGrantedOnce()
    {
        var profile = StartingInventoryGrants.Prepare(null, new[] { Item("radio") }, 3);
        var catalog = new[] { Item("radio"), Item("license"), Item("license") };
        var prepared = StartingInventoryGrants.Prepare(profile, catalog, 3);
        Check(PlayerInventorySaveCodec.Read(prepared).Length == 2);
        Check(JToken.DeepEquals(prepared, StartingInventoryGrants.Prepare(prepared, catalog, 3)));
    }

    public static void InvalidLedgerAndCatalogPreserveProfile()
    {
        var profile = StartingInventoryGrants.Prepare(null, new[] { Item("radio") }, 1);
        var before = profile.ToString();
        try { StartingInventoryGrants.Prepare(profile, new[] { Item("license"), Item("") }, 1); }
        catch (FormatException)
        {
            Check(before == profile.ToString());
            profile[StartingInventoryGrants.Key]["Items"]["radio"] = "invalid";
            try { StartingInventoryGrants.Prepare(profile, new[] { Item("radio") }, 1); }
            catch (FormatException) { return; }
        }
        throw new Exception("Invalid grant data was accepted.");
    }
}
