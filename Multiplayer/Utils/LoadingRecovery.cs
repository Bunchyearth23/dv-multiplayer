using System;
using System.Diagnostics;
using System.Linq;

namespace Multiplayer.Utils;

/// <summary>Rate limits targeted recovery without extending the loading deadline.</summary>
public sealed class LoadingRecovery
{
    public const int MaxBatch = 128;
    private readonly Func<double> clock;
    private double next;
    private ushort last;
    public LoadingRecovery(Func<double> clock = null)
    {
        this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        next = this.clock() + 5;
    }
    public bool TryTake(ushort[] missing, out ushort[] batch)
    {
        batch = Array.Empty<ushort>();
        if (missing == null || missing.Length == 0 || clock() < next) return false;
        if (missing.Any(id => id == 0) || missing.Distinct().Count() != missing.Length)
            throw new ArgumentException("Invalid recovery IDs.");
        next = clock() + 5;
        // Rotate through missing IDs so one permanently failing object cannot starve later batches.
        var ordered = missing.OrderBy(id => id).ToArray();
        batch = ordered.Where(id => id > last).Concat(ordered.Where(id => id <= last)).Take(MaxBatch).ToArray();
        last = batch[batch.Length - 1];
        return true;
    }
}
