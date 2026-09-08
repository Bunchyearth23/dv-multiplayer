using DV;
using DV.Booklets;
using DV.Customization;
using DV.Customization.Paint;
using DV.Garages;
using DV.InventorySystem;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.Scenarios.Common;
using DV.ServicePenalty;
using DV.ServicePenalty.UI;
using DV.ThingTypes;
using DV.WeatherSystem;
using Humanizer;
using LiteNetLib;
using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Clientbound.World;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.Packets.Serverbound.Jobs;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Networking.Packets.Unconnected;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Patches.MainMenu;
using Multiplayer.Patches.World;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using UnityEngine;

namespace Multiplayer.Networking.Managers.Server;

public partial class NetworkServer : NetworkManager
{
    private const int WEATHER_UPDATE_INTERVAL = 30; //seconds
    private const int HIGH_PING_LOG_INTERVAL = 60; //only log high ping once every 60 seconds per player

    public Action<ServerPlayer> PlayerConnected;
    public Action<ServerPlayer> PlayerDisconnected;
    public Action<ServerPlayer> PlayerReady;
    protected override string LogPrefix => "[Server]";

    private readonly Queue<ITransportPeer> joinQueue = new();   //Queue for players attempting to join while server is loading
    private readonly ShopPurchaseLedger shopPurchases = new();

    private readonly Dictionary<byte, ServerPlayer> serverPlayers = [];             //player Id to ServerPlayer mapping
    private readonly Dictionary<byte, ITransportPeer> peers = [];                   //player Id to peer mapping
    private readonly Dictionary<ITransportPeer, ServerPlayer> peerToPlayer = [];    //peer to ServerPlayer mapping
    public readonly Dictionary<byte, ServerPlayerWrapper> PlayerWrapperCache = []; //cache for ServerPlayers for API use

    private LobbyServerManager lobbyServerManager;
    public readonly bool IsSinglePlayer;
    public LobbyServerData ServerData;
    public RerailController rerailController;

    private bool fastTravelAdvancesTime;

    public IReadOnlyCollection<ServerPlayer> ServerPlayers => serverPlayers.Values;
    public IReadOnlyCollection<ServerPlayerWrapper> ServerPlayerWrappers => PlayerWrapperCache.Values;
    public int PlayerCount => ServerPlayers.Count;

    private ITransportPeer _selfPeer;
    public ITransportPeer SelfPeer
    {
        get
        {
            if (_selfPeer != null)
                return _selfPeer;

            peers.TryGetValue(SelfId, out _selfPeer);
            return _selfPeer;
        }
    }

    public byte SelfId => NetworkLifecycle.Instance.Client?.PlayerId ?? 0;

    public readonly IDifficulty Difficulty;
    private bool IsLoaded;

    private readonly ChatManager _chatManager = new();
    public ChatManager ChatManager => _chatManager;

    private uint lastTick;

    public NetworkServer(IDifficulty difficulty, Settings settings, bool singlePlayer, LobbyServerData serverData) : base(settings)
    {
        Log($"Server created for {(singlePlayer ? "single player" : "multiplayer")} game");

        IsSinglePlayer = singlePlayer;
        ServerData = serverData;
        Difficulty = difficulty;

        fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
        TimeAdvancePatch.FastTravelAdvancesTime = fastTravelAdvancesTime;
    }

    public override void OnSettingsUpdated(Settings settings)
    {
        if (settings.FastTravelAdvancesTime != fastTravelAdvancesTime)
        {
            fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
            TimeAdvancePatch.FastTravelAdvancesTime = fastTravelAdvancesTime;
            SendGameParams(Globals.G.GameParams);
        }
    }

    public override bool Start(int port)
    {
        Log($"Starting server...");

        //setup paint theme lookup cache
        PaintThemeLookup.Instance.CheckInstance();

        WorldStreamingInit.LoadingFinished += OnLoaded;

        //Try to get our static IPv6 Address we will need this for IPv6 NAT punching to be reliable
        if (IPAddress.TryParse(LobbyServerManager.GetStaticIPv6Address(), out IPAddress ipv6Address))
        {
            //start the connection, IPv4 messages can come from anywhere, IPv6 messages need to specifically come from the static IPv6
            return base.Start(IPAddress.Any, ipv6Address, port);
        }

        //we're not running IPv6, start as normal
        return base.Start(port);
    }

    private bool hasStopped;

    public override void Stop()
    {
        if (hasStopped) return;
        hasStopped = true;
        Log("Stopping server...");
        WorldStreamingInit.LoadingFinished -= OnLoaded;
        NetworkLifecycle.Instance.OnTick -= OnTick;
        IsLoaded = false;
        void CleanupStep(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { LogError($"Server shutdown failed ({name}): {ex}"); }
        }
        CleanupStep("fast travel cancellation", () => fastTravelRoutine?.Dispose());
        CleanupStep("inventory restoration cancellation", () => UnityEngine.Object.FindObjectOfType<NetworkedItemManager>()?.CancelInventoryRestores());
        if (WorldStreamingInit.isLoaded)
            CleanupStep("final inventory capture", () => SaveGameManager.Instance.UpdateInternalData());
        if (lobbyServerManager != null)
        {
            CleanupStep("lobby removal", () => lobbyServerManager.RemoveFromLobbyServer());
            CleanupStep("lobby object", () => UnityEngine.Object.Destroy(lobbyServerManager));
        }
        // Callbacks can remove players/peers and serialize their own packets during Disconnect.
        var packet = new NetDataWriter();
        netPacketProcessor.Write(packet, new ClientboundDisconnectPacket());
        foreach (var peer in peers.Values.ToArray())
            if (peer != SelfPeer)
                CleanupStep("peer disconnect", () => peer?.Disconnect(packet));
        foreach (var player in serverPlayers.Values.ToArray())
            CleanupStep("player disposal", player.Dispose);
        serverPlayers.Clear();
        peers.Clear();
        peerToPlayer.Clear();
        PlayerWrapperCache.Clear();
        joinQueue.Clear();
        PlayerDisconnected = null;
        base.Stop();
    }

    protected override void Subscribe()
    {
        // Client management
        netPacketProcessor.SubscribeReusable<ServerboundClientLoginPacket, IConnectionRequest>(OnServerboundClientLoginPacket);
        netPacketProcessor.SubscribeReusable<CommonChatPacket, ITransportPeer>(OnCommonChatPacket);
        netPacketProcessor.SubscribeReusable<UnconnectedPingPacket, IPEndPoint>(OnUnconnectedPingPacket);


        // World sync
        netPacketProcessor.SubscribeReusable<ServerboundLoadStateUpdatePacket, ITransportPeer>(OnServerboundLoadStateUpdatePacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorldItemRecoveryPacket, ITransportPeer>(OnWorldItemRecovery);
        netPacketProcessor.SubscribeReusable<ServerboundTrainRecoveryPacket, ITransportPeer>(OnTrainRecovery);
        netPacketProcessor.SubscribeReusable<ServerboundTrainManifestRecoveryPacket, ITransportPeer>(OnTrainManifestRecovery);
        netPacketProcessor.SubscribeReusable<ServerboundRailwayStateRecoveryPacket, ITransportPeer>(OnRailwayStateRecovery);
        netPacketProcessor.SubscribeReusable<ServerboundTimeAdvancePacket, ITransportPeer>(OnServerboundTimeAdvancePacket);

        netPacketProcessor.SubscribeReusable<CommonChangeJunctionPacket, ITransportPeer>(OnCommonChangeJunctionPacket);
        netPacketProcessor.SubscribeReusable<CommonRotateTurntablePacket, ITransportPeer>(OnCommonRotateTurntablePacket);

        netPacketProcessor.SubscribeReusable<CommonPitStopInteractionPacket, ITransportPeer>(OnCommonPitStopInteractionPacket);
        netPacketProcessor.SubscribeNetSerializable<CommonPitStopPlugInteractionPacket, ITransportPeer>(OnCommonPitStopPlugInteractionPacket);

        netPacketProcessor.SubscribeReusable<CommonCashRegisterWithModulesActionPacket, ITransportPeer>(OnCommonCashRegisterWithModulesActionPacket);

        netPacketProcessor.SubscribeReusable<CommonGenericSwitchStatePacket, ITransportPeer>(OnCommonGenericSwitchStatePacket);


        // Player
        netPacketProcessor.SubscribeReusable<ServerboundPlayerPositionPacket, ITransportPeer>(OnServerboundPlayerPositionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundLicensePurchaseRequestPacket, ITransportPeer>(OnServerboundLicensePurchaseRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundShopQuoteRequestPacket, ITransportPeer>(OnServerboundShopQuoteRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundShopPurchaseRequestPacket, ITransportPeer>(OnServerboundShopPurchaseRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundPlayerPreferenceUpdatePacket, ITransportPeer>(OnServerboundPlayerPreferenceUpdatePacket);


        // Train
        netPacketProcessor.SubscribeReusable<ServerboundTrainSyncRequestPacket, ITransportPeer>(OnServerboundTrainSyncRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundFastTravelPacket, ITransportPeer>(OnFastTravelRequest);
        netPacketProcessor.SubscribeReusable<ServerboundTenderCoalPacket, ITransportPeer>(OnServerboundTenderCoalPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainPortsPacket, ITransportPeer>(OnCommonTrainPortsPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainFusesPacket, ITransportPeer>(OnCommonTrainFusesPacket);
        netPacketProcessor.SubscribeReusable<CommonPaintThemePacket, ITransportPeer>(OnCommonPaintThemePacket);

        // Train Interaction
        netPacketProcessor.SubscribeReusable<ServerboundTrainControlAuthorityPacket, ITransportPeer>(OnServerboundTrainControlAuthorityPacket);
        netPacketProcessor.SubscribeReusable<CommonCouplerInteractionPacket, ITransportPeer>(OnCommonCouplerInteractionPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainUncouplePacket, ITransportPeer>(OnCommonTrainUncouplePacket);
        netPacketProcessor.SubscribeReusable<CommonHoseConnectedPacket, ITransportPeer>(OnCommonHoseConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonHoseDisconnectedPacket, ITransportPeer>(OnCommonHoseDisconnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonMuConnectedPacket, ITransportPeer>(OnCommonMuConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonMuDisconnectedPacket, ITransportPeer>(OnCommonMuDisconnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonCockFiddlePacket, ITransportPeer>(OnCommonCockFiddlePacket);
        netPacketProcessor.SubscribeReusable<CommonBrakeCylinderReleasePacket, ITransportPeer>(OnCommonBrakeCylinderReleasePacket);
        netPacketProcessor.SubscribeReusable<CommonHandbrakePositionPacket, ITransportPeer>(OnCommonHandbrakePositionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundAddCoalPacket, ITransportPeer>(OnServerboundAddCoalPacket);
        netPacketProcessor.SubscribeReusable<ServerboundFireboxIgnitePacket, ITransportPeer>(OnServerboundFireboxIgnitePacket);

        netPacketProcessor.SubscribeReusable<ServerboundTrainDeleteRequestPacket, ITransportPeer>(OnServerboundTrainDeleteRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainRerailRequestPacket, ITransportPeer>(OnServerboundTrainRerailRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainSpawnRequestPacket, ITransportPeer>(OnServerboundTrainSpawnRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorkTrainRequestPacket, ITransportPeer>(OnServerboundWorkTrainRequestPacket);


        // Jobs
        netPacketProcessor.SubscribeReusable<ServerboundJobValidateRequestPacket, ITransportPeer>(OnServerboundJobValidateRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWarehouseMachineControllerRequestPacket, ITransportPeer>(OnServerboundWarehouseMachineControllerRequestPacket);

        // Items
        netPacketProcessor.SubscribeNetSerializable<CommonItemChangePacket, ITransportPeer>(OnCommonItemChangePacket);
    }

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
            var serverPlayer = TryGetServerPlayer(peer, out var player) ? new ServerPlayerWrapper(player) : null;
            handler(wrapper.Packet, serverPlayer);
        },
        () => new ExternalSerializablePacketWrapper<T>()
        );
    }

    private void OnLoaded()
    {
        if (!IsSinglePlayer)
        {
            lobbyServerManager = NetworkLifecycle.Instance.GetOrAddComponent<LobbyServerManager>();
        }

        Log($"Server loaded, processing {joinQueue.Count} queued players");
        IsLoaded = true;

        //We should initialise object here for dedicated servers, rather than relying on the existance of a client
        NetworkedPitStopStation.InitialisePitStops();
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();

        while (joinQueue.Count > 0)
        {
            ITransportPeer peer = joinQueue.Dequeue();

            // Assuming the `peer.ConnectionState` property exists and is being checked
            if (peer.ConnectionState.Equals(TransportConnectionState.Connected))
            {
                System.Console.WriteLine("Connection is established.");
                OnServerboundLoadStateUpdatePacket(new ServerboundLoadStateUpdatePacket { LoadState = PlayerLoadingState.ReadyForWorldState }, peer);
            }
            else
            {
                System.Console.WriteLine("Connection is not established.");
            }
        }

        lastTick = NetworkLifecycle.Instance.Tick;
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    private void OnTick(uint tick)
    {
        if (!IsLoaded)
            return;

        if ((NetworkLifecycle.Instance.Tick - lastTick) > NetworkLifecycle.TICK_RATE * WEATHER_UPDATE_INTERVAL)
        {
            SendWeatherState();
            lastTick = NetworkLifecycle.Instance.Tick;
        }
    }

    public bool TryGetServerPlayer(ITransportPeer peer, out ServerPlayer player)
    {
        return peerToPlayer.TryGetValue(peer, out player);
    }

    public bool TryGetServerPlayer(byte playerId, out ServerPlayer player)
    {
        return serverPlayers.TryGetValue(playerId, out player);
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

    #region Net Events

    public override void OnPeerConnected(ITransportPeer peer)
    {
        LogDebug(() => $"OnPeerConnected({peer.Id})");
    }

    public override void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectReason)
    {
        LogDebug(() => $"OnPeerDisconnected({peer.Id})");
        if (!peerToPlayer.TryGetValue(peer, out ServerPlayer player))
        {
            LogWarning($"Peer {peer.GetType()}, peerId: {peer.Id} disconnected but no player found");
            return;
        }
        Log($"Player {player.Username} disconnected: {disconnectReason}");

        // Retire the peer first so reentrant disconnect notifications cannot run cleanup twice.
        // Keep the player in serverPlayers until the save has captured their final state.
        peerToPlayer.Remove(peer);
        void CleanupStep(string step, Action action)
        {
            try { action(); }
            catch (Exception ex) { LogError($"Disconnect cleanup failed ({step}, player {player.PlayerId}): {ex}"); }
        }
        if (WorldStreamingInit.isLoaded)
            CleanupStep("save", () => SaveGameManager.Instance.UpdateInternalData());

        serverPlayers.Remove(player.PlayerId);
        peers.Remove(player.PlayerId);
        PlayerWrapperCache.Remove(player.PlayerId);

        CleanupStep("notify clients", () => SendPacketToAll
        (
            new ClientboundPlayerDisconnectPacket
            {
                PlayerId = player.PlayerId
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.Complete
        ));

        var subscribers = PlayerDisconnected?.GetInvocationList();
        if (subscribers != null)
            foreach (Action<ServerPlayer> subscriber in subscribers)
                CleanupStep($"subscriber {subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name}", () => subscriber(player));

        CleanupStep("dispose", player.Dispose);
    }

    public override void OnNetworkLatencyUpdate(ITransportPeer peer, int latency)
    {
        if (!TryGetServerPlayer(peer, out var player))
            return;

        ClientboundPingUpdatePacket clientboundPingUpdatePacket = new()
        {
            PlayerId = player.PlayerId,
            Ping = latency
        };

        SendPacketToAll(clientboundPingUpdatePacket, DeliveryMethod.ReliableUnordered, PlayerLoadingState.None, peer);

        if (latency > LATENCY_FLAG)
        {
            if ((NetworkLifecycle.Instance.Tick - player.LastHighPingTickLogged) > NetworkLifecycle.TICK_RATE * HIGH_PING_LOG_INTERVAL)
            {
                LogWarning($"High Ping Detected! Player: \"{player.Username}\", ping: {latency}ms");
                player.LastHighPingTickLogged = NetworkLifecycle.Instance.Tick;
            }
        }

        // Ensure we don't send a TickSync packet to ourselves
        if (peer == SelfPeer)
            return;

        SendPacket(peer, new ClientboundTickSyncPacket
        {
            ServerTick = NetworkLifecycle.Instance.Tick
        }, DeliveryMethod.ReliableUnordered);
    }

    public override void OnConnectionRequest(NetDataReader requestData, IConnectionRequest request)
    {
        LogDebug(() => $"NetworkServer OnConnectionRequest");
        try
        {
            netPacketProcessor.ReadAllPackets(requestData, request);
        }
        catch (Exception e)
        {
            LogWarning($"Rejected malformed connection request{(Multiplayer.Settings.LogIps ? $" from {request?.RemoteEndPoint?.Address}" : "")}: {e.GetType().Name}: {e.Message}");
            try { request?.Reject(); }
            catch (Exception rejectError) { LogWarning($"Failed to reject malformed connection request: {rejectError.Message}"); }
        }
    }

    #endregion

    #region Packet Senders

    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod, PlayerLoadingState minimumLoadState, bool excludeSelf = false) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (excludeSelf && peer == SelfPeer)
                continue;

            peer?.Send(writer, deliveryMethod);
        }
    }

    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod, PlayerLoadingState minimumLoadState, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (peer == excludePeer || (excludeSelf && peer == SelfPeer))
                continue;

            if (TryGetServerPlayer(peer, out var player) && player.LoadingState < minimumLoadState)
                continue;

            peer?.Send(writer, deliveryMethod);
        }
    }

    private void SendNetSerializablePacketToAll<T>(T packet, DeliveryMethod deliveryMethod, bool excludeSelf = false) where T : INetSerializable, new()
    {
        NetDataWriter writer = WriteNetSerializablePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (excludeSelf && peer == SelfPeer)
                continue;

            peer?.Send(writer, deliveryMethod);
        }
    }

    private void SendNetSerializablePacketToAll<T>(T packet, DeliveryMethod deliveryMethod, ITransportPeer excludePeer, bool excludeSelf = false) where T : INetSerializable, new()
    {
        NetDataWriter writer = WriteNetSerializablePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (peer == excludePeer || (excludeSelf && peer == SelfPeer))
                continue;
            peer?.Send(writer, deliveryMethod);
        }
    }

    #region Mod Packets
    public void SendExternalPacketToAll<T>(T packet, bool reliable, bool excludeSelf = false) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludeSelf);
    }

    public void SendExternalPacketToAll<T>(T packet, bool reliable, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;

        if (excludePeer == null)
            SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludeSelf);
        else
            SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludePeer, excludeSelf);
    }

    public void SendExternalSerializablePacketToAll<T>(T packet, bool reliable, bool excludeSelf = false) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };
        SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludeSelf);
    }

    public void SendExternalSerializablePacketToAll<T>(T packet, bool reliable, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };

        if (excludePeer == null)
            SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludeSelf);
        else
            SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludePeer, excludeSelf);
    }

    public void SendExternalPacketToPlayer<T>(T packet, ITransportPeer peer, bool reliable) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacket(peer, packet, deliveryMethod);
    }

    public void SendExternalSerializablePacketToPlayer<T>(T packet, ITransportPeer peer, bool reliable) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };

        SendNetSerializablePacket(peer, wrapper, deliveryMethod);
    }

    #endregion

    public void SendRpcResponse(uint ticketId, IRpcResponse response, ITransportPeer peer)
    {
        SendNetSerializablePacket
        (
            peer,
            new ClientboundRpcResponsePacket
            {
                TicketId = ticketId,
                Response = response
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void KickPlayer(ServerPlayer player)
    {
        if (player == null || player.Peer == null)
            return;

        player.Peer.Disconnect(WritePacket(new ClientboundDisconnectPacket { Kicked = true }));
    }

    public void SendGameParams(GameParams gameParams)
    {
        var packet = ClientboundGameParamsPacket.FromGameParams(gameParams);
        packet.FastTravelAdvancesTime = fastTravelAdvancesTime;
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForGameData, excludeSelf: true);
    }

    public void SendWeatherState(ITransportPeer peer = null)
    {
        var packet = WeatherDriver.Instance.GetSaveData(Globals.G.GameParams.WeatherEditorAlwaysAllowed).ToObject<ClientboundWeatherPacket>();

        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    public void SendSpawnTrainset(List<TrainCar> set, bool autoCouple, bool sendToAll, ITransportPeer sendTo = null)
    {

        LogDebug(() =>
        {
            StringBuilder sb = new();

            sb.Append($"SendSpawnTrainSet() Sending trainset {set?.FirstOrDefault()?.GetNetId()} with {set?.Count} cars");

            TrainCar[] noNetId = set?.Where(car => car.GetNetId() == 0).ToArray();

            if (noNetId.Length > 0)
                sb.AppendLine($"Erroneous cars!: {string.Join(", ", noNetId.Select(car => $"{{{car?.ID}, {car?.CarGUID}, {car.logicCar != null}}}"))}");

            return sb.ToString();

        });

        var packet = ClientboundSpawnTrainSetPacket.FromTrainSet(set, autoCouple);

        if (!sendToAll)
        {
            if (sendTo == null)
                LogError($"SendSpawnTrainSet() Trying to send to null peer!");
            else
                SendPacket(sendTo, packet, DeliveryMethod.ReliableOrdered);
        }
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendDestroyTrainCar(NetworkedTrainCar netTrainCar, ITransportPeer peer = null)
    {
        //ushort netID = trainCar.GetNetId();
        Log($"Sending DestroyTrainCarPacket for [{netTrainCar.CurrentID} {netTrainCar.NetId}]");

        if (netTrainCar.NetId == 0)
        {
            LogWarning($"SendDestroyTrainCar failed. [{netTrainCar.CurrentID} {netTrainCar.NetId}]");
            return;
        }

        var packet = new ClientboundDestroyTrainCarPacket { NetId = netTrainCar.NetId };

        if (peer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
        else
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainsetPhysicsUpdate(ClientboundTrainsetPhysicsPacket packet, bool reliable)
    {
        //LogDebug(() => $"Sending Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        SendPacketToAll(packet, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendBrakeState(ushort netId, float mainReservoirPressure, float brakePipePressure, float brakeCylinderPressure, float overheatPercent, float overheatReductionFactor, float temperature)
    {
        SendPacketToAll(new ClientboundBrakeStateUpdatePacket
        {
            NetId = netId,
            MainReservoirPressure = mainReservoirPressure,
            BrakePipePressure = brakePipePressure,
            BrakeCylinderPressure = brakeCylinderPressure,
            OverheatPercent = overheatPercent,
            OverheatReductionFactor = overheatReductionFactor,
            Temperature = temperature
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);

        //LogDebug(()=> $"Sending Brake Pressures netId {netId}: {mainReservoirPressure}, {independentPipePressure}, {brakePipePressure}, {brakeCylinderPressure}");
    }

    public void SendCargoState(NetworkedTrainCar netTraincar, bool isLoading, byte cargoModelIndex)
    {
        Car logicCar = netTraincar?.TrainCar?.logicCar;

        //LogDebug(() => $"SendCargoState({netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}), logicCar: {logicCar?.ID}, WareHouseMachineID: {logicCar.CargoOriginWarehouse?.ID}, warehouse track: {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}");

        Log($"Sending Cargo State for {netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}");

        if (logicCar == null)
        {
            LogWarning($"Attempted to send cargo state for {netTraincar?.CurrentID}, but logic car does not exist!");
            return;
        }

        CargoType cargoTypeV1 = isLoading ? logicCar.CurrentCargoTypeInCar : logicCar.LastUnloadedCargoType;

        CargoTypeLookup.Instance.TryGetNetId(cargoTypeV1, out uint cargoType);

        ushort netMachineId = 0;
        if (logicCar.CargoOriginWarehouse != null)
        {
            if (!WarehouseMachineLookup.TryGetNetId(logicCar.CargoOriginWarehouse, out netMachineId))
            {
                Log($"Attempting to send cargo state for {netTraincar.CurrentID}, for warehouse machine at track {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}, but Warehouse Machine was not found");
                return;
            }
        }

        SendPacketToAll(new ClientboundCargoStatePacket
        {
            NetId = netTraincar.NetId,
            IsLoading = isLoading,
            CargoTypeNetId = cargoType,
            CargoAmount = logicCar.LoadedCargoAmount,
            CargoHealth = netTraincar.TrainCar.CargoDamage.HealthPercentage,
            CargoModelIndex = cargoModelIndex,
            WarehouseMachineNetId = netMachineId,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeNetId, ServerPlayer sendToPlayer = null)
    {
        var packet = new CommonPaintThemePacket
        {
            NetId = netTraincar.NetId,
            TargetArea = targetArea,
            PaintThemeId = themeNetId
        };

        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        if (sendToPlayer != null)
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableUnordered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, true);
    }

    public void SendRestorationStateChange(ushort netId, LocoRestorationController.RestorationState newState, ushort[] transportCars)
    {
        var packet = new ClientboundRestorationStateChangePacket
        {
            NetId = netId,
            NewState = newState,
            TransportCarNetIds = transportCars
        };

        Log($"Sending restoration state change for {netId}, new state: {newState}, transport cars count: {transportCars?.Count() ?? 0}");

        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, true);
    }

    public void SendWarehouseControllerUpdate(ushort netId, bool isLoading, ushort jobNetId, ushort carNetId, uint cargoTypeNetId, WarehouseMachineController.TextPreset preset)
    {
        LogDebug(() => $"SendWarehouseControllerUpdate({netId}, {isLoading}, {jobNetId}, {carNetId}, {cargoTypeNetId}, {preset})");

        SendPacketToAll(new ClientboundWarehouseControllerUpdatePacket()
        {
            NetId = netId,
            IsLoading = isLoading,
            JobNetId = jobNetId,
            CarNetId = carNetId,
            CargoTypeNetId = cargoTypeNetId,
            Preset = (ushort)preset,
        },
        DeliveryMethod.Sequenced, PlayerLoadingState.ReadyForJobs, SelfPeer);
    }

    public void SendCargoHealthUpdate(ushort netId, float currentHealth)
    {
        SendPacketToAll(new ClientboundCargoHealthUpdatePacket
        {
            NetId = netId,
            CargoHealth = currentHealth,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendCarHealthUpdate(ushort netId, TrainCarHealthData health)
    {

        //LogDebug(() => $"Sending Car Health Update for netId {netId}: BodyHP: {health.BodyHP}, WheelsHP: {health.WheelsHP}, MechanicalPT: {health.MechanicalPT}, ElectricalPT: {health.ElectricalPT}, WindowsBroken: {health.WindowsBroken}");

        SendPacketToAll(new ClientboundCarHealthUpdatePacket
        {
            NetId = netId,
            Health = health
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendRerailTrainCar(ushort netId, ushort rerailTrack, Vector3 worldPos, Vector3 forward)
    {
        SendPacketToAll(new ClientboundRerailTrainPacket
        {
            NetId = netId,
            TrackId = rerailTrack,
            Position = worldPos,
            Forward = forward
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendMoveTrainCarToTrack(ushort netId, ushort destinationTrack, Vector3 worldPos, Vector3 forward, bool isTeleporting)
    {
        LogDebug(() => $"SendMoveTrainCarToTrack({netId}, {destinationTrack}, {worldPos}, {forward}, {isTeleporting})");
        SendPacketToAll
        (
            new ClientboundMoveTrainPacket
            {
                NetId = netId,
                TrackId = destinationTrack,
                Position = worldPos,
                Forward = forward,
                IsTeleporting = isTeleporting
            }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, true
        );
    }

    public void SendWindowsBroken(ushort netId, Vector3 forceDirection)
    {
        SendPacketToAll
        (
            new ClientboundWindowsBrokenPacket
            {
                NetId = netId,
                ForceDirection = forceDirection
            }, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, SelfPeer
        );
    }

    public void SendWindowsRepaired(ushort netId)
    {
        SendPacketToAll
        (
            new ClientboundWindowsRepairedPacket
            {
                NetId = netId
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForTrainSets,
            excludeSelf: true
        );
    }

    public void SendMoney(float amount)
    {
        SendPacketToAll
        (
            new ClientboundMoneyPacket
            {
                Amount = amount
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendLicense(string id, bool isJobLicense)
    {
        SendPacketToAll
        (
            new ClientboundLicenseAcquiredPacket
            {
                Id = id,
                IsJobLicense = isJobLicense
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendGarage(string id)
    {
        SendPacketToAll
        (
            new ClientboundGarageUnlockPacket
            {
                Id = id
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendDebtStatus(bool hasDebt)
    {
        SendPacketToAll
        (
            new ClientboundDebtStatusPacket
            {
                HasDebt = hasDebt
            }, DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            SelfPeer
        );
    }

    public void SendPlayerPreferencesUpdate(ServerPlayer player, Dictionary<PlayerPreference, string> preferences)
    {
        Log($"Sending player preferences update for '{player.Username}'");

        var packet = new ClientboundPlayerPreferencesUpdatePacket
        {
            PlayerId = player.PlayerId,
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        };

        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete);
    }

    public void SendTrainUncouple(Coupler coupler, bool playAudio, bool dueToBrokenCouple, bool viaChainInteraction)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendTrainUncouple failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendTrainUncouple({coupler.train.ID}, {coupler.isFrontCoupler}, {dueToBrokenCouple}, {viaChainInteraction})");

        SendPacketToAll(
            new CommonTrainUncouplePacket
            {
                NetId = couplerNetId,
                IsFrontCoupler = coupler.isFrontCoupler,
                PlayAudio = playAudio,
                ViaChainInteraction = viaChainInteraction,
                DueToBrokenCouple = dueToBrokenCouple,
            },
            DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets,
            excludeSelf: true
        );
    }

    public void SendHoseDisconnected(Coupler coupler, bool playAudio)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendHoseDisconnected failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendHoseDisconnected({coupler.train.ID}, {coupler.isFrontCoupler}, {playAudio})");

        SendPacketToAll
        (
            new CommonHoseDisconnectedPacket
            {
                NetId = couplerNetId,
                IsFront = coupler.isFrontCoupler,
                PlayAudio = playAudio
            },
            DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets,
            excludeSelf: true
        );
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        SendPacketToAll
        (
            new CommonCockFiddlePacket
            {
                NetId = netId,
                IsFront = coupler.isFrontCoupler,
                IsOpen = isOpen
            },
            DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets,
            true
        );
    }

    public void SendTrainControlAuthorityUpdate(ushort netId, uint portNetId, ControlAuthorityState state, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        var packet = new ClientboundTrainControlAuthorityUpdatePacket
        {
            NetId = netId,
            PortNetId = portNetId,
            State = state
        };

        if (sendToPlayer == null)
            if (excludePlayer == null)
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludeSelf: false);
            else
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludePlayer.Peer, false);
        else
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobsCreatePacket(NetworkedStationController networkedStation, NetworkedJob[] jobs, ITransportPeer peer = null)
    {
        Log($"Sending JobsCreatePacket for stationNetId {networkedStation.NetId} with {jobs.Count()} jobs");

        var packet = ClientboundJobsCreatePacket.FromNetworkedJobs(networkedStation, jobs);

        if (peer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForJobs, excludeSelf: true);
        else
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobsUpdatePacket(uint stationNetId, NetworkedJob[] jobs)
    {
        Multiplayer.Log($"Sending JobsUpdatePacket for stationNetId {stationNetId} with {jobs.Count()} jobs");
        SendPacketToAll(ClientboundJobsUpdatePacket.FromNetworkedJobs(stationNetId, jobs), DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForJobs, excludeSelf: true);
    }

    public void SendTaskUpdate(ushort taskNetId, TaskState newState, float taskStartTime, float taskFinishTime)
    {
        Multiplayer.Log($"Sending TaskUpdate for taskNetId {taskNetId}, newState {newState}");
        SendPacketToAll
        (
            new ClientboundTaskUpdatePacket
            {
                TaskNetId = taskNetId,
                NewState = newState,
                TaskStartTime = taskStartTime,
                TaskFinishTime = taskFinishTime
            },
            DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForJobs,
            excludeSelf: true
        );
    }

    public void SendItemsChangePacket(List<ItemUpdateData> items, ServerPlayer player)
    {
        Log($"Sending SendItemsChangePacket with {items.Count()} items to {player.Username}");

        if (player.Peer != null && player.Peer != SelfPeer)
        {
            for (int offset = 0; offset < items.Count; offset += NetworkedItemManager.MaxServerBatchItems)
            {
                int count = Math.Min(NetworkedItemManager.MaxServerBatchItems, items.Count - offset);
                SendNetSerializablePacket(player.Peer,
                    new CommonItemChangePacket { Items = items.GetRange(offset, count) }, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    public void SendPitStopBulkDataPacket(ushort netId, int carCount, int carIndex, int faucetNotch, LocoResourceModuleData[] stationData, PitStopPlugData[] plugData, ServerPlayer player)
    {
        LogDebug(() => $"SendPitStopBulkDataPacket({netId}, {carCount}, {carIndex}, {faucetNotch}, {stationData.Count()}, {plugData.Count()}, {player})");

        var packet = new ClientboundPitStopBulkUpdatePacket
        {
            NetId = netId,
            CarCount = carCount,
            CarSelection = carIndex,
            FaucetNotch = faucetNotch,
            ResourceData = stationData,
            PlugData = plugData,
        };

        if (player.Peer != SelfPeer)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopInteractionPacket(ServerPlayer player, CommonPitStopInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({player.Username}, {packet.NetId})");

        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket(ServerPlayer player, CommonPitStopPlugInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({packet.NetId}, {packet.InteractionType}, {packet.PlayerId}, {packet.Position}, {packet.Rotation}, {packet.TrainCarNetId}, {packet.SocketIndex}, {packet.YankForce}, {packet.YankMode})");
        SendNetSerializablePacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendCashRegisterAction(CommonCashRegisterWithModulesActionPacket packet, ServerPlayer[] players = null)
    {
        if (players == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
        else
            foreach (var player in players)
                SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendGenericSwitchState(uint netId, bool isOn, ServerPlayer player = null)
    {
        var packet = new CommonGenericSwitchStatePacket
        {
            NetId = netId,
            IsOn = isOn
        };

        if (player != null)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, deliveryMethod: DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
    }

    public void SendChat(string message, ServerPlayer exclude = null)
    {
        var packet = new CommonChatPacket
        {
            message = message
        };

        if (exclude != null)
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete, exclude.Peer);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete);
    }

    public void SendWhisper(string message, ServerPlayer recipient)
    {
        if (!string.IsNullOrEmpty(message) && recipient != null && recipient.Peer != null)
        {
            NetworkLifecycle.Instance.Server.SendPacket
            (
                recipient.Peer,
                new CommonChatPacket
                {
                    message = message
                },
                DeliveryMethod.ReliableUnordered
            );
        }
    }

    #endregion

    #region Listeners

    private void OnServerboundClientLoginPacket(ServerboundClientLoginPacket packet, IConnectionRequest request)
    {
        string remote = Multiplayer.Settings.LogIps ? $" at {request?.RemoteEndPoint?.Address}" : "";
        if (packet == null || packet.Mods == null ||
            !LoginRequestPolicy.IsValidEnvelope(packet.Username, packet.Guid, packet.Password,
                packet.BuildVersion, packet.CharacterId, packet.Mods?.Length ?? -1) ||
            packet.Mods.Any(mod => !LoginRequestPolicy.IsValidMod(mod.Id, mod.Version)))
        {
            LogWarning($"Denied malformed login request{remote}");
            request?.Reject();
            return;
        }

        Log($"Received login request from {packet.Username}{remote}");

        LogDebug(() => $"OnServerboundClientLoginPacket from {packet.Username}");

        // clean up username - remove leading/trailing white space, swap spaces for underscores and truncate
        packet.Username = packet.Username.Trim().Replace(' ', '_').Truncate(Settings.MAX_USERNAME_LENGTH);
        string overrideUsername = packet.Username;

        // ensure the username is unique
        int uniqueName = ServerPlayers.Where(player => player.OriginalUsername.ToLower() == packet.Username.ToLower()).Count();

        if (uniqueName > 0)
        {
            overrideUsername += uniqueName;
        }

        Guid guid;
        try
        {
            guid = new Guid(packet.Guid);
        }
        catch (ArgumentException)
        {
            // This can only happen if the sent GUID is tampered with, in which case, we aren't worried about showing a message.
            Log($"Invalid GUID from {packet.Username}{remote}");
            request.Reject();
            return;
        }

        Log($"Processing login packet for {packet.Username} ({guid}){remote}");

        if (Multiplayer.Settings.Password != packet.Password)
        {
            LogWarning("Denied login due to invalid password!");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__INVALID_PASSWORD_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        string expectedBuild = ProtocolCompatibility.HandshakeBuild(MainMenuControllerPatch.MenuProvider.BuildVersionString);
        if (packet.BuildVersion != expectedBuild)
        {
            LogWarning($"Denied login to incompatible game/protocol version! Got: {packet.BuildVersion}, expected: {expectedBuild}");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__GAME_VERSION_KEY,
                ReasonArgs = [expectedBuild, packet.BuildVersion?.ToString() ?? ""]
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        if (PlayerCount >= Multiplayer.Settings.MaxPlayers || IsSinglePlayer && PlayerCount >= 1)
        {
            LogWarning("Denied login due to server being full!");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__FULL_SERVER_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        var validation = ModCompatibilityManager.Instance.ValidateClientMods(packet.Mods);
        if (!validation.IsValid)
        {

            LogWarning($"Denied login due to mod mismatch! {validation.Missing.Count} missing, {validation.Extra} extra");
            LogDebug(() =>
            {
                StringBuilder sb = new("Mod mis-match:");
                sb.AppendLine("Server Mods:");
                foreach (ModInfo mod in ModCompatibilityManager.Instance.GetLocalMods())
                    sb.AppendLine($"\t{mod.Id} {mod.Version}, Status: {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

                sb.AppendLine("\r\nClient Mods:");
                foreach (ModInfo mod in packet.Mods)
                    sb.AppendLine($"\t{mod.Id} {mod.Version}, Status (if known): {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

                sb.AppendLine("\r\nMissing Mods:");
                foreach (ModInfo mod in validation.Missing)
                    sb.AppendLine($"\t{mod.Id} {mod.Version}, Status: {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

                sb.AppendLine("\r\nExtra Mods:");
                foreach (ModInfo mod in validation.Extra)
                    sb.AppendLine($"\t{mod.Id} {mod.Version}, Status (if known): {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

                return sb.ToString();
            });

            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__MODS_KEY,
                Missing = validation.Missing.ToArray(),
                Extra = validation.Extra.ToArray(),
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        // Unpause physics
        if (AppUtil.Instance.IsTimePaused)
            AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

        ITransportPeer peer = request.Accept();

        ServerPlayer serverPlayer = new
        (
            peer,
            overrideUsername,
            packet.Username,
            guid,
            packet.CharacterId,
            packet.IsVR
        );

        serverPlayers.Add(serverPlayer.PlayerId, serverPlayer);
        peerToPlayer.Add(peer, serverPlayer);

        ClientboundLoginResponsePacket acceptPacket = new()
        {
            Accepted = true,
            PlayerId = serverPlayer.PlayerId,
            OverrideUsername = serverPlayer.OriginalUsername == serverPlayer.Username ? string.Empty : overrideUsername,
        };

        SendPacket(peer, acceptPacket, DeliveryMethod.ReliableUnordered);
    }

    private void OnWorldItemRecovery(ServerboundWorldItemRecoveryPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || player.LoadingState != PlayerLoadingState.ReadyForItems ||
            player.InitialWorldItems == null || packet.ItemIds == null || packet.ItemIds.Length == 0 ||
            packet.ItemIds.Length > LoadingRecovery.MaxBatch || packet.ItemIds.Distinct().Count() != packet.ItemIds.Length ||
            packet.ItemIds.Any(id => !player.InitialWorldItems.Contains(id))) return;
        if (!player.WorldItemRecovery.TryTake(packet.ItemIds, out var ids)) return;
        var updates = new List<ItemUpdateData>();
        foreach (var id in ids)
        {
            if (NetworkedItem.TryGet(id, out var item) && item != null && item.CanApplySnapshot)
                updates.Add(item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create));
            else
                updates.Add(new ItemUpdateData { ItemNetId = id, UpdateType = ItemUpdateData.ItemUpdateType.Destroy });
        }
        SendItemsChangePacket(updates.Where(item => item != null).ToList(), player);
    }

    private void OnTrainRecovery(ServerboundTrainRecoveryPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || player.LoadingState != PlayerLoadingState.ReadyForTrainSets ||
            player.InitialTrainCars == null || packet?.CarNetIds == null || packet.CarNetIds.Length == 0 ||
            packet.CarNetIds.Length > LoadingRecovery.MaxBatch ||
            packet.CarNetIds.Distinct().Count() != packet.CarNetIds.Length ||
            packet.CarNetIds.Any(id => !player.InitialTrainCars.Contains(id))) return;
        if (!player.TrainRecovery.TryTake(packet.CarNetIds, out var ids)) return;

        var sent = new HashSet<Trainset>();
        foreach (var id in ids)
            if (NetworkedTrainCar.TryGet(id, out NetworkedTrainCar car) && car?.TrainCar?.trainset?.cars != null &&
                sent.Add(car.TrainCar.trainset))
                SendSpawnTrainset(car.TrainCar.trainset.cars, false, false, peer);
    }

    private void OnTrainManifestRecovery(ServerboundTrainManifestRecoveryPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || player.LoadingState != PlayerLoadingState.ReadyForTrainSets ||
            player.InitialTrainCars == null || !player.TrainManifestRecovery.TryTake()) return;
        SendPacket(peer, new ClientboundLoadStateInfoPacket
        {
            LoadingState = PlayerLoadingState.ReadyForTrainSets,
            ItemsToLoad = player.InitialTrainsetCount
        }, DeliveryMethod.ReliableOrdered);
        SendPacket(peer, new ClientboundTrainManifestPacket
        {
            CarNetIds = player.InitialTrainCars.ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnRailwayStateRecovery(ServerboundRailwayStateRecoveryPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out var player) && player.LoadingState == PlayerLoadingState.ReadyForWorldState)
            SendRailwayState(peer);
    }

    private void SendRailwayState(ITransportPeer peer)
    {
        SendPacket(peer, new ClientboundRailwayStatePacket
        {
            SelectedJunctionBranches = NetworkedJunction.IndexedJunctions.Select(j => j.Junction.selectedBranch).ToArray(),
            TurntableRotations = NetworkedTurntable.IndexedTurntables.Select(j => j.TurntableRailTrack.currentYRotation).ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundLoadStateUpdatePacket(ServerboundLoadStateUpdatePacket packet, ITransportPeer peer)
    {
        LogDebug(() => $"OnServerboundLoadStateUpdatePacket from peerId: {peer.Id}, loadState: {packet.LoadState}");
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogError($"Load state update received for {peer.GetType()}, peerId: {peer.Id}, but ServerPlayer not found");
            peer.Disconnect();
            return;
        }

        if (player.LoadingState >= packet.LoadState)
        {
            LogWarning($"Player {player.Username} reported load state {packet.LoadState}, but is currently at load state {player.LoadingState}!");
            KickPlayer(player);
            return;
        }
        if (packet.LoadState > PlayerLoadingState.ReadyForItems && !NetworkLifecycle.Instance.IsHost(player) &&
            !player.InventoryRestoreComplete)
        {
            LogWarning($"Player {player.Username} skipped inventory restoration.");
            KickPlayer(player);
            return;
        }

        switch (packet.LoadState)
        {
            case PlayerLoadingState.None:
                LogWarning($"Player {player.Username} sent unexpected state: {packet.LoadState}");

                break;

            case PlayerLoadingState.ReadyForGameData:
                Log($"Player {player.Username} is ready for game data");

                PlayerConnected?.Invoke(player);

                var gameParamsPacket = ClientboundGameParamsPacket.FromGameParams(Globals.G.GameParams);
                gameParamsPacket.FastTravelAdvancesTime = fastTravelAdvancesTime;

                SendPacket(peer, gameParamsPacket, DeliveryMethod.ReliableOrdered);
                SendPacket(peer, ClientboundSaveGameDataPacket.CreatePacket(player), DeliveryMethod.ReliableOrdered);

                break;

            case PlayerLoadingState.ReadyForWorldState:
                if (!IsLoaded)
                {
                    Log($"Player {player.Username} is ready but server is still loading, adding to the queue");

                    joinQueue.Enqueue(peer);
                    SendPacket(peer, new ClientboundServerLoadingPacket(), DeliveryMethod.ReliableOrdered);

                    return;
                }
                else
                {
                    Log($"Player {player.Username} is ready for world state");
                }

                // Unpause physics
                if (AppUtil.Instance.IsTimePaused)
                    AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

                // Allow the player to receive packets
                peers.Add(player.PlayerId, peer);

                if (NetworkLifecycle.Instance.IsHost(player))
                {
                    Log($"Server loaded. Triggering loading screen removal");
                    packet.LoadState = PlayerLoadingState.Complete;
                    break;
                }

                // Send weather state
                SendWeatherState(peer);

                // Send junctions and turntables
                SendRailwayState(peer);

                // Send generic switch states
                foreach (var genericSwitch in NetworkedGenericSwitch.AllSwitches)
                {
                    SendPacket(peer, new CommonGenericSwitchStatePacket
                    {
                        NetId = genericSwitch.NetId,
                        IsOn = genericSwitch.IsOn
                    }, DeliveryMethod.ReliableOrdered);
                }

                break;

            case PlayerLoadingState.ReadyForTrainSets:
                var initialTrainsets = Trainset.allSets.Where(set => set?.cars != null).ToArray();
                // Send trains
                var trainManifest = new List<ushort>();
                uint sentTrainsets = 0;
                foreach (Trainset set in initialTrainsets)
                {
                    try
                    {
                        SendSpawnTrainset(set.cars, false, false, peer);
                        trainManifest.AddRange(set.cars.Select(car => car.GetNetId()).Where(id => id != 0));
                        sentTrainsets++;
                    }
                    catch (Exception e)
                    {
                        LogWarning($"Exception when trying to send train set spawn data for [{set?.firstCar?.ID}, {set?.firstCar?.GetNetId()}]\r\n{e.Message}\r\n{e.StackTrace}");
                    }
                }
                SendPacket(peer, new ClientboundLoadStateInfoPacket
                {
                    LoadingState = PlayerLoadingState.ReadyForTrainSets,
                    ItemsToLoad = sentTrainsets
                }, DeliveryMethod.ReliableOrdered);
                player.InitialTrainCars = new HashSet<ushort>(trainManifest);
                player.InitialTrainsetCount = sentTrainsets;
                SendPacket(peer, new ClientboundTrainManifestPacket
                {
                    CarNetIds = player.InitialTrainCars.ToArray()
                }, DeliveryMethod.ReliableOrdered);

                break;

            case PlayerLoadingState.ReadyForItems:
                player.InitialTrainCars = null;
                NetworkedItemManager.Instance.RestorePlayerInventory(player,
                    items =>
                    {
                        SendPacket(peer, new ClientboundInventoryRestorePacket { Items = items }, DeliveryMethod.ReliableOrdered);
                        var worldItems = NetworkedItemManager.Instance.CaptureInitialWorldItems(player);
                        player.InitialWorldItems = new HashSet<ushort>(worldItems.Select(item => item.ItemNetId));
                        SendItemsChangePacket(worldItems, player);
                        SendPacket(peer, new ClientboundWorldItemManifestPacket { ItemIds = player.InitialWorldItems.ToArray() }, DeliveryMethod.ReliableOrdered);
                    },
                    error =>
                    {
                        LogError($"Inventory restoration failed for {player.Username}: {error}");
                        SendPacket(peer, new ClientboundInventoryRestorePacket
                        {
                            Items = Array.Empty<PlayerItemSaveData>(), Error = error.Message
                        }, DeliveryMethod.ReliableOrdered);
                    });

                break;

            case PlayerLoadingState.ReadyForJobs:
                // This transition acknowledges application of the initial world item manifest.
                player.InitialWorldItems = null;

                var jobManifest = new List<ushort>();
                // Send Job Data
                foreach (StationController station in StationController.allStations)
                {
                    if (NetworkedStationController.GetFromStationController(station, out NetworkedStationController netStation))
                    {
                        //only send active jobs (available or in progress) - new clients don't need to know about old jobs
                        NetworkedJob[] jobs = netStation.NetworkedJobs
                            .Where(j => j.Job.State == JobState.Available || j.Job.State == JobState.InProgress)
                            .ToArray();

                        for (int i = 0; i < jobs.Length; i++)
                        {
                            SendJobsCreatePacket(netStation, [jobs[i]], peer);
                            jobManifest.Add(jobs[i].NetId);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Sending job packets... Failed to get NetworkedStation from station {station?.stationInfo?.Name}");
                    }
                }
                SendPacket(peer, new ClientboundJobManifestPacket { JobIds = jobManifest.ToArray() }, DeliveryMethod.ReliableOrdered);
                break;

            case PlayerLoadingState.ReadyForTiles:
                // Send Hazmat data
                break;

            case PlayerLoadingState.Complete:

                break;

            default:
                LogWarning($"Player {player.Username} sent unexpected load state: {packet.LoadState}");
                KickPlayer(player);

                break;
        }

        player.LoadingState = packet.LoadState;

        if (packet.LoadState == PlayerLoadingState.Complete)
        {
            Log($"Player {player.Username} has completed loading");

            // Send the new player to all other players
            ClientboundPlayerJoinedPacket clientboundPlayerJoinedPacket = new()
            {
                PlayerId = player.PlayerId,
                Username = player.Username,
                IsVR = player.IsVR,
                CharacterId = player.CharacterId,
                CrewName = player.CrewName,
                TrackingData = player.TrackingData,
                Posture = player.Posture,
                IsOnCar = player.CarId != 0,
                CarID = player.CarId,
            };

            SendPacketToAll(clientboundPlayerJoinedPacket, DeliveryMethod.ReliableOrdered, PlayerLoadingState.Complete, peer);

            // Announce player joined
            ChatManager.ServerMessage(player.Username + " joined the game", null, player);

            // Send existing players
            foreach (ServerPlayer otherPlayer in ServerPlayers)
            {
                if (player.PlayerId == otherPlayer.PlayerId)
                    continue;

                SendPacket(peer, new ClientboundPlayerJoinedPacket
                {
                    PlayerId = otherPlayer.PlayerId,
                    Username = otherPlayer.Username,
                    CharacterId = otherPlayer.CharacterId,
                    IsVR = otherPlayer.IsVR,
                    CrewName = otherPlayer.CrewName,
                    CarID = otherPlayer.CarId,
                    TrackingData = otherPlayer.TrackingData,  // full merged state
                    Posture = otherPlayer.Posture,
                    IsOnCar = otherPlayer.CarId != 0,
                }, DeliveryMethod.ReliableOrdered);
            }

            SendPacket(peer, new ClientboundRemoveLoadingScreenPacket(), DeliveryMethod.ReliableOrdered);
            PlayerReady?.Invoke(player);
        }
    }

    private void OnServerboundPlayerPositionPacket(ServerboundPlayerPositionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"Received Player Position from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        // If the player's car has changed, remove the player from the old car
        if (packet.CarID == 0 && player.CarId != 0)
        {
            if (NetworkedTrainCar.TryGet(player.CarId, out NetworkedTrainCar currentCar) && currentCar != null)
                currentCar.Server_RemovePlayer(player);
        }

        // If the player is on a car, update the car's player list
        if (packet.CarID != 0 && NetworkedTrainCar.TryGet(packet.CarID, out NetworkedTrainCar newCar) && newCar != null)
            newCar.Server_PlayerOnCar(player);

        // Merge incoming delta into stored state
        player.TrackingData = player.TrackingData.MergeFrom(packet.TrackingData);
        player.CarId = packet.CarID;
        player.Posture = packet.Posture;

        SendPacketToAll(new ClientboundPlayerPositionPacket
        {
            PlayerId = player.PlayerId,
            TrackingData = packet.TrackingData,
            Posture = packet.Posture,
            IsOnCar = packet.IsOnCar,
            CarID = packet.CarID
        }, DeliveryMethod.Sequenced, PlayerLoadingState.Complete, peer);
    }

    private void OnServerboundPlayerPreferenceUpdatePacket(ServerboundPlayerPreferenceUpdatePacket packet, ITransportPeer peer)
    {
        Dictionary<PlayerPreference, string> preferences = [];

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"Received Player Preferences Update from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        // Store the characterId for other players connecting to the server
        if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CharacterModel, out string characterId))
        {
            player.CharacterId = characterId;
            preferences.Add(PlayerPreference.CharacterModel, characterId);
        }

        if (preferences.Count > 0)
            SendPlayerPreferencesUpdate(player, preferences);
    }

    public bool AllowsAction(ServerPlayer player, bool enabled) => player != null &&
        ServerActionPolicy.Allowed(player.LoadingState == PlayerLoadingState.Complete,
            player.PlayerId == SelfId, enabled);

    private void RejectAction(ITransportPeer peer, string action, string reason)
    {
        SendPacket(peer, new CommonChatPacket { message = $"{action} refused: {reason}." }, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundTimeAdvancePacket(ServerboundTimeAdvancePacket packet, ITransportPeer peer)
    {

        if (!TryGetServerPlayer(peer, out var sender) ||
            !AllowsAction(sender, Multiplayer.Settings.AllowClientTimeAdvance) ||
            !ServerActionPolicy.Finite(packet.amountOfTimeToSkipInSeconds) || packet.amountOfTimeToSkipInSeconds < 0 ||
            packet.amountOfTimeToSkipInSeconds > 86400)
        {
            RejectAction(peer, "Time advance", "not ready, permission denied or invalid duration");
            return;
        }
        if (!fastTravelAdvancesTime)
        {
            TryGetServerPlayer(peer, out ServerPlayer player);
            LogWarning($"Player {player?.Username} sent a TimeAdvance request, but FastTravelAdvancesTime is disabled");
            return;
        }

        SendPacketToAll
        (
            new ClientboundTimeAdvancePacket
            {
                amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            peer
        );
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || !AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            !NetworkedJunction.Get(packet.NetId, out var junction) || junction?.Junction == null ||
            !Enum.IsDefined(typeof(Junction.SwitchMode), (Junction.SwitchMode)packet.Mode) ||
            !ServerActionPolicy.JunctionBranch(packet.SelectedBranch, junction.Junction.outBranches?.Count ?? 0))
        {
            RejectAction(peer, "Junction", "not ready, permission denied or invalid target/state");
            return;
        }
        // Map/radio switching is intentionally available remotely.
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || !AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            !NetworkedTurntable.Get(packet.NetId, out var turntable) || turntable?.TurntableRailTrack == null ||
            !ServerActionPolicy.Finite(packet.rotation))
        {
            RejectAction(peer, "Turntable", "not ready, permission denied or invalid target/rotation");
            return;
        }
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet, ITransportPeer peer)
    {
        if (!peerToPlayer.TryGetValue(peer, out var player))
        {
            LogWarning($"Received Coupler Interaction from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        //todo: add validation that to ensure the client is near the coupler - this packet may also be used for remote operations and may need to factor that in in the future
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            if (netTrainCar.Server_ValidateCouplerInteraction(packet, player))
            {
                //passed validation, send to all but the originator
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
            }
            else
            {
                LogDebug(() => $"OnCommonCouplerInteractionPacket([{packet.Flags}, {netTrainCar.CurrentID}, {packet.NetId}], {player.PlayerId}) Sending validation failure");
                //failed validation notify client
                SendPacket
                (
                    peer,
                    new CommonCouplerInteractionPacket
                    {
                        NetId = packet.NetId,
                        Flags = (ushort)CouplerInteractionType.NoAction,
                        IsFrontCoupler = packet.IsFrontCoupler,
                    },
                    DeliveryMethod.ReliableOrdered
                );
            }
        }
        else
        {
            LogDebug(() => $"OnCommonCouplerInteractionPacket([{packet.Flags}, {netTrainCar.CurrentID}, {packet.NetId}], {player.PlayerId}) Sending destroy");
            //Car doesn't exist, tell client to delete it
            SendDestroyTrainCar(netTrainCar, peer);
        }
    }

    //private void OnCommonTrainCouplePacket(CommonTrainCouplePacket packet, ITransportPeer peer)
    //{
    //    SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    //}

    private void OnCommonTrainUncouplePacket(CommonTrainUncouplePacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonHoseConnectedPacket(CommonHoseConnectedPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonHoseDisconnectedPacket(CommonHoseDisconnectedPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonMuConnectedPacket(CommonMuConnectedPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonMuDisconnectedPacket(CommonMuDisconnectedPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonCockFiddlePacket(CommonCockFiddlePacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonBrakeCylinderReleasePacket(CommonBrakeCylinderReleasePacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonHandbrakePositionPacket(CommonHandbrakePositionPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            packet.TargetArea != TrainCarPaint.Target.Interior && packet.TargetArea != TrainCarPaint.Target.Exterior)
        {
            RejectAction(peer, "Train paint", "not ready, permission denied or invalid target");
            return;
        }

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");

        netTrainCar?.Server_ValidatePaintThemeChange(packet.TargetArea, paint, player);

        //SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnServerboundAddCoalPacket(ServerboundAddCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        //is value valid?
        if (!ServerActionPolicy.Finite(packet.CoalMassDelta))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to add coal?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.firebox?.fireboxCoalControlPort.ExternalValueUpdate(packet.CoalMassDelta);
        }
    }

    private void OnServerboundTenderCoalPacket(ServerboundTenderCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        // is value valid?
        if (!ServerActionPolicy.Finite(packet.CoalMassDelta))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to add/remove coal?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.coalPile?.coalConsumePort.ExternalValueUpdate(packet.CoalMassDelta);
        }
    }

    private void OnServerboundFireboxIgnitePacket(ServerboundFireboxIgnitePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to ignite firebox?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.firebox?.Ignite();
        }
    }

    private void OnCommonTrainPortsPacket(CommonTrainPortsPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) || !AllowsAction(player, true))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (packet.PortIds == null || packet.PortValues == null ||
            !ServerActionPolicy.ParallelPayload(packet.PortIds.Length, packet.PortValues.Length) ||
            packet.PortValues.Any(value => !ServerActionPolicy.Finite(value)))
        {
            RejectAction(peer, "Train control", "invalid port payload");
            return;
        }

        //if not the host && validation fails then ignore packet
        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            bool flag = networkedTrainCar.Server_ValidateClientSimFlowPacket(player, packet);

            //LogDebug(() => $"OnCommonTrainPortsPacket from {player.Username}, Not host, valid: {flag}");
            if (!flag)
            {
                return;
            }
        }

        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnServerboundTrainControlAuthorityPacket(ServerboundTrainControlAuthorityPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) || !AllowsAction(player, true))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Server_ReceiveAuthorityRequest(packet.PortNetId, player, packet.RequestAuthority);
    }

    private void OnCommonTrainFusesPacket(CommonTrainFusesPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (packet.FuseIds == null || packet.FuseValues == null ||
            !ServerActionPolicy.ParallelPayload(packet.FuseIds.Length, packet.FuseValues.Length) ||
            !NetworkLifecycle.Instance.IsHost(player) && !networkedTrainCar.Server_ValidateClientFusesPacket(player, packet))
        {
            RejectAction(peer, "Train fuse", "invalid payload, target or distance");
            return;
        }
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, peer);
    }

    private void OnServerboundTrainSyncRequestPacket(ServerboundTrainSyncRequestPacket packet, ITransportPeer peer)
    {
        if (packet.NetId == 0 || !TryGetServerPlayer(peer, out var player) ||
            player.LoadingState < PlayerLoadingState.ReadyForTrainSets) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < player.NextTrainRepairAt) return;
        player.NextTrainRepairAt = now + System.Diagnostics.Stopwatch.Frequency;
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
        {
            var cars = networkedTrainCar.TrainCar.trainset.cars;
            if (cars.Any(c => c == null || c.IsTeleporting || IsFastTravelCar(c.GetNetId())) || cars.Count > TrainCompositionPolicy.MaxCars) return;
            SendPacket(peer, new ClientboundTrainRepairPacket { Parts = TrainsetSpawnPart.FromTrainSet(cars) }, DeliveryMethod.ReliableOrdered);
            foreach (var car in cars)
                if (car.TryNetworked(out NetworkedTrainCar netCar)) netCar.Server_DirtyAllState();
        }
        else
            SendPacket(peer, new ClientboundDestroyTrainCarPacket { NetId = packet.NetId }, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundTrainDeleteRequestPacket(ServerboundTrainDeleteRequestPacket packet, ITransportPeer peer)
    {
        if (IsFastTravelCar(packet.NetId)) return;
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!AllowsAction(player, Multiplayer.Settings.AllowClientTrainManagement))
        {
            RejectAction(peer, "Train management", "not ready or permission denied");
            return;
        }
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (networkedTrainCar.HasPlayers())
        {
            LogWarning($"{player.Username} tried to delete a train with players in it!");
            return;
        }

        TrainCar trainCar = networkedTrainCar.TrainCar;
        float cost = trainCar.playerSpawnedCar ? 0.0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);
        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to delete a train without enough money to do so!");
            return;
        }

        Job job = JobsManager.Instance.GetJobOfCar(trainCar.logicCar);
        switch (job?.State)
        {
            case JobState.Available:
                job.ExpireJob();
                break;
            case JobState.InProgress:
                JobsManager.Instance.AbandonJob(job);
                break;
        }

        var garageRef = trainCar.GetComponent<HomeGarageReference>();
        if (garageRef != null && garageRef.garageCarSpawner != null)
        {
            // Clear the countdown so players can request the car immediately
            trainCar.visitChecker?.recentlyVisitedTimer?.StopCountdown();
            garageRef.garageCarSpawner.ReturnCarHome(trainCar);
        }
        else
        {
            CarSpawner.Instance.DeleteCar(trainCar);
            UnusedTrainCarDeleter.Instance.ClearInvalidCarReferencesAfterManualDelete();
        }
    }

    private void OnServerboundTrainRerailRequestPacket(ServerboundTrainRerailRequestPacket packet, ITransportPeer peer)
    {
        if (IsFastTravelCar(packet.NetId)) return;
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!AllowsAction(player, Multiplayer.Settings.AllowClientTrainManagement))
        {
            RejectAction(peer, "Train management", "not ready or permission denied");
            return;
        }
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;

        TrainCar trainCar = networkedTrainCar.TrainCar;
        Vector3 position = packet.Position + WorldMover.currentMove;
        var rerailPoints = networkedRailTrack.RailTrack.GetKinkedPointSet()?.points;
        bool hasRerailPoints = rerailPoints != null && rerailPoints.Length > 0;
        var closestRerailPoint = hasRerailPoints
            ? rerailPoints.OrderBy(point => ((Vector3)point.position - packet.Position).sqrMagnitude).First()
            : default;
        bool positionMatchesTrack = hasRerailPoints && ServerActionPolicy.TrackAlignment(
            ((Vector3)closestRerailPoint.position - packet.Position).sqrMagnitude,
            Mathf.Abs(Vector3.Dot(packet.Forward.normalized, ((Vector3)closestRerailPoint.forward).normalized)));
        if (!ServerActionPolicy.InRange((player.WorldPosition - position).sqrMagnitude, CommsRadioCarSpawner.SIGNAL_RANGE) ||
            !ServerActionPolicy.InRange((player.WorldPosition - trainCar.transform.position).sqrMagnitude, CommsRadioCarSpawner.SIGNAL_RANGE) ||
            !ServerActionPolicy.Finite(packet.Forward.sqrMagnitude) || packet.Forward.sqrMagnitude < 0.5f || packet.Forward.sqrMagnitude > 1.5f ||
            !positionMatchesTrack ||
            networkedTrainCar.HasPlayers())
        {
            RejectAction(peer, "Rerail", "position/track mismatch, invalid direction, occupied train or out of radio range");
            return;
        }

        //Check if player is a Newbie (currently shared with all players)
        float cost = TutorialHelper.InRestrictedMode || rerailController != null && rerailController.isPlayerNewbie ? 0f :
            RerailController.CalculatePrice((networkedTrainCar.transform.position - position).magnitude, trainCar.carType, Globals.G.GameParams.RerailMaxPrice);

        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to rerail a train without enough money to do so!");
            return;
        }

        trainCar.Rerail(networkedRailTrack.RailTrack, position, packet.Forward);
    }

    private void OnServerboundTrainSpawnRequestPacket(ServerboundTrainSpawnRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!AllowsAction(player, Multiplayer.Settings.AllowClientTrainManagement))
        {
            RejectAction(peer, "Train management", "not ready or permission denied");
            return;
        }

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to spawn a car on invalid track netId: {packet.TrackNetId}");
            return;
        }

        if (!Components.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to spawn a car with invalid livery Id: {packet.LiveryId}");
            return;
        }

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            return;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;// + WorldMover.currentMove;
        var endpoint = (Vector3)kinked.Last().position;// + WorldMover.currentMove;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            return;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (player.AbsoluteWorldPosition - (Vector3)spawnPoint.position).magnitude;
        if (!ServerActionPolicy.InRange(playerDistanceToSpawn * playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            return;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        TrainCar spawnedCar = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward);
    }

    private void OnServerboundWorkTrainRequestPacket(ServerboundWorkTrainRequestPacket packet, ITransportPeer peer)
    {
        TrainCar trainCar;
        NetworkedTrainCar networkedTrainCar;

        var rpcResponse = new SpawnResponse() { Response = SpawnResponse.ResponseType.InUse };

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            SendRpcResponse(packet.TicketId, new SpawnResponse { Response = SpawnResponse.ResponseType.NotReady }, peer);
            return;
        }

        void Respond(SpawnResponse.ResponseType response)
        {
            rpcResponse.Response = response;
            SendRpcResponse(packet.TicketId, rpcResponse, peer);
        }

        if (player.LoadingState != PlayerLoadingState.Complete)
        {
            Respond(SpawnResponse.ResponseType.NotReady);
            return;
        }

        if (!AllowsAction(player, Multiplayer.Settings.AllowClientTrainManagement))
        {
            Respond(SpawnResponse.ResponseType.InsufficientPermissions);
            return;
        }

        LogDebug(() => $"OnServerboundWorkTrainRequestPacket() from : {player.Username}, trackNetId: {packet.TrackNetId}, liveryId: {packet.LiveryId}, index: {packet.Index}, withTrackDirection: {packet.WithTrackDirection}");

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to request a work train on invalid track netId: {packet.TrackNetId}");
            Respond(SpawnResponse.ResponseType.InvalidRequest);
            return;
        }

        if (!Components.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to request a work train with invalid livery Id: {packet.LiveryId}");
            Respond(SpawnResponse.ResponseType.InvalidRequest);
            return;
        }

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            Respond(SpawnResponse.ResponseType.InvalidRequest);
            return;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;
        var endpoint = (Vector3)kinked.Last().position;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            Respond(SpawnResponse.ResponseType.NoSpace);
            return;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (player.AbsoluteWorldPosition - (Vector3)spawnPoint.position).magnitude;
        if (!ServerActionPolicy.InRange(playerDistanceToSpawn * playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            Respond(SpawnResponse.ResponseType.OutOfRange);
            return;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        // Check if this is a garage loco
        bool isGarageCar = GarageCarSpawner.Spawners.TryGetValue(livery, out var selectedGarageSpawner);

        trainCar = isGarageCar ? selectedGarageSpawner.GetCar(livery) : CarSpawner.Instance.AllCars.FirstOrDefault(tc => tc.carLivery == livery);

        // Check if the car exists and is not in use - applies to both garage and non-garage cars
        if (trainCar != null)
        {
            if (NetworkedTrainCar.TryGetFromTrainCar(trainCar, out networkedTrainCar) && networkedTrainCar != null)
            {
                if (networkedTrainCar.InUse(out LocoInUseData.LocoInUseReason reason, out float timeout))
                {
                    LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but NetworkedTrainCar is in use, reason: {reason}, timeout: {timeout}");
                    rpcResponse.Reason = reason;
                    rpcResponse.Timeout = timeout;
                    SendRpcResponse(packet.TicketId, rpcResponse, peer);
                    return;
                }
            }
            else
            {
                LogWarning($"{player.Username} tried to request a work train of {livery.id} but NetworkedTrainCar not found");
                Respond(SpawnResponse.ResponseType.ServerError);
                return;
            }
        }

        float chargedPrice = 0;
        if (isGarageCar)
        {
            var price = Mathf.Min(selectedGarageSpawner.garageType.summonPrice, Globals.G.GameParams.WorkTrainSummonMaxPrice);
            try
            {
                chargedPrice = price; // RemoveMoney mutates before its event callbacks.
                if (!Inventory.Instance.RemoveMoney(price))
                {
                    chargedPrice = 0;
                    LogWarning($"{player.Username} tried to request a work train without enough money to do so!");
                    Respond(SpawnResponse.ResponseType.InsufficientFunds);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogError($"Work train debit failed for {player.Username}: {ex}");
                try { if (chargedPrice > 0) Inventory.Instance.AddMoney(chargedPrice); }
                catch (Exception refundError) { LogError($"Work train debit compensation failed for {player.Username}: {refundError}"); }
                Respond(SpawnResponse.ResponseType.ServerError);
                return;
            }
        }

        bool spawnedNew = trainCar == null;
        try
        {
            if (spawnedNew)
            {
                LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but no existing car found, spawning new car");
                trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);
            }
            else
            {
                // Checks passed, call the work train.
                trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);
                if (trainCar == null) throw new InvalidOperationException("Work train summon returned no car.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Work train request failed for {player.Username}: {ex}");
            if (chargedPrice > 0)
            {
                try { Inventory.Instance.AddMoney(chargedPrice); }
                catch (Exception refundError)
                {
                    LogError($"Work train refund failed for {player.Username}: {refundError}");
                }
            }
            Respond(SpawnResponse.ResponseType.ServerError);
            return;
        }

        if (spawnedNew)
        {
            // The car now exists and the purchase is committed. A transient notification
            // failure must not refund it and invite a duplicate retry.
            try { SendSpawnTrainset([trainCar], true, true); }
            catch (Exception ex) { LogError($"Work train spawned but its immediate notification failed: {ex}"); }
        }

        rpcResponse.Response = SpawnResponse.ResponseType.Success;
        SendRpcResponse(packet.TicketId, rpcResponse, peer);
    }

    private void OnServerboundShopQuoteRequestPacket(ServerboundShopQuoteRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            SendRpcResponse(packet.TicketId, new ShopQuoteResponse { RegisterNetId = packet.RegisterNetId, Status = ShopQuoteStatus.NotReady }, peer);
            return;
        }

        var quote = new ShopQuote(ShopQuoteStatus.NotReady);
        try
        {
            if (player.LoadingState == PlayerLoadingState.Complete)
            {
                quote = NetworkedCashRegisterWithModules.Get(packet.RegisterNetId, out var register) && register != null
                    ? register.Server_QuoteShopCart(player, packet.ItemIds, packet.Quantities)
                    : new ShopQuote(ShopQuoteStatus.InvalidShop);
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Shop quote failed for register {packet.RegisterNetId}: {ex.Message}");
            quote = new ShopQuote(ShopQuoteStatus.ServerError);
        }

        SendRpcResponse(packet.TicketId, new ShopQuoteResponse
        {
            RegisterNetId = packet.RegisterNetId,
            Status = quote.Status,
            Total = quote.Total,
            FailedLine = quote.FailedLine
        }, peer);
    }

    private void OnServerboundShopPurchaseRequestPacket(ServerboundShopPurchaseRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            SendRpcResponse(packet.TicketId, new ShopPurchaseResponse { RegisterNetId = packet.RegisterNetId, OperationId = packet.OperationId, Status = ShopQuoteStatus.NotReady }, peer);
            return;
        }
        ShopPurchaseBackend backend = null;
        // Replay the recorded outcome before checking mutable world/player state.
        var outcome = shopPurchases.Execute(player.Guid, packet.OperationId, packet.RegisterNetId,
                packet.ItemIds, packet.Quantities,
                () =>
                {
                    NetworkedCashRegisterWithModules.Get(packet.RegisterNetId, out var register);
                    return backend = new ShopPurchaseBackend(register, player, packet.ItemIds, packet.Quantities);
                });
        RpcTerminalResponse.Send(
            outcome.Quote.Status == ShopQuoteStatus.Success && backend != null
                ? backend.NotifyCommitted
                : null,
            ex => LogError($"Shop purchase {packet.OperationId} committed, but its notification failed: {ex}"),
            () =>
            {
                if (outcome.RecoveryRequired)
                    LogError($"Shop purchase {packet.OperationId} requires recovery after failed compensation.");
                SendRpcResponse(packet.TicketId, new ShopPurchaseResponse
                {
                    RegisterNetId = packet.RegisterNetId, OperationId = packet.OperationId,
                    Status = outcome.Quote.Status, Total = outcome.Quote.Total,
                    FailedLine = outcome.Quote.FailedLine, RecoveryRequired = outcome.RecoveryRequired
                }, peer);
            });
    }

    private void OnServerboundLicensePurchaseRequestPacket(ServerboundLicensePurchaseRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            SendRpcResponse(packet.TicketId, new LicensePurchaseResponse { Id = "<invalid>", IsJobLicense = packet.IsJobLicense, Status = LicensePurchaseStatus.NotReady }, peer);
            return;
        }

        string responseId = string.IsNullOrWhiteSpace(packet.Id) || packet.Id.Length > 256 ? "<invalid>" : packet.Id;
        void Respond(LicensePurchaseStatus status) => SendRpcResponse(packet.TicketId, new LicensePurchaseResponse
        {
            Id = responseId, IsJobLicense = packet.IsJobLicense, Status = status
        }, peer);

        if (player.LoadingState != PlayerLoadingState.Complete)
        {
            Respond(LicensePurchaseStatus.NotReady);
            return;
        }

        if (responseId == "<invalid>")
        {
            Respond(LicensePurchaseStatus.InvalidRequest);
            return;
        }

        JobLicenseType_v2 jobLicense = null;
        GeneralLicenseType_v2 generalLicense = null;
        float? price = packet.IsJobLicense
            ? (jobLicense = Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id))?.price
            : (generalLicense = Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id))?.price;

        if (!price.HasValue || float.IsNaN(price.Value) || float.IsInfinity(price.Value) || price.Value < 0f)
        {
            LogWarning($"{player.Username} tried to purchase an invalid {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id}!");
            Respond(LicensePurchaseStatus.InvalidRequest);
            return;
        }

        bool acquired = packet.IsJobLicense
            ? LicenseManager.Instance.IsJobLicenseAcquired(jobLicense)
            : LicenseManager.Instance.IsGeneralLicenseAcquired(generalLicense);
        if (acquired)
        {
            // A lost response may retry after the first request committed.
            Respond(LicensePurchaseStatus.Success);
            return;
        }

        if (!AllowsAction(player, Multiplayer.Settings.AllowClientPurchases))
        {
            Respond(LicensePurchaseStatus.PermissionDenied);
            return;
        }

        bool obtainable = packet.IsJobLicense
            ? LicenseManager.Instance.IsJobLicenseObtainable(jobLicense)
            : LicenseManager.Instance.IsGeneralLicenseObtainable(generalLicense);
        if (!obtainable)
        {
            Respond(LicensePurchaseStatus.PrerequisiteMissing);
            return;
        }

        var screen = Resources.FindObjectsOfTypeAll<CareerManagerLicensePayingScreen>()
            .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid() &&
                candidate.licensePrinter != null && candidate.licensePrinter.spawnAnchor != null)
            .OrderBy(candidate => (candidate.licensePrinter.spawnAnchor.position - player.WorldPosition).sqrMagnitude)
            .FirstOrDefault();
        if (screen == null || (screen.licensePrinter.spawnAnchor.position - player.WorldPosition).sqrMagnitude > 100f)
        {
            LogWarning($"{player.Username} tried to purchase a license outside a Career Manager.");
            Respond(LicensePurchaseStatus.OutOfRange);
            return;
        }

        CareerManagerDebtController.Instance.RefreshExistingDebtsState();
        if (CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts > 0)
        {
            LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} while having existing debts!");
            Respond(LicensePurchaseStatus.DebtOutstanding);
            return;
        }

        bool debited = false;
        try
        {
            debited = true; // RemoveMoney mutates before MoneyChanged callbacks.
            if (!Inventory.Instance.RemoveMoney(price.Value))
            {
                debited = false;
                LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} without enough money to do so!");
                Respond(LicensePurchaseStatus.InsufficientFunds);
                return;
            }

            if (packet.IsJobLicense)
                LicenseManager.Instance.AcquireJobLicense(jobLicense);
            else
                LicenseManager.Instance.AcquireGeneralLicense(generalLicense);
        }
        catch (Exception ex)
        {
            acquired = packet.IsJobLicense
                ? LicenseManager.Instance.IsJobLicenseAcquired(jobLicense)
                : LicenseManager.Instance.IsGeneralLicenseAcquired(generalLicense);
            if (!acquired && debited)
            {
                try { Inventory.Instance.AddMoney(price.Value); }
                catch (Exception refundError) { LogError($"License refund failed for {player.Username}: {refundError}"); }
            }
            if (!acquired)
            {
                LogError($"License purchase failed for {player.Username}: {ex}");
                Respond(LicensePurchaseStatus.ServerError);
                return;
            }
            LogError($"License was acquired, but a post-acquisition callback failed: {ex}");
        }

        // The server owns the physical document; normal item sync delivers it to nearby clients.
        try
        {
            if (packet.IsJobLicense)
                BookletCreator.CreateLicense(jobLicense, screen.licensePrinter.spawnAnchor.position,
                    screen.licensePrinter.spawnAnchor.rotation, WorldMover.OriginShiftParent);
            else
                BookletCreator.CreateLicense(generalLicense, screen.licensePrinter.spawnAnchor.position,
                    screen.licensePrinter.spawnAnchor.rotation, WorldMover.OriginShiftParent);
            screen.licensePrinter.Print(ignoreCooldown: true);
        }
        catch (Exception ex) { LogError($"License acquired, but its physical document failed to print: {ex}"); }
        Respond(LicensePurchaseStatus.Success);
    }
    private void OnServerboundJobValidateRequestPacket(ServerboundJobValidateRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedJob.Get(packet.JobNetId, out NetworkedJob networkedJob))
        {
            LogWarning($"Received job validation request from {player.DisplayName}, but job with netId {packet.JobNetId} was not found.");

            SendPacket(peer, new ClientboundJobValidateResponsePacket { JobNetId = packet.JobNetId, Invalid = true }, DeliveryMethod.ReliableOrdered);
            return;
        }

        // Find the station and validator
        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController) || networkedStationController.JobValidator == null)
        {
            LogWarning($"Received job validation request from {player.DisplayName} for job {networkedJob?.Job?.ID} at station with netId {packet.StationNetId}, StationController found: {networkedStationController != null}, JobValidator found: {networkedStationController?.JobValidator != null}");
            return;
        }

        if (!AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            !ServerActionPolicy.InRange((player.WorldPosition - networkedStationController.JobValidator.transform.position).sqrMagnitude, 10f))
        {
            RejectAction(peer, "Job validation", "not ready, permission denied or out of reach");
            return;
        }

        LogDebug(() => $"OnServerboundJobValidateRequestPacket() Validating {packet.JobNetId}, Validation Type: {packet.validationType} overview: {networkedJob.JobOverview != null}, booklet: {networkedJob.JobBooklet != null}");
        switch (packet.validationType)
        {
            case ValidationType.JobOverview:
                if (networkedJob.JobOverview == null)
                {
                    LogWarning($"Received job validation request from {player.DisplayName} for job {networkedJob?.Job?.ID} but JobOverview is null.");
                    return;
                }
                var jobOverview = networkedJob.JobOverview.GetTrackedItem<JobOverview>();
                if (jobOverview != null)
                {
                    networkedStationController.JobValidator.ProcessJobOverview(jobOverview);
                    return;
                }
                break;

            case ValidationType.JobBooklet:
                if (networkedJob.JobBooklet == null)
                {
                    LogWarning($"Received job validation request from {player.DisplayName} for job {networkedJob?.Job?.ID} but JobBooklet is null.");
                    return;
                }
                var jobBooklet = networkedJob.JobBooklet.GetTrackedItem<JobBooklet>();
                if (jobBooklet != null)
                {
                    networkedStationController.JobValidator.ValidateJob(jobBooklet);
                    return;
                }
                break;
        }

        //SendPacket(peer, new ClientboundJobValidateResponsePacket { JobNetId = packet.JobNetId, Invalid = false }, DeliveryMethod.ReliableUnordered);
    }

    private void OnServerboundWarehouseMachineControllerRequestPacket(ServerboundWarehouseMachineControllerRequestPacket packet, ITransportPeer peer)
    {
        LogDebug(() => $"ServerboundWarehouseMachineControllerRequestPacket(): {packet.NetId}");

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"ServerboundWarehouseMachineControllerRequestPacket() ServerPlayer not found: {peer.Id}");
            return;
        }

        if (!AllowsAction(player, Multiplayer.Settings.AllowClientService) ||
            !Enum.IsDefined(typeof(WarehouseAction), packet.WarehouseAction))
        {
            RejectAction(peer, "Warehouse", "not ready, permission denied or invalid action");
            return;
        }

        //Find the warehouse
        if (!NetworkedWarehouseMachineController.Get(packet.NetId, out var targetWarehouse))
        {
            LogWarning($"ServerboundWarehouseMachineControllerRequestPacket() WarehouseMachineController not found. NetId: {packet.NetId}");
            return;
        }

        if (targetWarehouse == null || targetWarehouse.WarehouseMachineController == null ||
            targetWarehouse.WarehouseMachine == null ||
            !ServerActionPolicy.InRange((player.WorldPosition - targetWarehouse.WarehouseMachineController.transform.position).sqrMagnitude, 10f))
        {
            RejectAction(peer, "Warehouse", "unavailable or out of reach");
            return;
        }

        try { targetWarehouse.ServerProcessWarehouseAction(packet.WarehouseAction); }
        catch (Exception ex)
        {
            LogError($"Warehouse action failed: {ex}");
            RejectAction(peer, "Warehouse", "server error");
        }
    }

    private void OnCommonChatPacket(CommonChatPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            ChatManager.ProcessMessage(packet.message, player);
    }
    #endregion

    #region Unconnected Packet Handling
    private void OnUnconnectedPingPacket(UnconnectedPingPacket packet, IPEndPoint endPoint)
    {
        //Log($"OnUnconnectedPingPacket({endPoint.Address})");
        //SendUnconnectedPacket(packet, endPoint.Address.ToString(), endPoint.Port);
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
        }
        else
        {
            if (NetworkedPitStopStation.Get(packet.NetId, out NetworkedPitStopStation controller))
                controller.ProcessInteractionPacketAsHost(packet, player);
            else
                LogWarning($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
            SendNetSerializablePacket(peer, new CommonPitStopPlugInteractionPacket
            {
                NetId = packet.NetId,
                InteractionType = (byte)PitStopStationInteractionType.Reject
            }, DeliveryMethod.ReliableOrdered);
        }

        if (NetworkedPluggableObject.Get(packet.NetId, out NetworkedPluggableObject plug) && foundPlayer)
        {
            plug.ProcessInteractionPacketAsHost(packet, player);
        }
        else
        {
            LogError($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    private void OnCommonItemChangePacket(CommonItemChangePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player) || player.LoadingState != PlayerLoadingState.Complete)
            return;
        if (packet?.Items == null || packet.Items.Count > NetworkedItemManager.MaxClientBatchItems ||
            !NetworkedItemManager.Instance.ReceiveSnapshots(packet.Items, player))
        {
            LogWarning($"Rejected oversized item update batch from {player.Username}");
            KickPlayer(player);
            return;
        }

        //LogDebug(()=>$"OnCommonItemChangePacket({packet?.Items?.Count}, {peer.Id} (\"{player.Username}\"))");

        //LogDebug(() =>
        //{
        //    string debug = "";

        //    foreach (var item in packet?.Items)
        //    {
        //        debug += "UpdateType: " + item?.UpdateType + "\r\n";
        //        debug += "itemNetId: " + item?.ItemNetId + "\r\n";
        //        debug += "PrefabName: " + item?.PrefabName + "\r\n";
        //        debug += "Equipped: " + item?.ItemState + "\r\n";
        //        debug += "Position: " + item?.ItemPosition + "\r\n";
        //        debug += "Rotation: " + item?.ItemRotation + "\r\n";
        //        debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
        //        debug += "Player: " + item?.Player + "\r\n";
        //        debug += "CarNetId: " + item?.CarNetId + "\r\n";
        //        debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

        //        debug += "States:";

        //        if (item.States != null)
        //            foreach (var state in item?.States)
        //                debug += "\r\n\t" + state.Key + ": " + state.Value;
        //    }

        //    return debug;
        //}

        //);

    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Cash Register With Modules Action received, but player was not found");
            return;
        }

        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");
        netCashRegister.Server_ProcessCashRegisterAction(player, packet);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Received Generic Switch State, but player was not found");
            return;
        }

        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State from \"{player.Username}\" for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Server_ReceiveSwitchState(packet.IsOn, player);
    }

    #endregion
}
