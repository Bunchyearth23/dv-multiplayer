using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Clientbound;
using Newtonsoft.Json.Linq;

internal static class InventoryLocationTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Inventory location invariant failed."); }

    public static void InventoryLocationSurvivesItemProtocol()
    {
        var location = new ItemInventoryLocation(-1, "container-guid", 3, false);
        var states = new Dictionary<string, object>(); location.Write(states);
        var source = new ItemUpdateData { ItemNetId = 3, ItemState = ItemState.InInventory, Player = 255,
            UpdateType = ItemUpdateData.ItemUpdateType.FullSync, States = states };
        var writer = new NetDataWriter(); source.Serialize(writer);
        var actual = new ItemUpdateData(); actual.Deserialize(new NetDataReader(writer.CopyData()));
        Check(ItemInventoryLocation.TryRead(actual.States, out var restored) && restored.Equals(location) && actual.Player == 255);
    }

    public static void PartialOrWronglyTypedLocationsRejected()
    {
        var states = new Dictionary<string, object>(); new ItemInventoryLocation(7, null, -1, true).Write(states);
        Check(ItemInventoryLocation.TryRead(states, out var location) && location.Slot == 7 && location.Locked);
        states[ItemInventoryLocation.SlotKey] = 7f; Check(!ItemInventoryLocation.TryRead(states, out _));
        states[ItemInventoryLocation.SlotKey] = 7;
        states.Remove(ItemInventoryLocation.LockedKey); Check(!ItemInventoryLocation.TryRead(states, out _));
        Check(!ItemInventoryLocation.TryRead(null, out _));
    }

    public static void InvalidContainerAndSlotCombinationsRejected()
    {
        var cases = new Action[] {
            () => new ItemInventoryLocation(-2, null, -1, false),
            () => new ItemInventoryLocation(32768, null, -1, false),
            () => new ItemInventoryLocation(-1, "box", -1, false),
            () => new ItemInventoryLocation(-1, null, 0, false),
            () => new ItemInventoryLocation(-1, new string('x', 257), 1, false) };
        foreach (var action in cases)
        {
            try { action(); throw new Exception("Invalid location accepted"); }
            catch (ArgumentException) { }
        }
        Check(ItemInventoryLocation.Unplaced.Equals(new ItemInventoryLocation(-1, "", -1, false)));
    }

    public static void InventoryRestorePacketPreservesBindingAndState()
    {
        var processor = new NetPacketProcessor();
        processor.RegisterNestedType(PlayerItemSaveData.Serialize, PlayerItemSaveData.Deserialize);
        ClientboundInventoryRestorePacket received = null;
        processor.SubscribeReusable<ClientboundInventoryRestorePacket>(packet => received = packet);
        var writer = new NetDataWriter();
        var identity = Guid.NewGuid();
        processor.Write(writer, new ClientboundInventoryRestorePacket { Items = new[] { new PlayerItemSaveData
        {
            NetId = 400, ItemPrefabName = "Flashlight", InventorySlotIndex = 4, ContainerSlotIndex = -1,
            State = new JObject { [PlayerInventorySaveCodec.IdentityKey] = identity.ToString("N"), ["Battery_power"] = 53 }
        } } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received.Items.Length == 1 && received.Items[0].NetId == 400 && received.Items[0].InventorySlotIndex == 4 &&
            PlayerInventorySaveCodec.Identity(received.Items[0].State) == identity && (int)received.Items[0].State["Battery_power"] == 53);
        writer.Reset();
        processor.Write(writer, new ClientboundInventoryRestorePacket { Items = Array.Empty<PlayerItemSaveData>(), Error = "Missing prefab" });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received.Items.Length == 0 && received.Error == "Missing prefab");
    }
}
