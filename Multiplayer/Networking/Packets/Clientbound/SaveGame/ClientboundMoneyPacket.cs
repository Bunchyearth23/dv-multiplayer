namespace Multiplayer.Networking.Packets.Clientbound.SaveGame;

public class ClientboundMoneyPacket
{
    public string PlayerGuid { get; set; }
    public double Amount { get; set; }
}
