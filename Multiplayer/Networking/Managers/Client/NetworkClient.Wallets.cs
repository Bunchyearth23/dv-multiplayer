using System;
using DV.InventorySystem;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
namespace Multiplayer.Networking.Managers.Client;
public partial class NetworkClient
{
    private void OnClientboundMoneyPacket(ClientboundMoneyPacket packet)
    {
        LogDebug(() => $"Received new money amount ${packet.Amount}");
        if (isAlsoHost || !Guid.TryParseExact(packet.PlayerGuid, "N", out var owner) || owner != Multiplayer.Settings.GetGuid() ||
            double.IsNaN(packet.Amount) || double.IsInfinity(packet.Amount) || packet.Amount < 0) return;
        Inventory.Instance.SetMoney(packet.Amount);
    }


}
