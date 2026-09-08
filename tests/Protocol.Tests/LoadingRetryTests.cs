using System;
using Multiplayer.Utils;

internal static class LoadingRetryTests
{
    public static void RetryIsRateLimitedAndMonotone()
    {
        double now = 10;
        var retry = new LoadingRetry(5, () => now);
        Check(!retry.TryTake());
        now = 14.999; Check(!retry.TryTake());
        now = 15; Check(retry.TryTake());
        Check(!retry.TryTake());
        now = 20; Check(retry.TryTake());
    }

    public static void InvalidIntervalsAreRejected()
    {
        foreach (double value in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            try { _ = new LoadingRetry(value); throw new Exception("Invalid retry interval accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Loading retry invariant failed");
    }
}
