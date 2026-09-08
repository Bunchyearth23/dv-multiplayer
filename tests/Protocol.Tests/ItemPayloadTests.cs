using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;

internal static class ItemPayloadTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Item payload invariant failed."); }
    private static NetDataWriter Header(int count)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)ItemUpdateData.ItemUpdateType.ObjectState);
        writer.Put((ushort)1); writer.Put((byte)ItemState.Dropped); writer.Put(count);
        return writer;
    }
    private static void Reject(NetDataWriter writer)
    {
        try { new ItemUpdateData().Deserialize(new NetDataReader(writer.CopyData())); }
        catch (FormatException) { return; }
        throw new Exception("Malformed payload accepted");
    }

    public static void TrackedValueCountsAreBounded()
    {
        Reject(Header(-1)); Reject(Header(ItemUpdateData.MaxTrackedValues + 1));
    }

    public static void DuplicateAndOversizedKeysRejected()
    {
        var writer = Header(2);
        for (int i = 0; i < 2; i++) { writer.Put("same"); writer.Put((byte)0); writer.Put(true); }
        Reject(writer);
        writer = Header(1); writer.Put(new string('x', ItemUpdateData.MaxKeyLength + 1));
        writer.Put((byte)0); writer.Put(true); Reject(writer);
    }

    public static void InvalidValueContentsRejected()
    {
        var writer = Header(1); writer.Put("fuel"); writer.Put((byte)3); writer.Put(float.NaN); Reject(writer);
        writer = Header(1); writer.Put("name"); writer.Put((byte)4);
        writer.Put(new string('x', ItemUpdateData.MaxStringLength + 1)); Reject(writer);
        var item = new ItemUpdateData { ItemNetId = 1, UpdateType = ItemUpdateData.ItemUpdateType.ObjectState,
            States = new Dictionary<string, object> { ["fuel"] = float.PositiveInfinity } };
        try { item.Serialize(new NetDataWriter()); }
        catch (FormatException) { return; }
        throw new Exception("Non-finite output accepted");
    }

    public static void ReusedDeserializerClearsAbsentFields()
    {
        var item = new ItemUpdateData { ItemNetId = 5, PrefabName = "old", Player = 8, CarNetId = 2,
            States = new Dictionary<string, object> { ["old"] = true }, ItemState = ItemState.InInventory };
        item.Deserialize(new NetDataReader(Header(0).CopyData()));
        Check(item.ItemNetId == 1 && item.PrefabName == null && item.Player == 0 && item.CarNetId == 0 && item.States == null);
        var writer = new NetDataWriter(); writer.Put((byte)ItemUpdateData.ItemUpdateType.Destroy); writer.Put((ushort)3);
        item.Deserialize(new NetDataReader(writer.CopyData()));
        Check(item.ItemNetId == 3 && item.ItemState == ItemState.Dropped);
    }

    public static void InvalidIdentityAndFlagsRejected()
    {
        foreach (byte flags in new byte[] { 0, 128, 3, 5 })
        {
            var writer = new NetDataWriter(); writer.Put(flags); writer.Put((ushort)1); Reject(writer);
        }
        var zero = new NetDataWriter(); zero.Put((byte)ItemUpdateData.ItemUpdateType.Destroy); zero.Put((ushort)0); Reject(zero);
    }
}
