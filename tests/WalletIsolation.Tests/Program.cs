using System;
using System.Linq;
using DV.InventorySystem;
using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Wallets;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.Managers.Client;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
internal static class Program
{
    static int checks;
    static void Check(bool value,string message) { if(!value) throw new Exception(message); checks++; }
    static void Main()
    {
        var server=new NetworkServer(); NetworkLifecycle.Instance.Server=server;
        var host=new ServerPlayer { Peer=server.SelfPeer }; var a=new ServerPlayer(); var b=new ServerPlayer();
        server.ServerPlayers.AddRange(new[]{host,a,b}); var api=new ServerAPIProvider(server);
        api.EnsureIndividualBalance(server.GetWrapper(a),Guid.NewGuid(),1000);
        api.EnsureIndividualBalance(server.GetWrapper(b),Guid.NewGuid(),2000);
        Check(server.Sent.Count==2 && server.Sent[0].Peer==a.Peer && server.Sent[1].Peer==b.Peer,"Initialization broadcast to wrong owner");
        Check(server.Sent.All(s=>s.Delivery==DeliveryMethod.ReliableOrdered),"Money updates unordered"); server.Sent.Clear();
        PlayerWallet.Credit(host,100000);
        Check(server.Sent.Count==0 && PlayerWallet.Read(a)==1000 && PlayerWallet.Read(b)==2000,"Host grant leaked");
        var hostBalance=Inventory.Instance.PlayerMoney;
        Check(PlayerWallet.TryDebit(a,100),"Guest purchase refused");
        Check(Inventory.Instance.PlayerMoney==hostBalance && PlayerWallet.Read(a)==900 && PlayerWallet.Read(b)==2000,"Guest purchase charged host/other guest");
        Check(server.Sent.Count==1 && server.Sent[0].Peer==a.Peer,"Guest purchase broadcast");
        Check(!PlayerWallet.TryDebit(a,10000) && Inventory.Instance.PlayerMoney==hostBalance,"Insufficient guest funds used host money");
        PlayerWallet.Credit(a,100); Check(PlayerWallet.Read(a)==1000 && PlayerWallet.Read(b)==2000,"Refund misrouted");
        api.TransferIndividualBalance(server.GetWrapper(a),server.GetWrapper(b),Guid.NewGuid(),50);
        Check(PlayerWallet.Read(a)==950 && PlayerWallet.Read(b)==2050,"Transfer lost wallet separation");
        var saved=api.SaveIndividualWallets(); var restored=new ServerAPIProvider(server); restored.Load(saved);
        Check(PlayerWallet.Read(a)==950 && PlayerWallet.Read(b)==2050,"Save/reload merged wallets");
        var duplicate = new ServerPlayer { Guid=a.Guid }; server.ServerPlayers.Add(duplicate);
        Check(!PlayerWallet.TryDebit(duplicate,1) && !PlayerWallet.TryDebit(a,1), "Ambiguous identity spent shared wallet");
        server.ServerPlayers.Remove(duplicate);
        var old=a; server.ServerPlayers.Remove(old); a=new ServerPlayer{ Guid=old.Guid }; server.ServerPlayers.Add(a);
        Check(PlayerWallet.Read(a)==950,"Reconnect lost persistent balance");
        Check(!PlayerWallet.TryDebit(old,1),"Disconnected peer spent money");
        server.Sent.Clear(); PlayerWallet.Credit(old,25);
        Check(PlayerWallet.Read(a)==975 && server.Sent.Single().Peer==a.Peer,"Delayed refund lost owner after reconnect");
        var zero=new ServerPlayer();server.ServerPlayers.Add(zero);
        restored.EnsureIndividualBalance(server.GetWrapper(zero),Guid.NewGuid(),0);
        restored.EnsureIndividualBalance(server.GetWrapper(zero),Guid.NewGuid(),500);
        Check(PlayerWallet.Read(zero)==0,"Zero balance reset");
        var register=new NetworkedCashRegisterWithModules();register.Deposit(a,25);
        Check(register.CanUse(a) && !register.CanUse(b) && !register.CanUse(host),"Other player used deposit");
        register.FailClear(true);try{register.ReturnIndividualDeposit();}catch(Exception){}
        register.FailClear(false);register.ReturnIndividualDeposit();
        Check(PlayerWallet.Read(a)==1000,"Retry refunded deposit twice");
        var beforeNewSession=restored.SaveIndividualWallets();
        var nextSession=new ServerAPIProvider(server); nextSession.Load(beforeNewSession);
        bool obsoleteRefused=false;try{PlayerWallet.Credit(old,10);}catch(InvalidOperationException){obsoleteRefused=true;}
        Check(obsoleteRefused && PlayerWallet.Read(a)==1000,"Old session refund entered new wallet authority");
        server.Sent.Clear();a.LoadingState=PlayerLoadingState.ReadyForGameData;
        nextSession.CreditIndividualBalance(server.GetWrapper(a),Guid.NewGuid(),1);
        Check(server.Sent.Count==0,"Money packet applied before native Inventory exists");a.LoadingState=PlayerLoadingState.Complete;
        Multiplayer.Multiplayer.Settings.Guid=a.Guid; var client=new NetworkClient();
        var packet=new ClientboundMoneyPacket{PlayerGuid=a.Guid.ToString("N"),Amount=123456789.25};
        var processor=new NetPacketProcessor();ClientboundMoneyPacket roundtrip=null;
        processor.SubscribeReusable<ClientboundMoneyPacket>(p=>roundtrip=new ClientboundMoneyPacket{PlayerGuid=p.PlayerGuid,Amount=p.Amount});
        var writer=new NetDataWriter();processor.Write(writer,packet);processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(roundtrip.Amount==packet.Amount && roundtrip.PlayerGuid==packet.PlayerGuid,"Wire rounded wallet/removed identity");
        client.Receive(roundtrip);Check(Inventory.Instance.PlayerMoney==packet.Amount,"Own wallet not applied");
        client.Receive(new ClientboundMoneyPacket{PlayerGuid=b.Guid.ToString("N"),Amount=2});Check(Inventory.Instance.PlayerMoney==packet.Amount,"Other owner applied");
        client.Receive(new ClientboundMoneyPacket{PlayerGuid=a.Guid.ToString("N"),Amount=double.NaN});Check(Inventory.Instance.PlayerMoney==packet.Amount,"NaN applied");
        client.Receive(new ClientboundMoneyPacket{PlayerGuid=a.Guid.ToString("N"),Amount=7},true);Check(Inventory.Instance.PlayerMoney==packet.Amount,"Host inventory overwritten from remote ledger");
        Console.WriteLine($"{checks} wallet isolation checks passed (production routing/API/ledger/client handler and real packet serialization).");
    }
}
