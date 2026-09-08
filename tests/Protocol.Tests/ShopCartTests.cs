using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Packets.Serverbound;

internal static class ShopCartTests
{
    private static readonly Dictionary<string, ShopOffer> Catalog = new Dictionary<string, ShopOffer>
    {
        ["lamp"] = new ShopOffer(100, 3), ["radio"] = new ShopOffer(250, 2), ["free"] = new ShopOffer(0, 1)
    };

    private static ShopQuote Quote(string[] ids, int[] quantities, double wallet = 1000) =>
        ShopCartPolicy.Quote(ids, quantities, wallet, id => Catalog.TryGetValue(id, out var offer) ? offer : (ShopOffer?)null);

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static void ServerPricesAndWallet()
    {
        var quote = Quote(new[] { "lamp", "radio" }, new[] { 2, 1 }, 450);
        Check(quote.Status == ShopQuoteStatus.Success && quote.Total == 450 && quote.FailedLine == -1, "Wrong quote");
        quote = Quote(new[] { "lamp", "radio" }, new[] { 2, 1 }, 449);
        Check(quote.Status == ShopQuoteStatus.InsufficientFunds && quote.Total == 450, "Insufficient funds accepted");
        Check(Quote(new[] { "free" }, new[] { 1 }, 0).Status == ShopQuoteStatus.Success, "Free sandbox item rejected");
    }

    public static void InvalidCartShape()
    {
        Check(Quote(null, null).Status == ShopQuoteStatus.InvalidCart, "Null cart accepted");
        Check(Quote(new string[0], new int[0]).Status == ShopQuoteStatus.InvalidCart, "Empty cart accepted");
        Check(Quote(new[] { "lamp" }, new int[0]).Status == ShopQuoteStatus.InvalidCart, "Mismatched arrays accepted");
        Check(Quote(new string[65], new int[65]).Status == ShopQuoteStatus.InvalidCart, "Oversized cart accepted");
        foreach (string id in new[] { null, " ", new string('x', 257) })
            Check(Quote(new[] { id }, new[] { 1 }).Status == ShopQuoteStatus.InvalidCart, "Invalid identifier accepted");
    }

    public static void QuantitiesAndDuplicateLines()
    {
        foreach (int quantity in new[] { int.MinValue, -1, 0, 1001, int.MaxValue })
            Check(Quote(new[] { "lamp" }, new[] { quantity }).Status == ShopQuoteStatus.InvalidCart, "Invalid quantity accepted");
        var quote = Quote(new[] { "lamp", "lamp" }, new[] { 2, 2 });
        Check(quote.Status == ShopQuoteStatus.InvalidCart && quote.FailedLine == 1, "Duplicate stock bypass accepted");
    }

    public static void AvailabilityAndStock()
    {
        Check(Quote(new[] { "absent" }, new[] { 1 }).Status == ShopQuoteStatus.UnavailableItem, "Unavailable item accepted");
        var quote = Quote(new[] { "lamp", "radio" }, new[] { 1, 3 });
        Check(quote.Status == ShopQuoteStatus.InsufficientStock && quote.FailedLine == 1, "Missing stock accepted");
        Check(quote.Total == 0, "Partial cart total escaped as a valid quote");
    }

    public static void InvalidPricesAndOverflow()
    {
        foreach (double price in new[] { double.NaN, double.PositiveInfinity, -1, double.MaxValue })
        {
            var quote = ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 2 }, 1000, id => new ShopOffer(price, 2));
            Check(quote.Status == ShopQuoteStatus.InvalidCatalog, "Invalid price or overflow accepted");
        }
        foreach (double wallet in new[] { double.NaN, double.PositiveInfinity, -1 })
            Check(Quote(new[] { "lamp" }, new[] { 1 }, wallet).Status == ShopQuoteStatus.InvalidCatalog, "Invalid wallet accepted");
        Check(ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 1 }, 1000, id => new ShopOffer(1, -1)).Status ==
            ShopQuoteStatus.InvalidCatalog, "Negative stock accepted");
    }

    public static void QuotesAreReadOnlyAndRevalidated()
    {
        int stock = 1; double wallet = 100;
        Func<string, ShopOffer?> lookup = id => new ShopOffer(100, stock);
        var first = ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 1 }, wallet, lookup);
        var retry = ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 1 }, wallet, lookup);
        Check(first.Status == ShopQuoteStatus.Success && retry.Status == first.Status && stock == 1 && wallet == 100,
            "Quote or retry mutated stock/funds");
        stock = 0;
        Check(ShopCartPolicy.Quote(new[] { "lamp" }, new[] { 1 }, wallet, lookup).Status == ShopQuoteStatus.InsufficientStock,
            "Stale stock reused");
    }

    public static void QuoteRequestWireRoundTrip()
    {
        var processor = new NetPacketProcessor();
        bool received = false;
        processor.SubscribeReusable<ServerboundShopQuoteRequestPacket>(packet =>
        {
            Check(packet.TicketId == 12 && packet.RegisterNetId == 5 &&
                packet.ItemIds.SequenceEqual(new[] { "lamp", "radio" }) && packet.Quantities.SequenceEqual(new[] { 2, 1 }),
                "Request changed on the wire");
            received = true;
        });
        var writer = new NetDataWriter();
        processor.Write(writer, new ServerboundShopQuoteRequestPacket
        { TicketId = 12, RegisterNetId = 5, ItemIds = new[] { "lamp", "radio" }, Quantities = new[] { 2, 1 } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received, "Request handler not invoked");
    }

    public static void QuoteResponseWireRoundTrip()
    {
        foreach (ShopQuoteStatus status in Enum.GetValues(typeof(ShopQuoteStatus)))
        {
            var writer = new NetDataWriter();
            new ShopQuoteResponse { RegisterNetId = 8, Status = status, Total = 123.5, FailedLine = 2 }.Serialize(writer);
            var response = new ShopQuoteResponse();
            response.Deserialize(new NetDataReader(writer.CopyData()));
            Check(response.RegisterNetId == 8 && response.Status == status && response.Total == 123.5 && response.FailedLine == 2,
                "Quote response changed on the wire");
        }
    }
}
