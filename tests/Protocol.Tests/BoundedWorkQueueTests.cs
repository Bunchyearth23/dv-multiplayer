using System;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Utils;

internal static class BoundedWorkQueueTests
{
    public static void OverflowPreservesOrderAndCapacity()
    {
        var queue = new BoundedWorkQueue<int>(2, 10);
        Check(queue.TryEnqueue(1, 6), "first admission");
        Check(!queue.TryEnqueue(99, 5), "byte overflow");
        Check(queue.TryEnqueue(2, 4), "exact byte limit");
        Check(!queue.TryEnqueue(99, 0), "count overflow");
        Check(queue.Count == 2 && queue.Bytes == 10, "failed admissions leave accounting intact");
        Check(queue.TryDequeue(out var first) && first == 1, "overflow cannot overtake FIFO");
        Check(queue.TryDequeue(out var second) && second == 2, "second FIFO item");
        Check(queue.Bytes == 0 && !queue.TryDequeue(out _), "drain releases bytes");
        queue.TryEnqueue(3, 10);
        queue.Clear();
        Check(queue.Count == 0 && queue.Bytes == 0 && queue.TryEnqueue(4, 10), "shutdown releases capacity");
        try { queue.TryEnqueue(0, -1); throw new Exception("negative size accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }

    public static void ConcurrentAdmissionNeverExceedsLimits()
    {
        var queue = new BoundedWorkQueue<int>(100, 200);
        Parallel.For(0, 10000, i => queue.TryEnqueue(i, 3));
        Check(queue.Count == 66 && queue.Bytes == 198, "atomic byte admission under contention");
        var seen = new System.Collections.Generic.HashSet<int>();
        while (queue.TryDequeue(out var item)) Check(seen.Add(item), "no duplicated packet");
        Check(seen.Count == 66 && queue.Bytes == 0, "complete concurrent accounting");
    }
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
}
