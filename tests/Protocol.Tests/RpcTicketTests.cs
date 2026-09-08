using System;
using Multiplayer.Networking.Data.RPCs;

internal static class RpcTicketTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("RPC invariant failed."); }

    public static void DeadlineRejectsLateResponses()
    {
        double now = 10;
        int expired = 0, resolved = 0;
        var ticket = new RpcTicket(1, 2, () => now).OnTimeout(() => expired++).OnResolve(_ => resolved++);
        now = 11.9; ticket.CheckExpiry(); Check(!ticket.IsFinished);
        now = 12; ticket.Resolve(new ShopQuoteResponse());
        ticket.CheckExpiry(); ticket.Cancel(); ticket.Resolve(new ShopQuoteResponse());
        Check(ticket.IsExpired && expired == 1 && resolved == 0);
    }

    public static void ResolutionIsTerminalDespiteCallbackFailure()
    {
        int calls = 0;
        var ticket = new RpcTicket(1, 10, () => 0).OnResolve(_ => { calls++; throw new Exception(); });
        try { ticket.Resolve(new ShopQuoteResponse()); } catch { }
        ticket.Resolve(new ShopQuoteResponse()); ticket.Cancel();
        Check(ticket.IsResolved && calls == 1);
    }

    public static void CancellationRunsExactlyOnce()
    {
        int cancelled = 0, timedOut = 0;
        var ticket = new RpcTicket(1, 10, () => 0).OnTimeout(() => timedOut++)
            .OnCancelled(() => cancelled++);
        ticket.Cancel(); ticket.Cancel(); ticket.CheckExpiry(); ticket.Resolve(new ShopQuoteResponse());
        Check(ticket.IsCancelled && cancelled == 1 && timedOut == 0);
        new RpcTicket(2, 10, () => 0).OnTimeout(() => timedOut++).Cancel();
        Check(timedOut == 1);
    }

    public static void TimeoutRetryDoesNotCorruptCollection()
    {
        double now = 0;
        int errors = 0, timeouts = 0;
        var collection = new RpcTicketCollection(_ => errors++, () => now);
        RpcTicket retry = null;
        collection.Create(1).OnTimeout(() => { timeouts++; retry = collection.Create(5); });
        collection.Create(1).OnTimeout(() => { timeouts++; throw new Exception("callback failure"); });
        now = 1; collection.Poll();
        Check(timeouts == 2 && errors == 1 && collection.Count == 1 && !retry.IsFinished);
        collection.Resolve(retry.TicketId, new ShopQuoteResponse());
        Check(collection.Count == 0 && retry.IsResolved);
    }

    public static void CancelAllIsolatesCallbacksAndPreventsRetry()
    {
        int errors = 0, completed = 0;
        var collection = new RpcTicketCollection(_ => errors++, () => 0);
        var first = collection.Create(5).OnCancelled(() => collection.Create(1));
        var second = collection.Create(5).OnCancelled(() => { collection.CancelAll(); completed++; });
        collection.CancelAll(); collection.CancelAll();
        Check(first.IsCancelled && second.IsCancelled && errors == 1 && completed == 1 && collection.Count == 0);
        var next = collection.Create(5);
        Check(next.TicketId != first.TicketId && !collection.Resolve(first.TicketId, new ShopQuoteResponse()));
    }

    public static void ResolveRemovesBeforeReentrantCallback()
    {
        int errors = 0, calls = 0;
        var collection = new RpcTicketCollection(_ => errors++, () => 0);
        var ticket = collection.Create(5);
        ticket.OnResolve(_ =>
        {
            calls++;
            Check(!collection.Resolve(ticket.TicketId, new ShopQuoteResponse()));
            collection.Create(5);
            throw new Exception();
        });
        Check(!collection.Resolve(ticket.TicketId, null) && collection.Count == 1);
        Check(collection.Resolve(ticket.TicketId, new ShopQuoteResponse()));
        Check(calls == 1 && errors == 1 && collection.Count == 1);
    }
}
