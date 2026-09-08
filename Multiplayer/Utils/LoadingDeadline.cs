using System;
using System.Diagnostics;

namespace Multiplayer.Utils;

/// <summary>Bounds both stalled and slowly progressing loading stages using wall-clock time.</summary>
public sealed class LoadingDeadline
{
    private readonly string stage;
    private readonly double idleLimit, totalLimit, started;
    private readonly Func<double> clock;
    private double lastProgress;
    private string progress;

    public LoadingDeadline(string stage, double idleLimit, double totalLimit, Func<double> clock = null)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException(nameof(stage));
        if (double.IsNaN(idleLimit) || double.IsInfinity(idleLimit) || idleLimit <= 0 ||
            double.IsNaN(totalLimit) || double.IsInfinity(totalLimit) || totalLimit < idleLimit)
            throw new ArgumentOutOfRangeException(nameof(idleLimit));
        this.stage = stage;
        this.idleLimit = idleLimit;
        this.totalLimit = totalLimit;
        this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        started = lastProgress = this.clock();
    }

    public void Check(string currentProgress)
    {
        double now = clock();
        if (now - started >= totalLimit || now - lastProgress >= idleLimit)
            throw new TimeoutException($"Loading timed out during {stage}: {currentProgress ?? "no progress"} " +
                $"(elapsed {now - started:F0}s, without progress {now - lastProgress:F0}s).");
        if (progress != currentProgress)
        {
            progress = currentProgress;
            lastProgress = now;
        }
    }
}
