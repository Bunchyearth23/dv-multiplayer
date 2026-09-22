using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using MPAPI.Interfaces;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
namespace MPAPI.Interfaces { public interface IPlayer { } }
namespace Multiplayer
{
    public static class Multiplayer { public static Settings Settings = new(); }
    public class Settings { public Guid Guid; public Guid GetGuid() => Guid; }
}
namespace DV.InventorySystem
{
    public class Inventory
    {
        public static Inventory Instance = new(); public double PlayerMoney = 9000;
        public void SetMoney(double amount) => PlayerMoney = amount;
        public void AddMoney(double amount) => PlayerMoney += amount;
        public bool RemoveMoney(double amount) { if (PlayerMoney < amount) return false; PlayerMoney -= amount; return true; }
    }
}
namespace Multiplayer.Networking.Data
{
    public class ServerPlayer { public Guid Guid = Guid.NewGuid(); public object Peer = new(); public PlayerLoadingState LoadingState = PlayerLoadingState.Complete; }
}
namespace Multiplayer.API
{
    public class ServerPlayerWrapper : IPlayer
    { internal ServerPlayer _serverPlayer; public object Peer => _serverPlayer.Peer; public ServerPlayerWrapper(ServerPlayer p) { _serverPlayer = p; } }
    public partial class ServerAPIProvider
    {
        private readonly NetworkServer server;
        public ServerAPIProvider(NetworkServer value) { server = value; Current = this; }
        public void Load(Newtonsoft.Json.Linq.JObject data) { if (!global::Multiplayer.Networking.Data.Wallets.IndividualWalletStoreCodec.TryRead(data, individualWallets)) throw new Exception("Load failed"); }
    }
}
namespace Multiplayer.Networking.Managers.Server
{
    public partial class NetworkServer
    {
        public object SelfPeer = new(); public List<ServerPlayer> ServerPlayers = new();
        public List<(object Peer, ClientboundMoneyPacket Packet, DeliveryMethod Delivery)> Sent = new();
        private Dictionary<ServerPlayer,global::Multiplayer.API.ServerPlayerWrapper> wrappers = new();
        public global::Multiplayer.API.ServerPlayerWrapper GetWrapper(ServerPlayer p) { if (!wrappers.TryGetValue(p,out var wrapper)) wrappers[p] = wrapper = new(p); return wrapper; }
        public bool TryGetServerPlayer(object peer, out ServerPlayer player) { player = ServerPlayers.FirstOrDefault(p=>ReferenceEquals(p.Peer,peer)); return player != null; }
        private void SendPacket(object peer, ClientboundMoneyPacket packet, DeliveryMethod delivery) => Sent.Add((peer,packet,delivery));
        public void LogError(string message) { }
    }
}
namespace Multiplayer.Components.Networking
{
    public class NetworkLifecycle
    { public static NetworkLifecycle Instance = new(); public NetworkServer Server; public bool IsHost(ServerPlayer player) => ReferenceEquals(player.Peer,Server.SelfPeer); public bool IsHost() => true; }
}
namespace Multiplayer.Networking.Managers.Client
{
    public partial class NetworkClient
    { private bool isAlsoHost; private void LogDebug(Func<string> value) { } public void Receive(ClientboundMoneyPacket packet,bool host=false) { isAlsoHost=host; OnClientboundMoneyPacket(packet); } }
}
namespace Multiplayer.Components.Networking.World
{
    public class CashRegister { public double DepositedCash; public bool ThrowClear; public void SetCash(double amount) { if(ThrowClear) throw new Exception("native callback"); DepositedCash=amount; } }
    public partial class NetworkedCashRegisterWithModules
    {
        private CashRegister CashRegister = new();
        public void Deposit(ServerPlayer player,double amount) { depositOwner=player; depositRefundId=Guid.NewGuid(); CashRegister.DepositedCash=amount; }
        public bool CanUse(ServerPlayer player) => CanUseDeposit(player);
        public void FailClear(bool value) => CashRegister.ThrowClear=value;
    }
}
