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
        Check(ledger.Read(Guid.NewGuid(), Guid.NewGuid()).Balance == 0, "wallets leaked");
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
        Check(ledger.Read(player, Guid.NewGuid()).Balance == 5 && callbacks == 1, "concurrent retry was not exactly once");
    }

    public static void ReadsStayFreshAndDoNotConsumeReplayStorage()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid(); var readRequest = Guid.NewGuid();
        for (int i = 0; i < IndividualWalletLedger.MaxOperations * 2; i++) Check(ledger.Read(player, Guid.NewGuid()).Succeeded, "read saturated storage");
        Check(ledger.SnapshotOperations().Count == 0, "read entered durable replay journal");
        Check(ledger.Read(player, readRequest).Balance == 0, "initial read failed");
        Check(ledger.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Credit, 7).Succeeded, "reads blocked later mutation");
        Check(ledger.Read(player, readRequest).Balance == 7, "repeated read returned stale replay");
    }

    public static void ConcurrentEnsureInitializesExactlyOnce()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid(); int callbacks = 0;
        Parallel.For(0, 32, i => ledger.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Ensure, i + 1, _ => System.Threading.Interlocked.Increment(ref callbacks)));
        double balance = ledger.Read(player, Guid.NewGuid()).Balance;
        Check(balance >= 1 && balance <= 32 && callbacks == 1, "concurrent ensure initialized more than once");
    }

    public static void ReloadedZeroBalanceIsNotRecredited()
    {
        var player = Guid.NewGuid(); var source = new IndividualWalletLedger();
        source.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Ensure, 10);
        source.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Debit, 10);
        var restored = new IndividualWalletLedger();
        Check(IndividualWalletStoreCodec.TryRead(IndividualWalletStoreCodec.Write(source), restored), "restore failed");
        var ensured = restored.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Ensure, 10);
        Check(ensured.Succeeded && ensured.Balance == 0, "reload recredited initialized zero balance");
    }

    public static void EnsureRequestIdConflictIsRejected()
    {
        var ledger = new IndividualWalletLedger(); var player = Guid.NewGuid(); var request = Guid.NewGuid();
        var first = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Ensure, 10);
        var replay = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Ensure, 10);
        var conflict = ledger.Execute(player, Guid.Empty, request, WalletOperationKind.Ensure, 11);
        Check(first.Balance == 10 && replay.IsReplay && conflict.Status == IndividualWalletStatus.OperationConflict, "ensure request identity is not immutable");
    }

    public static void VersionOneZeroBalanceMigratesAsInitialized()
    {
        var player = Guid.NewGuid(); var source = new IndividualWalletLedger();
        source.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Credit, 10);
        source.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Debit, 10);
        var legacy = IndividualWalletStoreCodec.Write(source); legacy["Version"] = 1; legacy.Remove("Initialized");
        var restored = new IndividualWalletLedger();
        Check(IndividualWalletStoreCodec.TryRead(legacy, restored), "v1 restore failed");
        Check(restored.Execute(player, Guid.Empty, Guid.NewGuid(), WalletOperationKind.Ensure, 10).Balance == 0, "v1 zero balance was treated as new");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
