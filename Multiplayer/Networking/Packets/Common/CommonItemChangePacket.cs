using LiteNetLib.Utils;
using System.Collections.Generic;
using System;
using System.IO;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Common;

public class CommonItemChangePacket : INetSerializable
{
    private const int COMPRESS_AFTER_COUNT = 50;
    private const int MAX_ITEM_COUNT = ushort.MaxValue;
    private const int MAX_DECOMPRESSED_BYTES = 4 * 1024 * 1024;

    public List<ItemUpdateData> Items = new List<ItemUpdateData>();

    public void Deserialize(NetDataReader reader)
    {

        Items.Clear();

        //Multiplayer.LogDebug(()=>"CommonItemChangePacket.Deserialize()");
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Deserialize()\r\nBytes: {BitConverter.ToString(reader.RawData).Replace("-", " ")}");

        try
        {
            bool compressed = reader.GetBool();
            if (compressed)
            {
                DeserializeCompressed(reader);
            }
            else
            {
                DeserializeRaw(reader);
            }

            //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Deserialize() post-itemCount {Items?.Count} ");
        }
        catch (Exception ex)
        {
            // A truncated batch must not apply its successfully decoded prefix.
            Items.Clear();
            Multiplayer.LogError($"Error in CommonItemChangePacket.Deserialize: {ex.Message}");
        }
    }

    private void DeserializeCompressed(NetDataReader reader)
    {
        int itemCount = reader.GetInt();
        ValidateItemCount(itemCount);
        byte[] compressedData = reader.GetBytesWithLength();
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.DeserializeCompressed() itemCount {itemCount} length: {compressedData.Length}");

        byte[] decompressedData = PacketCompression.Decompress(compressedData, MAX_DECOMPRESSED_BYTES);
        //Multiplayer.Log($"CommonItemChangePacket.DeserializeCompressed() Compressed: {compressedData.Length} Decompressed: {decompressedData.Length}");

        NetDataReader decompressedReader = new NetDataReader(decompressedData);
        
        //Items.Capacity = itemCount;

        for (int i = 0; i < itemCount; i++)
        {
            var item = new ItemUpdateData();
            item.Deserialize(decompressedReader);
            Items.Add(item);
        }
    }

    private void DeserializeRaw(NetDataReader reader)
    {
        int itemCount = reader.GetInt();
        ValidateItemCount(itemCount);
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.DeserializeRaw() itemCount: {itemCount}");

        for (int i = 0; i < itemCount; i++)
        {
            var item = new ItemUpdateData();
            item.Deserialize(reader);
            Items.Add(item);
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        //Multiplayer.LogDebug(() => "CommonItemChangePacket.Serialize()");
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Serialize() Data Before\r\nBytes: {BitConverter.ToString(writer.CopyData()).Replace("-", " ")}");
        
        try
        {
            ValidateItemCount(Items.Count);
            if (Items.Count > COMPRESS_AFTER_COUNT)
            {
                SerializeCompressed(writer);
            }
            else
            {
                SerializeRaw(writer);
            }

            //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Serialize() Data After\r\nBytes: {BitConverter.ToString(writer.CopyData()).Replace("-", " ")}");
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"CommonItemChangePacket.Serialize: {ex.Message}\r\n{ex.StackTrace}");
            throw;
        }
    }

    private void SerializeCompressed(NetDataWriter writer)
    {
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Serialize() Compressing. Item Count: {Items.Count}");
        writer.Put(true); // compressed data stream
        writer.Put(Items.Count);

        NetDataWriter dataWriter = new NetDataWriter();

        foreach (var item in Items)
        {
            item.Serialize(dataWriter);
        }

        if (dataWriter.Length > MAX_DECOMPRESSED_BYTES)
            throw new InvalidDataException("Item batch exceeds the size limit.");
        byte[] compressedData = PacketCompression.Compress(dataWriter.CopyData());
        //Multiplayer.LogDebug(() => $"Uncompressed: {dataWriter.Length} Compressed: {compressedData.Length}");
        writer.PutBytesWithLength(compressedData);
    }

    private void SerializeRaw(NetDataWriter writer)
    {
        //Multiplayer.LogDebug(() => $"CommonItemChangePacket.Serialize() Raw. Item Count: {Items.Count}");
        writer.Put(false); // uncompressed data stream
        writer.Put(Items.Count);
        foreach (var item in Items)
        {
            item.Serialize(writer);
        }
    }

    private static void ValidateItemCount(int itemCount)
    {
        if (itemCount < 0 || itemCount > MAX_ITEM_COUNT)
            throw new InvalidDataException("Invalid item count.");
    }
}
