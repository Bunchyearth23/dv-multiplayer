using System;
using System.Collections.Generic;

namespace Multiplayer.Networking.Data.Items;

public enum ShopQuoteStatus : byte
{
    Success,
    InvalidCart,
    UnavailableItem,
    InsufficientStock,
    InsufficientFunds,
    InvalidCatalog,
    InvalidShop,
    OutOfReach,
    NotReady,
    ServerError,
    OperationConflict,
    OperationInProgress,
    SessionLimitReached,
    PermissionDenied
}

public readonly struct ShopOffer
{
    public readonly double UnitPrice;
    public readonly int Stock;
    public ShopOffer(double unitPrice, int stock) { UnitPrice = unitPrice; Stock = stock; }
}

public readonly struct ShopQuote
{
    public readonly ShopQuoteStatus Status;
    public readonly double Total;
    public readonly int FailedLine;
    public ShopQuote(ShopQuoteStatus status, double total = 0, int failedLine = -1)
    { Status = status; Total = total; FailedLine = failedLine; }
}

/// <summary>Read-only quote. An eventual purchase must revalidate before committing.</summary>
public static class ShopCartPolicy
{
    public const int MaxLines = 64;
    public const int MaxQuantity = 1000;
    public const int MaxTotalQuantity = 128;
    public const int MaxItemIdLength = 256;

    public static ShopQuote Quote(string[] itemIds, int[] quantities, double wallet,
        Func<string, ShopOffer?> lookup)
    {
        if (itemIds == null || quantities == null || itemIds.Length == 0 ||
            itemIds.Length > MaxLines || itemIds.Length != quantities.Length || lookup == null)
            return new ShopQuote(ShopQuoteStatus.InvalidCart);
        if (!Finite(wallet) || wallet < 0)
            return new ShopQuote(ShopQuoteStatus.InvalidCatalog);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        double total = 0;
        int totalQuantity = 0;
        for (int i = 0; i < itemIds.Length; i++)
        {
            string id = itemIds[i];
            int quantity = quantities[i];
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxItemIdLength ||
                !seen.Add(id) || quantity <= 0 || quantity > MaxQuantity)
                return new ShopQuote(ShopQuoteStatus.InvalidCart, failedLine: i);
            totalQuantity += quantity;
            if (totalQuantity > MaxTotalQuantity)
                return new ShopQuote(ShopQuoteStatus.InvalidCart, failedLine: i);
            var offer = lookup(id);
            if (!offer.HasValue)
                return new ShopQuote(ShopQuoteStatus.UnavailableItem, failedLine: i);
            if (!Finite(offer.Value.UnitPrice) || offer.Value.UnitPrice < 0 || offer.Value.Stock < 0)
                return new ShopQuote(ShopQuoteStatus.InvalidCatalog, failedLine: i);
            if (quantity > offer.Value.Stock)
                return new ShopQuote(ShopQuoteStatus.InsufficientStock, failedLine: i);
            total += offer.Value.UnitPrice * quantity;
            if (!Finite(total))
                return new ShopQuote(ShopQuoteStatus.InvalidCatalog, failedLine: i);
        }
        return new ShopQuote(total > wallet ? ShopQuoteStatus.InsufficientFunds : ShopQuoteStatus.Success, total);
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
