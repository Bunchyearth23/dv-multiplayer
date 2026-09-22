using System;

namespace Multiplayer.Networking.Data.Items;

/// <summary>Local display only. Never a deposit, reservation, or purchase authority.</summary>
public sealed class ShopPaymentPreview
{
    private uint generation;
    public bool IsPending { get; private set; }
    public double? Amount { get; private set; }

    public uint Begin()
    {
        Clear();
        IsPending = true;
        return generation;
    }

    public bool Complete(uint request, ShopQuoteStatus status, double total)
    {
        if (request != generation || !IsPending) return false;
        IsPending = false;
        if (status == ShopQuoteStatus.Success && !double.IsNaN(total) && !double.IsInfinity(total) && total >= 0)
            Amount = total;
        return true;
    }

    public void Clear()
    {
        unchecked { generation++; }
        IsPending = false;
        Amount = null;
    }
}
