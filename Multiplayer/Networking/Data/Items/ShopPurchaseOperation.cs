using System;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Packets.Serverbound;

namespace Multiplayer.Networking.Data.Items;

/// <summary>One immutable client intent. Timeouts must retry this operation, never create a new one.</summary>
public sealed class ShopPurchaseOperation
{
    private readonly string[] itemIds;
    private readonly int[] quantities;
    public string OperationId { get; } = Guid.NewGuid().ToString("N");
    public ushort RegisterNetId { get; }
    public bool IsComplete { get; private set; }
    public bool RecoveryRequired { get; private set; }
    public ShopQuote? Outcome { get; private set; }

    public ShopPurchaseOperation(ushort registerNetId, string[] itemIds, int[] quantities)
    {
        if (registerNetId == 0 || ShopCartPolicy.Quote(itemIds, quantities, 0,
            _ => new ShopOffer(0, ShopCartPolicy.MaxQuantity)).Status != ShopQuoteStatus.Success)
            throw new ArgumentException("Invalid shop purchase intent.");
        RegisterNetId = registerNetId;
        this.itemIds = (string[])itemIds.Clone();
        this.quantities = (int[])quantities.Clone();
    }

    public ServerboundShopPurchaseRequestPacket CreateRequest(uint ticketId) => new()
    {
        TicketId = ticketId, OperationId = OperationId, RegisterNetId = RegisterNetId,
        ItemIds = (string[])itemIds.Clone(), Quantities = (int[])quantities.Clone()
    };

    public bool Accept(ShopPurchaseResponse response)
    {
        if (IsComplete || response == null || response.OperationId != OperationId ||
            response.RegisterNetId != RegisterNetId || !Enum.IsDefined(typeof(ShopQuoteStatus), response.Status) ||
            double.IsNaN(response.Total) || double.IsInfinity(response.Total) || response.Total < 0 ||
            response.FailedLine < -1 || response.FailedLine >= itemIds.Length)
            return false;
        RecoveryRequired = response.RecoveryRequired;
        IsComplete = response.Status != ShopQuoteStatus.OperationInProgress;
        if (IsComplete)
            Outcome = new ShopQuote(response.Status, response.Total, response.FailedLine);
        return true;
    }
}
