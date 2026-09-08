using LiteNetLib.Utils;

namespace Multiplayer.Networking.Data.RPCs;

public class ShopPurchaseResponse : ShopQuoteResponse
{
    public string OperationId { get; set; }
    public bool RecoveryRequired { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        base.Serialize(writer);
        writer.Put(OperationId);
        writer.Put(RecoveryRequired);
    }

    public override void Deserialize(NetDataReader reader)
    {
        base.Deserialize(reader);
        OperationId = reader.GetString();
        RecoveryRequired = reader.GetBool();
    }
}
