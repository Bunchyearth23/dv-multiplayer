namespace Multiplayer.Networking.Packets.Serverbound.Train;

public class ServerboundFastTravelPacket
{
    public uint OperationId { get; set; }
    public ushort CarId { get; set; }
    public string Destination { get; set; }
}
