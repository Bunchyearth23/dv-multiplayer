using System;
using DV.InventorySystem;
using MPAPI.Types;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;

namespace Multiplayer.Networking.Data.Wallets;

// Unity owner only. Remote transactions must never fall back to the host's Inventory.
public static class PlayerWallet
{
    private static bool IsHost(ServerPlayer player) => player != null && NetworkLifecycle.Instance.IsHost(player);
    public static double Read(ServerPlayer player)
    {
        if (player == null) throw new ArgumentNullException(nameof(player));
        return IsHost(player) ? Inventory.Instance.PlayerMoney : RequireApi().ReadPlayerWallet(player);
    }
    public static bool TryDebit(ServerPlayer player, double amount)
    {
        if ((double.IsNaN(amount) || double.IsInfinity(amount)) || amount < 0) return false;
        if (amount == 0) return true;
        return IsHost(player) ? Inventory.Instance.RemoveMoney(amount) : RequireApi().DebitPlayerWallet(player, amount);
    }
    public static void Credit(ServerPlayer player, double amount, Guid? operationId = null)
    {
        if ((double.IsNaN(amount) || double.IsInfinity(amount)) || amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0) return;
        if (IsHost(player)) Inventory.Instance.AddMoney(amount);
        else RequireApi().RefundPlayerWallet(player, amount, operationId);
    }
    private static ServerAPIProvider RequireApi() => ServerAPIProvider.Current ?? throw new InvalidOperationException("Individual wallet authority unavailable.");
}
