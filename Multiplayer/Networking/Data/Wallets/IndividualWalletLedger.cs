using MPAPI.Types;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Multiplayer.Networking.Data.Wallets;

public enum WalletOperationKind : byte { Read, Credit, Debit, Transfer }

public sealed class WalletOperationRecord
{
    public Guid RequestId; public string Fingerprint; public IndividualWalletStatus Status;
    public double Balance; public double CounterpartyBalance;
}

public sealed class IndividualWalletLedger
{
    public const double MaxAmount = 1_000_000_000d;
    public const double MaxBalance = 1_000_000_000_000d;
    public const int MaxOperations = 16384;
    private readonly object gate = new();
    private readonly Dictionary<Guid, double> balances = new();
    private readonly Dictionary<Guid, WalletOperationRecord> operations = new();

    public IndividualWalletResult Execute(Guid player, Guid counterparty, Guid requestId, WalletOperationKind kind, double amount,
        Action<IndividualWalletChange> notify = null)
    {
        if (player == Guid.Empty) return Result(requestId, IndividualWalletStatus.InvalidPlayer);
        if (requestId == Guid.Empty) return Result(requestId, IndividualWalletStatus.InvalidRequest);
        if (kind != WalletOperationKind.Read && (!Finite(amount) || amount <= 0 || amount > MaxAmount))
            return Result(requestId, IndividualWalletStatus.InvalidAmount);
        if (kind == WalletOperationKind.Transfer && (counterparty == Guid.Empty || counterparty == player))
            return Result(requestId, IndividualWalletStatus.InvalidPlayer);

        string fingerprint = ((byte)kind).ToString(CultureInfo.InvariantCulture) + ":" + player.ToString("N") + ":" +
            counterparty.ToString("N") + ":" + amount.ToString("R", CultureInfo.InvariantCulture);
        IndividualWalletChange[] changes = null;
        IndividualWalletResult result;
        lock (gate)
        {
            if (operations.TryGetValue(requestId, out var prior))
                return prior.Fingerprint == fingerprint
                    ? new IndividualWalletResult(requestId, prior.Status, prior.Balance, prior.CounterpartyBalance, true)
                    : Result(requestId, IndividualWalletStatus.OperationConflict);
            if (operations.Count >= MaxOperations) return Result(requestId, IndividualWalletStatus.StorageLimitReached);

            double balance = Get(player), other = Get(counterparty);
            var status = IndividualWalletStatus.Success;
            if ((kind == WalletOperationKind.Debit || kind == WalletOperationKind.Transfer) && balance < amount)
                status = IndividualWalletStatus.InsufficientFunds;
            else if (kind == WalletOperationKind.Credit && balance > MaxBalance - amount)
                status = IndividualWalletStatus.BalanceLimitExceeded;
            else if (kind == WalletOperationKind.Transfer && other > MaxBalance - amount)
                status = IndividualWalletStatus.BalanceLimitExceeded;
            else if (kind == WalletOperationKind.Credit) { balances[player] = balance + amount; changes = [new(requestId, player, Guid.Empty, balance, balance + amount)]; }
            else if (kind == WalletOperationKind.Debit) { balances[player] = balance - amount; changes = [new(requestId, player, Guid.Empty, balance, balance - amount)]; }
            else if (kind == WalletOperationKind.Transfer)
            {
                balances[player] = balance - amount; balances[counterparty] = other + amount;
                changes = [new(requestId, player, counterparty, balance, balance - amount), new(requestId, counterparty, player, other, other + amount)];
            }
            double final = Get(player), finalOther = Get(counterparty);
            operations.Add(requestId, new WalletOperationRecord { RequestId = requestId, Fingerprint = fingerprint, Status = status, Balance = final, CounterpartyBalance = finalOther });
            result = new IndividualWalletResult(requestId, status, final, finalOther);
        }
        if (changes != null && notify != null)
            foreach (var change in changes) try { notify(change); } catch { }
        return result;
    }

    public IReadOnlyDictionary<Guid, double> SnapshotBalances() { lock (gate) return new Dictionary<Guid, double>(balances); }
    public IReadOnlyCollection<WalletOperationRecord> SnapshotOperations() { lock (gate) return new List<WalletOperationRecord>(operations.Values); }

    public bool TryReplace(IEnumerable<KeyValuePair<Guid, double>> savedBalances, IEnumerable<WalletOperationRecord> savedOperations)
    {
        var nextBalances = new Dictionary<Guid, double>(); var nextOperations = new Dictionary<Guid, WalletOperationRecord>();
        if (savedBalances == null || savedOperations == null) return false;
        foreach (var pair in savedBalances)
        {
            if (pair.Key == Guid.Empty || !Finite(pair.Value) || pair.Value < 0 || pair.Value > MaxBalance || nextBalances.ContainsKey(pair.Key)) return false;
            nextBalances.Add(pair.Key, pair.Value);
        }
        foreach (var op in savedOperations)
        {
            if (op == null || op.RequestId == Guid.Empty || string.IsNullOrEmpty(op.Fingerprint) || op.Fingerprint.Length > 256 ||
                !Finite(op.Balance) || !Finite(op.CounterpartyBalance) || op.Balance < 0 || op.CounterpartyBalance < 0 ||
                op.Balance > MaxBalance || op.CounterpartyBalance > MaxBalance || nextOperations.ContainsKey(op.RequestId)) return false;
            nextOperations.Add(op.RequestId, op);
        }
        if (nextOperations.Count > MaxOperations) return false;
        lock (gate) { balances.Clear(); operations.Clear(); foreach (var p in nextBalances) balances.Add(p.Key, p.Value); foreach (var p in nextOperations) operations.Add(p.Key, p.Value); }
        return true;
    }

    private double Get(Guid id) => id == Guid.Empty || !balances.TryGetValue(id, out var value) ? 0 : value;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static IndividualWalletResult Result(Guid id, IndividualWalletStatus status) => new(id, status, 0);
}
