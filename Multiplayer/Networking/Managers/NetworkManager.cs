using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.API;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Utils;
using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Multiplayer.Networking.Managers;

public abstract class NetworkManager
{
    protected const int LATENCY_FLAG = 150;

    protected readonly NetPacketProcessor netPacketProcessor;
    protected readonly NetDataWriter cachedWriter = new();

    private readonly ITransport transport;
    private const int MaximumQueuedPackets = 2048;
    private const int MaximumPacketBytes = 4 * 1024 * 1024;
    private const long MaximumQueuedBytes = 16L * 1024 * 1024;
    private const double MaximumPacketAgeSeconds = 10;
    private readonly BoundedWorkQueue<PendingPacket> pendingPackets = new(MaximumQueuedPackets, MaximumQueuedBytes);
    private System.Runtime.CompilerServices.ConditionalWeakTable<ITransportPeer, Rejection> rejectedPeers = new();
    private sealed class Rejection { public int Disconnected; }
    //protected readonly NetManager netManager;

    protected abstract string LogPrefix { get; }

    public NetStatistics Statistics => transport.Statistics;
    public bool IsRunning => transport.IsRunning;
    public bool IsProcessingPacket { get; private set; }
    public NetworkTrafficMetrics CampaignTraffic { get; } = new();

    protected NetworkManager(Settings settings)
    {
        netPacketProcessor = new NetPacketProcessor();
        //transport = new LiteNetLibTransport();
        transport = new SteamWorksTransport();

        transport.OnConnectionRequest += OnConnectionRequest;
        transport.OnPeerConnected += OnPeerConnected;
        transport.OnPeerDisconnected += OnPeerDisconnected;
        transport.OnNetworkReceive += OnNetworkReceive;
        transport.OnNetworkError += OnNetworkError;
        transport.OnNetworkLatencyUpdate += OnNetworkLatencyUpdate;

        RegisterNestedTypes();

        OnSettingsUpdated(settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;

        Subscribe();

    }

    private void RegisterNestedTypes()
    {
        netPacketProcessor.RegisterNestedType(BogieData.Serialize, BogieData.Deserialize);
        netPacketProcessor.RegisterNestedType<JobUpdateStruct>();
        netPacketProcessor.RegisterNestedType(JobData.Serialize, JobData.Deserialize);
        netPacketProcessor.RegisterNestedType(ModInfo.Serialize, ModInfo.Deserialize);
        netPacketProcessor.RegisterNestedType(RigidbodySnapshot.Serialize, RigidbodySnapshot.Deserialize);
        netPacketProcessor.RegisterNestedType(StationsChainNetworkData.Serialize, StationsChainNetworkData.Deserialize);
        netPacketProcessor.RegisterNestedType(TrainsetMovementPart.Serialize, TrainsetMovementPart.Deserialize);
        netPacketProcessor.RegisterNestedType(TrainsetSpawnPart.Serialize, TrainsetSpawnPart.Deserialize);
        netPacketProcessor.RegisterNestedType(TrainCarHealthData.Serialize, TrainCarHealthData.Deserialize);
        netPacketProcessor.RegisterNestedType(PitStopPlugMappingData.Serialize, PitStopPlugMappingData.Deserialize);
        netPacketProcessor.RegisterNestedType(LocoResourceModuleData.Serialize, LocoResourceModuleData.Deserialize);
        netPacketProcessor.RegisterNestedType(PitStopPlugData.Serialize, PitStopPlugData.Deserialize);
        netPacketProcessor.RegisterNestedType(PlayerItemSaveData.Serialize, PlayerItemSaveData.Deserialize);
        netPacketProcessor.RegisterNestedType(PlayerTrackingData.Serialize, PlayerTrackingData.Deserialize);
        netPacketProcessor.RegisterNestedType(Vector2Serializer.Serialize, Vector2Serializer.Deserialize);
        netPacketProcessor.RegisterNestedType(Vector3Serializer.Serialize, Vector3Serializer.Deserialize);
        netPacketProcessor.RegisterNestedType(ColorSerializer.Serialize, ColorSerializer.Deserialize);
    }

    public virtual void OnSettingsUpdated(Settings settings)
    {
        transport?.UpdateSettings(settings);
    }

    public void PollEvents()
    {
        //netManager.PollEvents();
        transport?.PollEvents();
    }

    /// <summary>
    /// Applies already-copied packets on the Unity thread under a bounded budget.
    /// Steam callbacks only copy bytes into the queue, so a burst cannot monopolize
    /// the transport poll or a single frame. Packet handlers remain on Unity because
    /// the existing handlers access Unity objects and game state.
    /// </summary>
    public int ProcessPendingPackets(double budgetMilliseconds)
    {
        if (budgetMilliseconds <= 0) return 0;
        var watch = Stopwatch.StartNew();
        var processed = 0;
        while (watch.Elapsed.TotalMilliseconds < budgetMilliseconds && pendingPackets.TryDequeue(out var packet))
        {
            try
            {
                if (packet.Peer == null || rejectedPeers.TryGetValue(packet.Peer, out _)) continue;
                if ((Stopwatch.GetTimestamp() - packet.ReceivedAt) / (double)Stopwatch.Frequency > MaximumPacketAgeSeconds)
                {
                    RejectOverloadedPeer(packet.Peer, "packet age exceeded");
                    continue;
                }
                CampaignTraffic.Record("in", packet.Peer?.Id ?? -1, packet.DeliveryMethod + "_ch" + packet.Channel, packet.Buffer.Length);
                IsProcessingPacket = true;
                netPacketProcessor.ReadAllPackets(new NetDataReader(packet.Buffer), packet.Peer);
            }
            catch (ParseException e)
            {
                Multiplayer.LogWarning($"[{GetType()}] Failed to parse packet: {e.Message}\r\n{e.StackTrace}");
            }
            finally
            {
                IsProcessingPacket = false;
            }
            processed++;
            if (watch.Elapsed.TotalMilliseconds >= budgetMilliseconds) break;
        }
        return processed;
    }

    public virtual bool Start()
    {
        NetIdProvider.Instance.CheckInitialization();
        return transport.Start();
    }
    public virtual bool Start(IPAddress ipv4, IPAddress ipv6, int port)
    {
        return transport.Start(ipv4, ipv6, port);
    }
    public virtual bool Start(int port)
    {
        return transport.Start(port);
    }

    protected virtual ITransportPeer Connect(string address, int port, NetDataWriter netDataWriter)
    {
        return transport.Connect(address, port, netDataWriter);
    }


    public virtual void Stop()
    {
        try { transport.Stop(true); }
        finally
        {
            pendingPackets.Clear();
            rejectedPeers = new();
            transport.OnConnectionRequest -= OnConnectionRequest;
            transport.OnPeerConnected -= OnPeerConnected;
            transport.OnPeerDisconnected -= OnPeerDisconnected;
            transport.OnNetworkReceive -= OnNetworkReceive;
            transport.OnNetworkError -= OnNetworkError;
            transport.OnNetworkLatencyUpdate -= OnNetworkLatencyUpdate;
    
            Settings.OnSettingsUpdated -= OnSettingsUpdated;
    
            NetIdProvider.Destroy(NetIdProvider.Instance);
        }
    }

    protected NetDataWriter WritePacket<T>(T packet) where T : class, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.Write(cachedWriter, packet);
        return cachedWriter;
    }

    protected NetDataWriter WriteNetSerializablePacket<T>(T packet) where T : INetSerializable, new()
    {
        cachedWriter.Reset();
        netPacketProcessor.WriteNetSerializable(cachedWriter, ref packet);
        return cachedWriter;
    }

    protected void SendPacket<T>(ITransportPeer peer, T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        if (peer == null) return;
        var writer = WritePacket(packet);
        CampaignTraffic.Record("out", peer.Id, typeof(T).Name, writer.Length);
        peer.Send(writer, deliveryMethod);
    }

    protected void SendNetSerializablePacket<T>(ITransportPeer peer, T packet, DeliveryMethod deliveryMethod) where T : INetSerializable, new()
    {
        if (peer == null) return;
        var writer = WriteNetSerializablePacket(packet);
        CampaignTraffic.Record("out", peer.Id, typeof(T).Name, writer.Length);
        peer.Send(writer, deliveryMethod);
    }

    //protected void SendUnconnectedPacket<T>(T packet, string ipAddress, int port) where T : class, new()
    //{
    //    transport.SendUnconnectedMessage(WritePacket(packet), ipAddress, port);
    //}

    protected abstract void Subscribe();

    #region Net Events
    public void OnNetworkReceive(ITransportPeer peer, NetDataReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (peer == null || rejectedPeers.TryGetValue(peer, out _)) return;
        if (reader.AvailableBytes > MaximumPacketBytes)
        {
            RejectOverloadedPeer(peer, "packet size exceeded");
            return;
        }
        var bytes = reader.GetRemainingBytes();
        if (bytes == null || bytes.Length == 0) return;
        if (!pendingPackets.TryEnqueue(new PendingPacket(peer, bytes, channel, deliveryMethod), bytes.Length))
            RejectOverloadedPeer(peer, "packet queue capacity exceeded");
    }

    private void RejectOverloadedPeer(ITransportPeer peer, string reason)
    {
        var rejection = rejectedPeers.GetValue(peer, _ => new Rejection());
        if (Interlocked.Exchange(ref rejection.Disconnected, 1) != 0) return;
        // Never apply a newer reliable packet ahead of the FIFO. This stream can
        // no longer be completed: reconnect/late join supplies a fresh baseline.
        LogWarning($"Disconnecting peer {peer.Id}: {reason}; reconnect to resynchronize.");
        peer.Disconnect();
    }

    private sealed class PendingPacket
    {
        public readonly ITransportPeer? Peer;
        public readonly byte[] Buffer;
        public readonly byte Channel;
        public readonly DeliveryMethod DeliveryMethod;
        public readonly long ReceivedAt = Stopwatch.GetTimestamp();
        public PendingPacket(ITransportPeer? peer, byte[] buffer, byte channel, DeliveryMethod deliveryMethod)
        { Peer = peer; Buffer = buffer; Channel = channel; DeliveryMethod = deliveryMethod; }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Multiplayer.LogError($"Network error from {endPoint}: {socketError}");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        //Multiplayer.Log($"OnNetworkReceiveUnconnected({remoteEndPoint}, {messageType})");
        try
        {
            IsProcessingPacket = true;
            netPacketProcessor.ReadAllPackets(reader, remoteEndPoint);
        }
        catch (ParseException e)
        {
            Multiplayer.LogWarning($"Failed to parse packet: {e.Message}");
        }
        finally
        {
            IsProcessingPacket = false;
        }
    }

    //Standard networking callbacks
    public abstract void OnPeerConnected(ITransportPeer peer);
    public abstract void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectInfo);
    public abstract void OnConnectionRequest(NetDataReader requestData, IConnectionRequest request);
    public abstract void OnNetworkLatencyUpdate(ITransportPeer peer, int latency);

    #endregion

    #region Logging

    public void LogDebug(Func<object> resolver)
    {
        if (!Multiplayer.Settings.DebugLogging)
            return;
        Multiplayer.LogDebug(() => $"{LogPrefix} {resolver.Invoke()}");
    }

    public void Log(object msg)
    {
        Multiplayer.Log($"{LogPrefix} {msg}");
    }

    public void LogWarning(object msg)
    {
        Multiplayer.LogWarning($"{LogPrefix} {msg}");
    }

    public void LogError(object msg)
    {
        Multiplayer.LogError($"{LogPrefix} {msg}");
    }

    #endregion
}
