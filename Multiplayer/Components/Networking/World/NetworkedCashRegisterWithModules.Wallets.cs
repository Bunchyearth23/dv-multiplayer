using System;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Wallets;

namespace Multiplayer.Components.Networking.World;
public partial class NetworkedCashRegisterWithModules
{
    private ServerPlayer depositOwner;
    private Guid depositRefundId;
    private bool CanUseDeposit(ServerPlayer player)
    {
        if (CashRegister.DepositedCash <= 0) { depositOwner = null; return true; }
        return depositOwner == null ? NetworkLifecycle.Instance.IsHost(player) : depositOwner.Guid == player.Guid;
    }
    internal bool ReturnIndividualDeposit()
    {
        if (!NetworkLifecycle.Instance.IsHost() || depositOwner == null || NetworkLifecycle.Instance.IsHost(depositOwner)) return false;
        var amount = CashRegister.DepositedCash;
        if (amount > 0) PlayerWallet.Credit(depositOwner, amount, depositRefundId);
        // A retried native callback uses the same refund identity until clearing succeeds.
        CashRegister.SetCash(0);
        depositOwner = null;
        return true;
    }
}
