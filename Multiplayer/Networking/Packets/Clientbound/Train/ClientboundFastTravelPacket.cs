namespace Multiplayer.Networking.Packets.Clientbound.Train;

public class ClientboundFastTravelPacket
{
    public uint OperationId { get; set; }
    public ushort CarId { get; set; }
    // 0 refused, 1 arrived, 2 pending, 3 recovery required.
    public byte Status { get; set; }
}
