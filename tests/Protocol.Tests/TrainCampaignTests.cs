using System;
using Multiplayer.Utils;

internal static class TrainCampaignTests
{
    public static void IndependentHandPosesSurviveWireAndDeltaMerge()
    {
        var pose = new Multiplayer.Networking.Data.Player.PlayerTrackingData
        {
            LeftHandPosition = new UnityEngine.Vector3(-1, 2, 3),
            RightHandPosition = new UnityEngine.Vector3(4, 5, 6),
            LeftHandRotation = new UnityEngine.Quaternion(0, 0, 0, 1),
            RightHandRotation = new UnityEngine.Quaternion(0, 1, 0, 0)
        };
        var writer = new LiteNetLib.Utils.NetDataWriter();
        Multiplayer.Networking.Data.Player.PlayerTrackingData.Serialize(writer, pose);
        var restored = Multiplayer.Networking.Data.Player.PlayerTrackingData.Deserialize(
            new LiteNetLib.Utils.NetDataReader(writer.CopyData()));
        Check(restored.LeftHandPosition.Value.x == -1 && restored.RightHandPosition.Value.x == 4);
        Check(restored.LeftHandRotation.Value.w == 1 && restored.RightHandRotation.Value.y == 1);
        var merged = restored.MergeFrom(new Multiplayer.Networking.Data.Player.PlayerTrackingData
            { LeftHandPosition = new UnityEngine.Vector3(-2, 2, 3) });
        Check(merged.LeftHandPosition.Value.x == -2 && merged.RightHandPosition.Value.x == 4);
        Check(merged.RightHandRotation.Value.y == 1 && merged.HasAdditionalData);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Train campaign invariant failed"); }
    public static void BurstPreservesBusinessOrder()
    {
        var queue = new TickSnapshotBuffer<string>(256);
        for (uint i = 0; i < 256; i++) Check(queue.TryEnqueue("track/derail/pose " + i, i));
        Check(queue.Count == 256 && queue.HighWater == 256);
        Check(!queue.TryEnqueue("overflow", 256));
        for (uint i = 0; i < 256; i++)
        {
            Check(queue.TryDequeue(out var value, out var tick));
            Check(tick == i && value == "track/derail/pose " + i);
        }
        Check(queue.TryEnqueue("retry", 256)); // rejection must not advance the watermark
        Check(queue.Overflows == 1);
    }
    public static void DuplicateAndReorderedTicksAreIgnored()
    {
        var queue = new TickSnapshotBuffer<int>(2);
        Check(queue.TryEnqueue(10, 10));
        Check(queue.TryEnqueue(9, 9) && queue.TryEnqueue(99, 10));
        Check(queue.Count == 1 && queue.Stale == 2);
        Check(queue.TryDequeue(out var value, out var tick) && value == 10 && tick == 10);
    }
    public static void TickWrapAndCorrectionWatermark()
    {
        var queue = new TickSnapshotBuffer<int>(3);
        Check(queue.TryEnqueue(1, uint.MaxValue));
        Check(queue.TryEnqueue(2, 0));
        queue.Clear();
        Check(queue.TryEnqueue(3, uint.MaxValue) && queue.Count == 0);
        Check(queue.TryEnqueue(4, 1) && queue.Count == 1);
        queue.Reset();
        Check(queue.TryEnqueue(5, 0) && queue.Stale == 0 && queue.HighWater == 1);
    }
    public static void LongSyntheticCampaignRemainsBounded()
    {
        var queue = new TickSnapshotBuffer<uint>(256);
        uint expected = 0;
        for (uint tick = 0; tick < 100000; tick++)
        {
            Check(queue.TryEnqueue(tick, tick));
            if (tick % 17 != 16) continue;
            while (queue.TryDequeue(out var value, out var received))
                Check(value == expected && received == expected++);
        }
        while (queue.TryDequeue(out var value, out var received)) Check(value == expected && received == expected++);
        Check(expected == 100000 && queue.HighWater == 17 && queue.Overflows == 0);
    }
    public static void MetricsRejectInvalidSamplesAndCountThresholds()
    {
        var metrics = new NetworkCampaignMetrics();
        metrics.RecordFrame(double.NaN); metrics.RecordFrame(-1); metrics.RecordFrame(double.PositiveInfinity);
        metrics.RecordFrame(.01); metrics.RecordFrame(.03);
        metrics.RecordPosition(2, 2); metrics.RecordPosition(3, 2); metrics.RecordPosition(double.NaN, 2);
        Check(metrics.Frames == 2 && Math.Abs(metrics.FrameSeconds - .04) < .00001);
        Check(metrics.MaxFrameSeconds == .03 && metrics.PositionChecks == 2 && metrics.CorrectionRequests == 1);
        Check(metrics.Format(1024).Contains("mean_frame_ms=20.000") && metrics.Format(1024).Contains("managed_bytes=1024"));
    }
    public static void DrainBudgetAndFailureKeepPendingOrder()
    {
        var queue = new TickSnapshotBuffer<int>(64);
        for (uint i = 0; i < 64; i++) queue.TryEnqueue((int)i, i);
        int expected = 0;
        Check(queue.Drain(32, (value, tick) => Check(value == expected++)) == 32);
        Check(queue.Count == 32);
        try { queue.Drain(32, (value, tick) => throw new InvalidOperationException()); }
        catch (InvalidOperationException) { }
        Check(queue.Count == 31);
        Check(queue.TryDequeue(out var next, out var nextTick) && next == 33 && nextTick == 33);
        Check(queue.Drain(0, (value, tick) => throw new Exception()) == 0);
    }
}
