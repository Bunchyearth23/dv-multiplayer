using System;
using System.Linq;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Packets.Serverbound;

internal static class ShopPurchaseTests
{
    private static readonly Guid Player = Guid.NewGuid();
    private static string Operation() => Guid.NewGuid().ToString("N");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Backend : IShopPurchaseBackend
    {
        public double Wallet = 100;
        public int Stock = 1, Objects, Debits, Prepares, Commits, Rollbacks, Validations;
        public string Fail;
        public Action DuringPrepare;
        private bool charged, counted;
        public ShopQuote Validate()
        {
            Validations++;
            return ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 1 }, Wallet, id => new ShopOffer(100, Stock));
        }
        public void Prepare()
        {
            Prepares++; Objects++;
            DuringPrepare?.Invoke();
            if (Fail == "prepare") throw new Exception("Failed prefab");
        }
        public bool TryDebit(double total)
        {
            if (Fail == "funds") return false;
            Debits++; Wallet -= total; charged = true;
            if (Fail == "debit") throw new Exception("Failed money subscriber");
            return true;
        }
        public void Commit()
        {
            Commits++; Stock--; counted = true;
            if (Fail == "commit") throw new Exception("Failed activation");
        }
        public void Rollback()
        {
            Rollbacks++; Objects = 0;
            if (counted) { Stock++; counted = false; }
            if (charged) { Wallet += 100; charged = false; }
            if (Fail == "rollback") throw new Exception("Failed compensation");
        }
    }

    private static ShopPurchaseOutcome Buy(ShopPurchaseLedger ledger, string operation, Backend backend,
        Guid? player = null, string itemId = "lamp", int quantity = 1) =>
        ledger.Execute(player ?? Player, operation, 1, new[] { itemId }, new[] { quantity }, () => backend);

    public static void SuccessfulPurchaseAndReplay()
    {
        var ledger = new ShopPurchaseLedger(); var backend = new Backend(); string id = Operation();
        var first = Buy(ledger, id, backend);
        var replay = Buy(ledger, id, backend);
        Check(first.Quote.Status == ShopQuoteStatus.Success && replay.Quote.Status == first.Quote.Status, "Replay lost result");
        Check(backend.Wallet == 0 && backend.Stock == 0 && backend.Objects == 1 && backend.Debits == 1 &&
            backend.Prepares == 1 && backend.Commits == 1 && backend.Validations == 2, "Purchase repeated or skipped revalidation");
    }

    public static void OperationPayloadConflict()
    {
        var ledger = new ShopPurchaseLedger(); var backend = new Backend(); string id = Operation();
        Buy(ledger, id, backend);
        Check(Buy(ledger, id, backend, itemId: "radio").Quote.Status == ShopQuoteStatus.OperationConflict, "Changed item accepted");
        Check(Buy(ledger, id, backend, quantity: 2).Quote.Status == ShopQuoteStatus.OperationConflict, "Changed quantity accepted");
        Check(backend.Debits == 1, "Conflicting request charged");
    }

    public static void PreparationFailureCompensated()
    {
        var ledger = new ShopPurchaseLedger(); var backend = new Backend { Fail = "prepare" }; string id = Operation();
        var result = Buy(ledger, id, backend);
        Check(result.Quote.Status == ShopQuoteStatus.ServerError && backend.Debits == 0 && backend.Objects == 0, "Prepare failure leaked state");
        Buy(ledger, id, backend);
        Check(backend.Prepares == 1 && backend.Rollbacks == 1, "Failed operation repeated");
    }

    public static void DebitAndCommitFailuresCompensated()
    {
        foreach (string failure in new[] { "debit", "commit", "funds" })
        {
            var backend = new Backend { Fail = failure };
            var result = Buy(new ShopPurchaseLedger(), Operation(), backend);
            Check(result.Quote.Status != ShopQuoteStatus.Success && !result.RecoveryRequired, "Failed transaction succeeded");
            Check(backend.Wallet == 100 && backend.Stock == 1 && backend.Objects == 0 && backend.Rollbacks == 1, "Compensation incomplete");
        }
    }

    public static void StockRecheckedAfterPreparation()
    {
        var backend = new Backend(); backend.DuringPrepare = () => backend.Stock = 0;
        var result = Buy(new ShopPurchaseLedger(), Operation(), backend);
        Check(result.Quote.Status == ShopQuoteStatus.InsufficientStock && backend.Debits == 0 && backend.Objects == 0,
            "Stock change during preparation ignored");
    }

    public static void FailedRecoveryIsRetained()
    {
        var ledger = new ShopPurchaseLedger(); var backend = new Backend { Fail = "rollback" };
        backend.DuringPrepare = () => backend.Stock = 0;
        string id = Operation(); var result = Buy(ledger, id, backend);
        Check(result.RecoveryRequired && result.Quote.Status == ShopQuoteStatus.ServerError, "Recovery failure hidden from first caller");
        Check(Buy(ledger, id, backend).RecoveryRequired && backend.Rollbacks == 1, "Recovery failure not retained");
    }

    public static void CapacityDoesNotEvictReplayProtection()
    {
        var ledger = new ShopPurchaseLedger(1); var backend = new Backend(); string id = Operation();
        Buy(ledger, id, backend);
        Check(Buy(ledger, Operation(), new Backend()).Quote.Status == ShopQuoteStatus.SessionLimitReached, "Ledger limit ignored");
        Check(Buy(ledger, id, backend).Quote.Status == ShopQuoteStatus.Success && backend.Debits == 1, "Replay entry evicted");
    }

    public static void ReentrantPurchasesDoNotOverlap()
    {
        var ledger = new ShopPurchaseLedger(); var backend = new Backend(); string id = Operation();
        var other = new Backend();
        backend.DuringPrepare = () =>
        {
            Check(Buy(ledger, id, backend).Quote.Status == ShopQuoteStatus.OperationInProgress, "Duplicate reentry allowed");
            Check(Buy(ledger, Operation(), other).Quote.Status == ShopQuoteStatus.OperationInProgress, "Concurrent reentry allowed");
        };
        Check(Buy(ledger, id, backend).Quote.Status == ShopQuoteStatus.Success && other.Prepares == 0, "Reentry changed transaction");
    }

    public static void PlayersHaveSeparateOperationNamespaces()
    {
        var ledger = new ShopPurchaseLedger(); string id = Operation();
        Check(Buy(ledger, id, new Backend()).Quote.Status == ShopQuoteStatus.Success, "First player failed");
        Check(Buy(ledger, id, new Backend(), Guid.NewGuid()).Quote.Status == ShopQuoteStatus.Success, "Unrelated player operation collided");
    }

    public static void InvalidOperationsHaveNoSideEffects()
    {
        foreach (string operation in new[] { null, "invalid", Guid.Empty.ToString("N") })
        {
            var backend = new Backend();
            Check(Buy(new ShopPurchaseLedger(), operation, backend).Quote.Status == ShopQuoteStatus.InvalidCart && backend.Prepares == 0,
                "Invalid operation created objects");
        }
        Check(ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 129 }, 1000, id => new ShopOffer(1, 1000)).Status ==
            ShopQuoteStatus.InvalidCart, "Unbounded purchase quantity accepted");
    }

    public static void PurchasePacketsRoundTrip()
    {
        var processor = new NetPacketProcessor(); string operation = Operation(); bool received = false;
        processor.SubscribeReusable<ServerboundShopPurchaseRequestPacket>(packet =>
        {
            Check(packet.OperationId == operation && packet.TicketId == 9 && packet.RegisterNetId == 2 &&
                packet.ItemIds.SequenceEqual(new[] { "lamp" }) && packet.Quantities.SequenceEqual(new[] { 1 }), "Purchase request changed");
            received = true;
        });
        var writer = new NetDataWriter();
        processor.Write(writer, new ServerboundShopPurchaseRequestPacket
        { OperationId = operation, TicketId = 9, RegisterNetId = 2, ItemIds = new[] { "lamp" }, Quantities = new[] { 1 } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received, "Purchase request not dispatched");
        writer.Reset();
        IRpcResponse sent = new ShopPurchaseResponse { OperationId = operation, RegisterNetId = 2,
            Total = 100, Status = ShopQuoteStatus.ServerError, RecoveryRequired = true };
        sent.Serialize(writer);
        var result = new ShopPurchaseResponse(); result.Deserialize(new NetDataReader(writer.CopyData()));
        Check(result.OperationId == operation && result.RegisterNetId == 2 && result.Total == 100 && result.RecoveryRequired &&
            result.Status == ShopQuoteStatus.ServerError, "Purchase response changed");
    }
}
