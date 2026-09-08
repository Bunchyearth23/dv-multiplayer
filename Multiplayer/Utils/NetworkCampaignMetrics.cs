using System;
using System.Globalization;

namespace Multiplayer.Utils;

// Main-thread counters. One instance per watcher/session; no per-player IDs retained.
public sealed class NetworkCampaignMetrics
{
    public long Frames { get; private set; }
    public double FrameSeconds { get; private set; }
    public double MaxFrameSeconds { get; private set; }
    public long PositionChecks { get; private set; }
    public long CorrectionRequests { get; private set; }
    public double MaxPositionDelta { get; private set; }
    public long UnknownTrainsets { get; set; }
    public long CompositionMismatches { get; set; }

    public void RecordFrame(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return;
        Frames++; FrameSeconds += seconds; MaxFrameSeconds = Math.Max(MaxFrameSeconds, seconds);
    }
    public void RecordPosition(double delta, double threshold)
    {
        if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0) return;
        PositionChecks++; MaxPositionDelta = Math.Max(MaxPositionDelta, delta);
        if (delta > threshold) CorrectionRequests++;
    }
    public string Format(long managedBytes) => string.Format(CultureInfo.InvariantCulture,
        "network_campaign frames={0} mean_frame_ms={1:F3} max_frame_ms={2:F3} position_checks={3} correction_requests={4} max_position_delta_m={5:F3} unknown_trainsets={6} composition_mismatches={7} managed_bytes={8}",
        Frames, Frames == 0 ? 0 : FrameSeconds * 1000 / Frames, MaxFrameSeconds * 1000,
        PositionChecks, CorrectionRequests, MaxPositionDelta, UnknownTrainsets, CompositionMismatches, managedBytes);
}
