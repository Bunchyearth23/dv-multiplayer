using System;
using Multiplayer.Networking.Data.Items;

internal static class ShopPaymentPreviewTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static void QuoteIsPresentationOnly()
    {
        var preview = new ShopPaymentPreview();
        var request = preview.Begin();
        Check(preview.IsPending && !preview.Amount.HasValue, "Pending wallet already accepted");
        Check(preview.Complete(request, ShopQuoteStatus.Success, 125) && preview.Amount == 125 && !preview.IsPending,
            "Server quote not shown");
        Check(!preview.Complete(request, ShopQuoteStatus.Success, 250) && preview.Amount == 125,
            "Duplicate response overwrote accepted quote");
        preview.Clear();
        Check(!preview.Amount.HasValue && !preview.IsPending, "Cancel retained displayed funds");
    }

    public static void ChangedBasketAndUnloadRejectLateQuote()
    {
        var preview = new ShopPaymentPreview();
        var old = preview.Begin();
        preview.Clear(); // Basket change, cancel, or register unload.
        Check(!preview.Complete(old, ShopQuoteStatus.Success, 100) && !preview.Amount.HasValue,
            "Late quote republished after invalidation");
        var current = preview.Begin();
        Check(!preview.Complete(old, ShopQuoteStatus.InsufficientFunds, 0) && preview.IsPending,
            "Old failure cancelled a newer request");
        Check(preview.Complete(current, ShopQuoteStatus.Success, 30) && preview.Amount == 30,
            "Current quote was lost");
    }

    public static void FailedOrMalformedQuoteNeverShowsFunds()
    {
        var preview = new ShopPaymentPreview();
        foreach (var status in new[] { ShopQuoteStatus.InsufficientFunds, ShopQuoteStatus.NotReady,
            ShopQuoteStatus.PermissionDenied, ShopQuoteStatus.ServerError })
        {
            preview.Complete(preview.Begin(), status, 100);
            Check(!preview.Amount.HasValue && !preview.IsPending, "Rejected quote shown as payment");
        }
        foreach (double total in new[] { double.NaN, double.PositiveInfinity, -1d })
        {
            preview.Complete(preview.Begin(), ShopQuoteStatus.Success, total);
            Check(!preview.Amount.HasValue, "Malformed quote shown as payment");
        }
        preview.Complete(preview.Begin(), ShopQuoteStatus.Success, 0);
        Check(preview.Amount == 0, "Free item rejected");
    }

    public static void PlayersDoNotSharePaymentPreview()
    {
        var host = new ShopPaymentPreview();
        var client = new ShopPaymentPreview();
        host.Complete(host.Begin(), ShopQuoteStatus.Success, 100);
        client.Complete(client.Begin(), ShopQuoteStatus.Success, 50);
        host.Clear();
        Check(client.Amount == 50, "Host cancellation changed remote basket funding");
    }
}
