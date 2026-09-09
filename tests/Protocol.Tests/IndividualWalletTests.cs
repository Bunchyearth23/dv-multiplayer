using MPAPI.Types;
using Multiplayer.Networking.Data.Wallets;
using System;
using System.Linq;
using System.Threading.Tasks;

internal static class IndividualWalletTests
{
    public static void PlayersAreIsolatedAndTransfersAreAtomic()
    {
        var ledger = new IndividualWalletLedger(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Check(ledger.Execute(a, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Credit, 100).Succeeded, "credit failed");
        var transfer = ledger.Execute(a, b, Guid.NewGuid(), WalletOperationKind.Transfer, 35);
        Check(transfer.Succeeded && transfer.Balance == 65 && transfer.CounterpartyBalance == 35, "transfer not atomic");
        Check(ledger.Execute(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), WalletOperationKind.Read, 0).Balance == 0, "wallets leaked");
    }

    public static void RetriesAreExactlyOnceAndImmutable()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid(); var request = Guid.NewGuid();
        var first = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 10);
        var replay = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 10);
        var conflict = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 11);
        Check(first.Balance == 10 && replay.Balance == 10 && replay.IsReplay, "retry executed twice");
        Check(conflict.Status == IndividualWalletStatus.OperationConflict, "changed request accepted");
    }

    public static void InvalidAndOverflowValuesAreRejected()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid();
        foreach (double value in new[] { 0, -1, double.NaN, double.PositiveInfinity, IndividualWalletLedger.MaxAmount + 1 })
            Check(ledger.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Credit, value).Status == IndividualWalletStatus.InvalidAmount, "invalid amount accepted");
        Check(ledger.Execute(player, player, Guid.NewGuid(), WalletOperationKind.Transfer, 1).Status == IndividualWalletStatus.InvalidPlayer, "self transfer accepted");
    }

    public static void SaveReloadPreservesBalancesAndReplayProtection()
    {
        var source = new IndividualWalletLedger(); var player = Guid.NewGuid(); var request = Guid.NewGuid();
        source.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 42);
        var json = IndividualWalletStoreCodec.Write(source); var restored = new IndividualWalletLedger();
        Check(IndividualWalletStoreCodec.TryRead(json, restored), "restore failed");
        var replay = restored.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 42);
        Check(replay.IsReplay && replay.Balance == 42, "durable replay protection failed");
    }

    public static void ConcurrentRetryMutatesOnceAndCallbacksAreIsolated()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid(); var request = Guid.NewGuid(); int callbacks = 0;
        Parallel.For(0, 32, _ => ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Credit, 5, change => { System.Threading.Interlocked.Increment(ref callbacks); throw new Exception("adapter"); }));
        Check(ledger.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Read, 0).Balance == 5 && callbacks == 1, "concurrent retry was not exactly once");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
