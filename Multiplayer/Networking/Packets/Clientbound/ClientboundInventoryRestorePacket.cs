using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundInventoryRestorePacket
{
    public PlayerItemSaveData[] Items { get; set; }
    public string Error { get; set; }
}
