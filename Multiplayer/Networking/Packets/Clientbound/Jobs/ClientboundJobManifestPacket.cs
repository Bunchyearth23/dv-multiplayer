namespace Multiplayer.Networking.Packets.Clientbound.Jobs;

/// <summary>Exact initial job set, sent after the ordered creation packets.</summary>
public class ClientboundJobManifestPacket
{
    public ushort[] JobIds { get; set; }
}
