using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using Multiplayer.API;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.TransportLayers;

public class StateRequest : ISerializablePacket
{
    public string Value;
    public void Serialize(BinaryWriter writer) => writer.Write(Value);
    public void Deserialize(BinaryReader reader) => Value = reader.ReadString();
}
public class OrdinaryRequest : IPacket { public string Value { get; set; } }
internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static int Main()
    {
        try
        {
            var server = new NetworkServer(); var peer = new Peer();
            var player = new ServerPlayer { PlayerId = 2, PersistentId = Guid.NewGuid() };
            server.Peers[peer] = player;
            var canonical = server.GetWrapper(player);
            IPlayer sender = null; string payload = null;
            server.RegisterExternalSerializablePacket<StateRequest>((request, actor) => { sender = actor; payload = request.Value; });
            server.RegisterExternalPacket<OrdinaryRequest>((request, actor) => { sender = actor; payload = request.Value; });
            server.Deliver(peer, "state.get");
            Check(ReferenceEquals(sender, canonical), "BDVM rejects serialized request as unknown-peer: sender differs from GetPlayer");
            Check(payload == "state.get" && sender.PersistentId == player.PersistentId, "Payload or durable identity changed");
            server.Deliver(peer, new string('x', 4096));
            Check(ReferenceEquals(sender, canonical) && payload.Length == 4096, "Compressed page request changed identity/payload");
            server.DeliverOrdinary(peer);
            Check(ReferenceEquals(sender, canonical), "Ordinary and serializable packet senders differ");
            server.Peers.Remove(peer); server.PlayerWrapperCache.Remove(player.PlayerId);
            server.Deliver(peer, "stale");
            Check(sender == null, "Disconnected peer retained authentication");
            var replacementPeer = new Peer(); var replacement = new ServerPlayer { PlayerId = 2, PersistentId = Guid.NewGuid() };
            server.Peers[replacementPeer] = replacement;
            server.Deliver(replacementPeer, "reconnect");
            Check(sender != null && !ReferenceEquals(sender, canonical) && ReferenceEquals(sender, server.GetWrapper(replacement)) &&
                sender.PersistentId == replacement.PersistentId, "Reused PlayerId inherited former identity");
            server.Deliver(peer, "old-session");
            Check(sender == null, "Old peer mapped to replacement session");
            server.Deliver(new Peer(), "unknown");
            Check(sender == null, "Unknown peer authenticated");
            Console.WriteLine($"{checks} external packet identity checks passed (production callbacks/cache and real LiteNetLib serialization).");
            RollingStockChecks.Run();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
namespace MPAPI.Interfaces { public interface IPlayer { byte PlayerId { get; } Guid PersistentId { get; } } }
namespace Multiplayer { public static class Multiplayer { public static void LogDebug(Func<string> message) { } } }
namespace Multiplayer.Networking.TransportLayers { public interface ITransportPeer { } public class Peer : ITransportPeer { } }
namespace Multiplayer.Networking.Data { public class ServerPlayer { public byte PlayerId; public Guid PersistentId; } }
namespace Multiplayer.API
{
    public class ServerPlayerWrapper : IPlayer
    {
        private readonly ServerPlayer player;
        public ServerPlayerWrapper(ServerPlayer player) { this.player = player; }
        public byte PlayerId => player.PlayerId;
        public Guid PersistentId => player.PersistentId;
    }
}
namespace Multiplayer.Networking.Managers.Server
{
    public partial class NetworkServer
    {
        private readonly NetPacketProcessor netPacketProcessor = new NetPacketProcessor();
        public readonly Dictionary<ITransportPeer, ServerPlayer> Peers = new Dictionary<ITransportPeer, ServerPlayer>();
        public readonly Dictionary<byte, ServerPlayerWrapper> PlayerWrapperCache = new Dictionary<byte, ServerPlayerWrapper>();
        public bool TryGetServerPlayer(ITransportPeer peer, out ServerPlayer player) => Peers.TryGetValue(peer, out player);
        public void Deliver(ITransportPeer peer, string value)
        {
            var writer = new NetDataWriter();
            var packet = new ExternalSerializablePacketWrapper<StateRequest> { Packet = new StateRequest { Value = value } };
            netPacketProcessor.WriteNetSerializable(writer, ref packet);
            netPacketProcessor.ReadAllPackets(new NetDataReader(writer.CopyData()), peer);
        }
        public void DeliverOrdinary(ITransportPeer peer)
        {
            var writer = new NetDataWriter(); netPacketProcessor.Write(writer, new OrdinaryRequest { Value = "ordinary" });
            netPacketProcessor.ReadAllPackets(new NetDataReader(writer.CopyData()), peer);
        }
    }
}
