namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundShopPurchaseRequestPacket
{
    public uint TicketId { get; set; }
    public string OperationId { get; set; }
    public ushort RegisterNetId { get; set; }
    public string[] ItemIds { get; set; }
    public int[] Quantities { get; set; }
}
