using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.API;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.TransportLayers;

namespace Multiplayer.Networking.Managers.Server;

public partial class NetworkServer
{
    //allow mods to register their own packets
    public void RegisterExternalPacket<T>(ServerPacketHandler<T> handler) where T : class, IPacket, new()
    {
        netPacketProcessor.SubscribeReusable<T, ITransportPeer>((packet, peer) =>
        {
            var serverPlayer = TryGetServerPlayer(peer, out var player) ? GetWrapper(player) : null;
            handler(packet, serverPlayer);
        });
    }

    public void RegisterExternalSerializablePacket<T>(ServerPacketHandler<T> handler) where T : class, ISerializablePacket, new()
    {
        netPacketProcessor.SubscribeNetSerializable<ExternalSerializablePacketWrapper<T>, ITransportPeer>((wrapper, peer) =>
        {
            // Identity-sensitive adapters compare this sender with IServer.GetPlayer.
            // Use the same session wrapper as ordinary packets and player events.
            var serverPlayer = TryGetServerPlayer(peer, out var player) ? GetWrapper(player) : null;
            handler(wrapper.Packet, serverPlayer);
        },
        () => new ExternalSerializablePacketWrapper<T>()
        );
    }

    public ServerPlayerWrapper GetWrapper(ServerPlayer serverPlayer)
    {
        if (!PlayerWrapperCache.TryGetValue(serverPlayer.PlayerId, out var wrapper))
        {
            wrapper = new ServerPlayerWrapper(serverPlayer);
            PlayerWrapperCache[serverPlayer.PlayerId] = wrapper;
        }
        return wrapper;
    }
}
