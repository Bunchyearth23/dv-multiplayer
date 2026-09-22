using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Networking.Data.Items;

public interface IShopPurchaseBackend
{
    ShopQuote Validate();
    void Prepare();
    bool TryDebit(double total);
    void Commit();
    void Rollback();
}

public readonly struct ShopPurchaseOutcome
{
    public readonly ShopQuote Quote;
    public readonly bool RecoveryRequired;
    public readonly Exception FailureException;
    public readonly string FailurePhase;
    public ShopPurchaseOutcome(ShopQuote quote, bool recoveryRequired = false,
        Exception failureException = null, string failurePhase = null)
    { Quote = quote; RecoveryRequired = recoveryRequired; FailureException = failureException; FailurePhase = failurePhase; }
}

/// <summary>Session-scoped idempotency. Retains terminal outcomes rather than evicting replay protection.</summary>
public sealed class ShopPurchaseLedger
{
    private sealed class Entry
    {
        public string Fingerprint;
        public ShopPurchaseOutcome Outcome = new(new ShopQuote(ShopQuoteStatus.OperationInProgress));
    }

    private readonly Dictionary<(Guid, Guid), Entry> entries = new();
    private readonly int capacity;
    private bool executing;

    public ShopPurchaseLedger(int capacity = 4096)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public ShopPurchaseOutcome Execute(Guid playerId, string operationId, ushort registerId,
        string[] itemIds, int[] quantities, Func<IShopPurchaseBackend> createBackend)
    {
        if (!Guid.TryParseExact(operationId, "N", out var operation) || operation == Guid.Empty ||
            registerId == 0 || itemIds == null || quantities == null || itemIds.Length == 0 ||
            itemIds.Length > ShopCartPolicy.MaxLines || itemIds.Length != quantities.Length)
            return Failure(ShopQuoteStatus.InvalidCart);

        var fingerprint = new StringBuilder().Append(registerId).Append(':');
        for (int i = 0; i < itemIds.Length; i++)
        {
            if (itemIds[i] == null || itemIds[i].Length > ShopCartPolicy.MaxItemIdLength)
                return Failure(ShopQuoteStatus.InvalidCart);
            fingerprint.Append(itemIds[i].Length).Append(':').Append(itemIds[i]).Append(':').Append(quantities[i]).Append(';');
        }
        string request = fingerprint.ToString();
        var key = (playerId, operation);
        if (entries.TryGetValue(key, out var previous))
            return previous.Fingerprint == request ? previous.Outcome : Failure(ShopQuoteStatus.OperationConflict);
        if (entries.Count >= capacity)
            return Failure(ShopQuoteStatus.SessionLimitReached);
        // A game event may synchronously re-enter the transaction path.
        if (executing)
            return Failure(ShopQuoteStatus.OperationInProgress);

        var entry = new Entry { Fingerprint = request };
        entries.Add(key, entry);
        executing = true;
        IShopPurchaseBackend backend = null;
        bool prepareStarted = false;
        bool committed = false;
        string phase = "create-backend";
        try
        {
            backend = createBackend();
            phase = "validate";
            var quote = backend.Validate();
            if (quote.Status != ShopQuoteStatus.Success)
                entry.Outcome = new ShopPurchaseOutcome(quote);
            else
            {
                prepareStarted = true;
                phase = "prepare";
                backend.Prepare();
                // Preparation may invoke Unity/mod callbacks; check price, funds and stock again.
                phase = "revalidate";
                quote = backend.Validate();
                if (quote.Status != ShopQuoteStatus.Success)
                    entry.Outcome = new ShopPurchaseOutcome(quote);
                else if (!TryDebit())
                    entry.Outcome = Failure(ShopQuoteStatus.InsufficientFunds);
                else
                {
                    phase = "commit";
                    backend.Commit();
                    committed = true;
                    entry.Outcome = new ShopPurchaseOutcome(quote);
                }

                bool TryDebit()
                {
                    phase = "debit";
                    return backend.TryDebit(quote.Total);
                }
            }
        }
        catch (Exception ex)
        {
            entry.Outcome = new ShopPurchaseOutcome(new ShopQuote(ShopQuoteStatus.ServerError),
                failureException: ex, failurePhase: phase);
        }
        finally
        {
            if (prepareStarted && !committed)
            {
                try { backend.Rollback(); }
                catch (Exception ex)
                {
                    var original = entry.Outcome.FailureException;
                    entry.Outcome = new ShopPurchaseOutcome(new ShopQuote(ShopQuoteStatus.ServerError), true,
                        original == null ? ex : new AggregateException(original, ex),
                        original == null ? "rollback" : entry.Outcome.FailurePhase + "+rollback");
                }
            }
            executing = false;
        }
        return entry.Outcome;
    }

    private static ShopPurchaseOutcome Failure(ShopQuoteStatus status) => new(new ShopQuote(status));
}
