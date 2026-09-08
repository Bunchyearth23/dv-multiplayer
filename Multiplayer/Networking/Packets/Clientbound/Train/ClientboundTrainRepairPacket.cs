using Multiplayer.Networking.Data.Train;

namespace Multiplayer.Networking.Packets.Clientbound.Train;

public class ClientboundTrainRepairPacket
{
    public TrainsetSpawnPart[] Parts { get; set; }
    public bool Relocate { get; set; }
    public uint Tick { get; set; }
}
