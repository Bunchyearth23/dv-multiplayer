using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Wallets;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV;
using DV.JObjectExtstensions;
using Newtonsoft.Json.Linq;
using MPAPI.Util;

namespace Multiplayer.API;

public class ServerAPIProvider : IServer, IPersistentPlayerWallets
{
    private readonly NetworkServer server;
    private readonly IndividualWalletLedger individualWallets = new();
    internal static ServerAPIProvider Current { get; private set; }

    public event Action<IPlayer> OnPlayerConnected;
    public event Action<IPlayer> OnPlayerDisconnected;
    public event Action<IPlayer> OnPlayerReady;
    public event Action<IndividualWalletChange> OnIndividualWalletChanged;

    public IndividualWalletResult ReadIndividualBalance(IPlayer player, Guid requestId) => ExecuteWallet(player, null, requestId, WalletOperationKind.Read, 0);
    public IndividualWalletResult CreditIndividualBalance(IPlayer player, Guid requestId, double amount) => ExecuteWallet(player, null, requestId, WalletOperationKind.Credit, amount);
    public IndividualWalletResult DebitIndividualBalance(IPlayer player, Guid requestId, double amount) => ExecuteWallet(player, null, requestId, WalletOperationKind.Debit, amount);
    public IndividualWalletResult TransferIndividualBalance(IPlayer source, IPlayer destination, Guid requestId, double amount) => ExecuteWallet(source, destination, requestId, WalletOperationKind.Transfer, amount);

    private IndividualWalletResult ExecuteWallet(IPlayer player, IPlayer counterparty, Guid requestId, WalletOperationKind kind, double amount)
    {
        if (!TryResolveAuthenticatedIdentity(player, out var id) || kind == WalletOperationKind.Transfer && !TryResolveAuthenticatedIdentity(counterparty, out _))
            return new(requestId, IndividualWalletStatus.InvalidPlayer, 0);
        Guid other = Guid.Empty;
        if (counterparty != null) TryResolveAuthenticatedIdentity(counterparty, out other);
        return individualWallets.Execute(id, other, requestId, kind, amount, change => EventDispatch.Isolated(OnIndividualWalletChanged, change, exception => server.LogError($"Individual wallet callback failed: {exception}")));
    }

    private bool TryResolveAuthenticatedIdentity(IPlayer player, out Guid identity)
    {
        identity = Guid.Empty;
        if (player is not ServerPlayerWrapper wrapper || wrapper.Peer == null || !server.TryGetServerPlayer(wrapper.Peer, out var authenticated) || !ReferenceEquals(authenticated, wrapper._serverPlayer) || authenticated.Guid == Guid.Empty) return false;
        identity = authenticated.Guid;
        return true;
    }

    internal JObject SaveIndividualWallets() => IndividualWalletStoreCodec.Write(individualWallets);

    #region Server Properties

    public int PlayerCount => server.PlayerCount;

    public IReadOnlyCollection<IPlayer> Players => server.ServerPlayerWrappers;

    public IPlayer GetPlayer(byte PlayerId)
    {
        server.PlayerWrapperCache.TryGetValue(PlayerId, out var player);

        return player;
    }
    #endregion

    #region Packet API
    public void RegisterPacket<T>(ServerPacketHandler<T> handler) where T : class, IPacket, new()
    {
        server.RegisterExternalPacket<T>(handler);
    }
    public void RegisterSerializablePacket<T>(ServerPacketHandler<T> handler) where T : class, ISerializablePacket, new()
    {
        server.RegisterExternalSerializablePacket<T>(handler);
    }


    public void SendPacketToAll<T>(T packet, bool reliable = true, bool excludeSelf = false, IPlayer excludePlayer = null) where T : class, IPacket, new()
    {
        ITransportPeer peer = null;

        if (excludePlayer != null)
            peer = GetPeerFromPlayer(excludePlayer, $"SendPacketToAll<{typeof(T).Name}>");

        server.SendExternalPacketToAll(packet, reliable, peer, excludeSelf);
    }

    public void SendSerializablePacketToAll<T>(T packet, bool reliable = true, bool excludeSelf = false, IPlayer excludePlayer = null) where T : class, ISerializablePacket, new()
    {
        ITransportPeer peer = null;

        if(excludePlayer != null)
            peer = GetPeerFromPlayer(excludePlayer, $"SendSerializablePacketToAll<{typeof(T).Name}>");

        server.SendExternalSerializablePacketToAll(packet, reliable, peer, excludeSelf);
    }

    public void SendPacketToPlayer<T>(T packet, IPlayer player, bool reliable = true) where T : class, IPacket, new()
    {
        var peer = GetPeerFromPlayer(player, $"SendPacketToPlayer<{typeof(T).Name}>");

        if (peer != null)
            server.SendExternalPacketToPlayer(packet, peer, reliable);
    }

    public void SendSerializablePacketToPlayer<T>(T packet, IPlayer player, bool reliable = true) where T : class, ISerializablePacket, new()
    {
        var peer = GetPeerFromPlayer(player, $"SendSerializablePacketToPlayer<{typeof(T).Name}>");

        if (peer != null)
            server.SendExternalSerializablePacketToPlayer(packet, peer, reliable);
    }
    #endregion

    #region Server Util
    public float AnyPlayerSqrMag(GameObject item) => DvExtensions.AnyPlayerSqrMag(item);

    public float AnyPlayerSqrMag(Vector3 anchor) => DvExtensions.AnyPlayerSqrMag(anchor);
    #endregion

    #region Player Management
    public void KickPlayer(IPlayer player)
    {
        server.KickPlayer(GetServerPlayerFromIPlayer(player));
    }

    public void SetPlayerCrewName(IPlayer player, string crewName)
    {
        var serverPlayer = GetServerPlayerFromIPlayer(player);

        if (serverPlayer != null)
            serverPlayer.CrewName = crewName;
    }
    #endregion

    #region Chat
    public void SendServerChatMessage(string message, IPlayer excludePlayer = null)
    {
        var excludedServerPlayer = GetServerPlayerFromIPlayer(excludePlayer);
        if (excludedServerPlayer != null)
            server.ChatManager.ServerMessage(message, null, excludedServerPlayer);
    }

    public void SendWhisperChatMessage(string message, IPlayer player)
    {
        var serverPlayer = GetServerPlayerFromIPlayer(player);
        if (serverPlayer != null)
            server.SendWhisper(message, serverPlayer);
    }

    public bool RegisterChatCommand(string commandLong, string commandShort, Func<string> helpMessage, ChatCommandCallback callback)
    {
        ChatCommandCallbackInternal internalCallback = (message, serverPlayer) =>
        {
            var playerWrapper = server.GetWrapper(serverPlayer);
            callback(message, playerWrapper);
        };

        return server.ChatManager.RegisterChatCommand(commandLong, commandShort, helpMessage, internalCallback);
    }

    public void RegisterChatFilter(ChatFilterDelegate callback)
    {
        ChatFilterDelegateInternal internalCallback = (ref string message, ServerPlayer serverPlayer) =>
        {
            var playerWrapper = server.GetWrapper(serverPlayer);
            return callback(ref message, playerWrapper);
        };

        server.ChatManager.RegisterChatFilter(internalCallback);
    }
    #endregion

    #region Class Helpers
    internal ServerAPIProvider(NetworkServer serverInstance)
    {
        this.server = serverInstance;
        Current = this;
        var root = SaveGameManager.Instance?.data?.GetJObject("Multiplayer");
        if (!IndividualWalletStoreCodec.TryRead(root?[IndividualWalletStoreCodec.Key] as JObject, individualWallets)) server.LogWarning("Invalid individual wallet save data; using empty wallet state.");

        server.PlayerConnected += OnPlayerConnectedInternal;
        server.PlayerDisconnected += OnPlayerDisconnectedInternal;
        server.PlayerReady += OnPlayerReadyInternal;
    }

    private ITransportPeer GetPeerFromPlayer(IPlayer player, string operationName)
    {
        if (player == null)
        {
            server.LogDebug(() => $"{operationName}: Player is null");
            return null;
        }

        if (player is ServerPlayerWrapper playerWrapper)
        {
            return playerWrapper.Peer;
        }

        server.LogWarning($"{operationName}: Player '{player.Username}' is not a ServerPlayerWrapper (got {player.GetType().Name})");
        return null;
    }

    private ServerPlayer GetServerPlayerFromIPlayer(IPlayer player)
    {
        if (player == null)
            return null;

        if (player is ServerPlayerWrapper wrapper)
            return wrapper._serverPlayer;

        server.LogWarning($"GetServerPlayerFromIPlayer: Player '{player.Username}' is not a ServerPlayerWrapper (got {player.GetType().Name})");
        return null;
    }

    internal void Dispose()
    {
        server.PlayerConnected -= OnPlayerConnectedInternal;
        server.PlayerDisconnected -= OnPlayerDisconnectedInternal;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void OnPlayerConnectedInternal(ServerPlayer serverPlayer)
    {
        OnPlayerConnected?.Invoke(server.GetWrapper(serverPlayer));
    }

    private void OnPlayerDisconnectedInternal(ServerPlayer serverPlayer)
    {
        // Get wrapper before removing from cache
        var wrapper = server.GetWrapper(serverPlayer);
        OnPlayerDisconnected?.Invoke(wrapper);
        server.PlayerWrapperCache.Remove(serverPlayer.PlayerId);
    }

    private void OnPlayerReadyInternal(ServerPlayer serverPlayer)
    {
        OnPlayerReady?.Invoke(server.GetWrapper(serverPlayer));
    }
    #endregion
}
