using System;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.RPCs;

internal static class ShopPurchaseOperationTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Purchase operation invariant failed."); }

    public static void RetryKeepsOriginalIntent()
    {
        var ids = new[] { "lamp" };
        var counts = new[] { 2 };
        var operation = new ShopPurchaseOperation(1, ids, counts);
        ids[0] = "radio"; counts[0] = 9;
        var first = operation.CreateRequest(1);
        first.ItemIds[0] = "other"; first.Quantities[0] = 4;
        var retry = operation.CreateRequest(2);
        Check(first.OperationId == retry.OperationId && retry.TicketId == 2 &&
            retry.ItemIds[0] == "lamp" && retry.Quantities[0] == 2 && !operation.IsComplete);
    }

    public static void ResponsesMustMatchPendingPurchase()
    {
        var operation = new ShopPurchaseOperation(1, new[] { "lamp" }, new[] { 1 });
        var response = new ShopPurchaseResponse { OperationId = "wrong", RegisterNetId = 1, Status = ShopQuoteStatus.Success };
        Check(!operation.Accept(response) && !operation.IsComplete);
        response.OperationId = operation.OperationId; response.RegisterNetId = 2;
        Check(!operation.Accept(response));
        response.RegisterNetId = 1; response.Status = (ShopQuoteStatus)255;
        Check(!operation.Accept(response));
        response.Status = ShopQuoteStatus.Success; response.Total = double.NaN;
        Check(!operation.Accept(response));
        response.Total = 5;
        Check(operation.Accept(response) && operation.IsComplete && !operation.Accept(response));
    }

    public static void InProgressAndRecoveryPreserveIntent()
    {
        var operation = new ShopPurchaseOperation(1, new[] { "lamp" }, new[] { 1 });
        var response = new ShopPurchaseResponse { OperationId = operation.OperationId, RegisterNetId = 1,
            Status = ShopQuoteStatus.OperationInProgress };
        Check(operation.Accept(response) && !operation.IsComplete);
        response.Status = ShopQuoteStatus.ServerError; response.RecoveryRequired = true;
        Check(operation.Accept(response) && operation.IsComplete && operation.RecoveryRequired);
    }

    public static void LostResponseReplaysBeforeWorldValidation()
    {
        var ledger = new ShopPurchaseLedger();
        var player = Guid.NewGuid();
        var operation = new ShopPurchaseOperation(1, new[] { "lamp" }, new[] { 1 });
        var request = operation.CreateRequest(1);
        var backend = new Backend();
        var first = ledger.Execute(player, request.OperationId, request.RegisterNetId, request.ItemIds,
            request.Quantities, () => backend);
        Check(first.Quote.Status == ShopQuoteStatus.Success && backend.Debits == 1);
        // The first response is lost. The original register/player may no longer be usable.
        request = operation.CreateRequest(2);
        var replay = ledger.Execute(player, request.OperationId, request.RegisterNetId, request.ItemIds,
            request.Quantities, () => throw new Exception("World validation must not run for a replay."));
        Check(replay.Quote.Status == ShopQuoteStatus.Success && backend.Debits == 1);
        Check(operation.Accept(new ShopPurchaseResponse { RegisterNetId = 1, OperationId = request.OperationId,
            Status = replay.Quote.Status, Total = replay.Quote.Total }));
    }

    private sealed class Backend : IShopPurchaseBackend
    {
        public int Debits;
        public ShopQuote Validate() => new ShopQuote(ShopQuoteStatus.Success, 5);
        public void Prepare() { }
        public bool TryDebit(double total) { ++Debits; return true; }
        public void Commit() { }
        public void Rollback() { throw new Exception("Unexpected rollback"); }
    }
}
