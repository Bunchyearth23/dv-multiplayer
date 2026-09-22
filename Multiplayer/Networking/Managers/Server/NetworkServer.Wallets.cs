using Multiplayer.Networking.Data;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
using LiteNetLib;

namespace Multiplayer.Networking.Managers.Server;
public partial class NetworkServer
{
    internal void SendIndividualMoney(ServerPlayer player, double amount)
    {
        if (player == null || player.Peer == SelfPeer || player.LoadingState < PlayerLoadingState.ReadyForWorldState ||
            !TryGetServerPlayer(player.Peer, out var current) || !ReferenceEquals(current, player)) return;
        SendPacket(player.Peer, new ClientboundMoneyPacket { PlayerGuid = player.Guid.ToString("N"), Amount = amount }, DeliveryMethod.ReliableOrdered);
    }
}
