using System;

namespace MPAPI.Types;

public enum IndividualWalletStatus : byte
{
    Success, InvalidPlayer, InvalidRequest, InvalidAmount, InsufficientFunds,
    BalanceLimitExceeded, OperationConflict, StorageLimitReached
}

public readonly struct IndividualWalletResult
{
    public Guid RequestId { get; }
    public IndividualWalletStatus Status { get; }
    public double Balance { get; }
    public double CounterpartyBalance { get; }
    public bool IsReplay { get; }
    public bool Succeeded => Status == IndividualWalletStatus.Success;

    public IndividualWalletResult(Guid requestId, IndividualWalletStatus status, double balance,
        double counterpartyBalance = 0, bool isReplay = false)
    { RequestId = requestId; Status = status; Balance = balance; CounterpartyBalance = counterpartyBalance; IsReplay = isReplay; }
}

public readonly struct IndividualWalletChange
{
    public Guid RequestId { get; }
    public Guid PlayerId { get; }
    public Guid CounterpartyId { get; }
    public double PreviousBalance { get; }
    public double Balance { get; }

    public IndividualWalletChange(Guid requestId, Guid playerId, Guid counterpartyId, double previousBalance, double balance)
    { RequestId = requestId; PlayerId = playerId; CounterpartyId = counterpartyId; PreviousBalance = previousBalance; Balance = balance; }
}
