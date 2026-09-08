namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundShopQuoteRequestPacket
{
    public uint TicketId { get; set; }
    public ushort RegisterNetId { get; set; }
    public string[] ItemIds { get; set; }
    public int[] Quantities { get; set; }
}
