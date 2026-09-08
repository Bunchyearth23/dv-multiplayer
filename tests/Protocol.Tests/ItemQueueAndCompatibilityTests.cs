using System;
using Multiplayer.Networking.Data;
using Multiplayer.Utils;

internal static class ItemQueueAndCompatibilityTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Queue or compatibility invariant failed."); }

    public static void BatchAdmissionIsAtomic()
    {
        var queue = new BoundedBatchQueue<int>(3);
        Check(queue.TryEnqueue(new[] { 1, 2 }));
        Check(!queue.TryEnqueue(new[] { 3, 4 }) && queue.Count == 2);
        Check(queue.TryDequeue(out var first) && first == 1);
        Check(queue.TryDequeue(out var second) && second == 2);
        Check(!queue.TryDequeue(out _));
    }

    public static void QueuePreservesOrderAndCanBeCleared()
    {
        var queue = new BoundedBatchQueue<string>(3);
        Check(queue.TryEnqueue(new[] { "a" }) && queue.TryEnqueue(new[] { "b", "c" }));
        Check(queue.TryDequeue(out var first) && first == "a");
        queue.Clear();
        Check(queue.Count == 0 && queue.TryEnqueue(Array.Empty<string>()));
    }

    public static void ProtocolHandshakeSeparatesIncompatibleBuilds()
    {
        string build = ProtocolCompatibility.HandshakeBuild("2026.1");
        Check(build == "2026.1|dvmp-protocol:3" && build != "2026.1|dvmp-protocol:2");
        Check(build != "2026.1" && build != "2026.1|dvmp-protocol:1");
        try { ProtocolCompatibility.HandshakeBuild(""); }
        catch (ArgumentException) { return; }
        throw new Exception("Empty game build accepted.");
    }
}
