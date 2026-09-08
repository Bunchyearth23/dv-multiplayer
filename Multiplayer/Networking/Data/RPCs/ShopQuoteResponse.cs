using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Data.RPCs;

public class ShopQuoteResponse : IRpcResponse
{
    public ushort RegisterNetId { get; set; }
    public ShopQuoteStatus Status { get; set; }
    public double Total { get; set; }
    public int FailedLine { get; set; } = -1;

    public virtual void Serialize(NetDataWriter writer)
    {
        writer.Put(RegisterNetId);
        writer.Put((byte)Status);
        writer.Put(Total);
        writer.Put(FailedLine);
    }

    public virtual void Deserialize(NetDataReader reader)
    {
        RegisterNetId = reader.GetUShort();
        Status = (ShopQuoteStatus)reader.GetByte();
        Total = reader.GetDouble();
        FailedLine = reader.GetInt();
    }
}
