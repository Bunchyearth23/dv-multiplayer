using System;
using System.Diagnostics;

namespace Multiplayer.Utils;

/// <summary>Rate limits a whole-stage retry without affecting its loading deadline.</summary>
public sealed class LoadingRetry
{
    private readonly Func<double> clock;
    private readonly double interval;
    private double next;

    public LoadingRetry(double intervalSeconds = 5, Func<double> clock = null)
    {
        if (intervalSeconds <= 0 || double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds))
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        interval = intervalSeconds;
        this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        next = this.clock() + interval;
    }

    public bool TryTake()
    {
        double now = clock();
        if (now < next) return false;
        next = now + interval;
        return true;
    }
}
