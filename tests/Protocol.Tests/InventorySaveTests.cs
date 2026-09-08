using System;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;

internal static class InventorySaveTests
{
    public static void InvalidContainerLocationsPreserveProfile()
    {
        var profile = new JObject(); PlayerInventorySaveCodec.Write(profile, new[] { Item() });
        string before = profile.ToString();
        foreach (bool orphanSlot in new[] { true, false })
        {
            var invalid = Item(); invalid.ContainerId = orphanSlot ? null : "box";
            invalid.ContainerSlotIndex = orphanSlot ? 1 : -1;
            try { PlayerInventorySaveCodec.Write(profile, new[] { invalid }); throw new Exception("Invalid location accepted"); }
            catch (FormatException) { Check(before == profile.ToString()); }
            var malformed = (JObject)profile.DeepClone();
            var row = malformed[PlayerInventorySaveCodec.InventoryKey]["Items"][0];
            row["Container"] = invalid.ContainerId; row["ContainerSlot"] = invalid.ContainerSlotIndex;
            try { PlayerInventorySaveCodec.Read(malformed); throw new Exception("Invalid saved location accepted"); }
            catch (FormatException) { }
        }
    }

    private static void Check(bool value) { if (!value) throw new Exception("Inventory save invariant failed."); }
    private static PlayerItemSaveData Item(int slot = 1) => new PlayerItemSaveData
    {
        NetId = 123, ItemPrefabName = "Flashlight", InventorySlotIndex = slot, ContainerSlotIndex = -1,
        ItemRotationW = 1, BelongsToPlayer = true, InLockedSlot = true,
        State = new JObject { [PlayerInventorySaveCodec.IdentityKey] = Guid.NewGuid().ToString("N"), ["Battery_power"] = 73.5f }
    };

    public static void InventorySaveSurvivesRestartWithoutSessionIds()
    {
        var profile = new JObject(); var item = Item();
        PlayerInventorySaveCodec.Write(profile, new[] { item });
        var restored = PlayerInventorySaveCodec.Read(JObject.Parse(profile.ToString()));
        Check(restored.Length == 1 && restored[0].NetId == 0 && restored[0].InventorySlotIndex == 1 &&
            restored[0].InLockedSlot && restored[0].BelongsToPlayer &&
            PlayerInventorySaveCodec.Identity(restored[0].State) == PlayerInventorySaveCodec.Identity(item.State) &&
            (float)restored[0].State["Battery_power"] == 73.5f);
    }

    public static void UnknownAndEmptyInventoriesRemainDistinct()
    {
        var profile = new JObject { ["OtherSetting"] = 9 };
        Check(PlayerInventorySaveCodec.Read(profile) == null);
        PlayerInventorySaveCodec.Write(profile, Array.Empty<PlayerItemSaveData>());
        Check(PlayerInventorySaveCodec.Read(profile).Length == 0 && (int)profile["OtherSetting"] == 9);
        profile[PlayerInventorySaveCodec.InventoryKey]["Version"] = 2;
        try { PlayerInventorySaveCodec.Read(profile); }
        catch (FormatException) { return; }
        throw new Exception("Unknown inventory version accepted");
    }

    public static void FailedInventoryReplacementIsAtomic()
    {
        var profile = new JObject(); var first = Item();
        PlayerInventorySaveCodec.Write(profile, new[] { first });
        string original = profile.ToString();
        var invalid = Item(); invalid.ItemPositionX = float.NaN;
        try { PlayerInventorySaveCodec.Write(profile, new[] { invalid }); }
        catch (FormatException) { Check(profile.ToString() == original); return; }
        throw new Exception("Invalid replacement accepted");
    }

    public static void InventoryDuplicatesAndAliasesRejected()
    {
        var first = Item(); var other = Item(); var profile = new JObject();
        int errors = 0;
        try { PlayerInventorySaveCodec.Write(profile, new[] { first, first }); } catch (FormatException) { errors++; }
        try { PlayerInventorySaveCodec.Write(profile, new[] { first, other }); } catch (FormatException) { errors++; }
        Check(errors == 2 && !profile.HasValues);
        other.ContainerId = "box"; other.ContainerSlotIndex = 1; other.InventorySlotIndex = -1;
        PlayerInventorySaveCodec.Write(profile, new[] { first, other });
        var values = PlayerInventorySaveCodec.Read(profile);
        Check(values.Length == 2 && values[1].ContainerId == "box" && values[1].ContainerSlotIndex == 1);
    }

    public static void InventoryStateDoesNotAliasSavedProfile()
    {
        var item = Item(); var profile = new JObject();
        PlayerInventorySaveCodec.Write(profile, new[] { item });
        item.State["Battery_power"] = 0;
        var loaded = PlayerInventorySaveCodec.Read(profile); Check((float)loaded[0].State["Battery_power"] == 73.5f);
        loaded[0].State["Battery_power"] = 1;
        Check((float)PlayerInventorySaveCodec.Read(profile)[0].State["Battery_power"] == 73.5f);
        var changed = loaded[0]; var position = changed.Position; changed.ItemPositionX = 8;
        Check(changed.Position.X == 8);
    }
}
