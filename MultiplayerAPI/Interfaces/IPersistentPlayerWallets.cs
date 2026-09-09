using System;
using MPAPI.Types;

namespace MPAPI.Interfaces;

/// <summary>Optional host-only capability for durable, per-player mod wallets.</summary>
public interface IPersistentPlayerWallets
{
    /// <summary>Raised after a successful balance mutation. Subscribers are isolated.</summary>
    event Action<IndividualWalletChange> OnIndividualWalletChanged;

    IndividualWalletResult ReadIndividualBalance(IPlayer player, Guid requestId);
    IndividualWalletResult CreditIndividualBalance(IPlayer player, Guid requestId, double amount);
    IndividualWalletResult DebitIndividualBalance(IPlayer player, Guid requestId, double amount);
    IndividualWalletResult TransferIndividualBalance(IPlayer source, IPlayer destination, Guid requestId, double amount);
}
