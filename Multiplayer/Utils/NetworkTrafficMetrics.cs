using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Multiplayer.Utils;

public sealed class NetworkTrafficMetrics
{
    private const int MaxSeries = 512;
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    public long DroppedSeries { get; private set; }

    public void Record(string direction, int peerId, string packetType, int bytes)
    {
        if (bytes < 0 || string.IsNullOrWhiteSpace(direction) || string.IsNullOrWhiteSpace(packetType)) return;
        string key = direction + ":" + peerId.ToString(CultureInfo.InvariantCulture) + ":" + packetType;
        if (!counters.TryGetValue(key, out var counter))
        {
            if (counters.Count >= MaxSeries) { DroppedSeries++; return; }
            counter = new Counter(); counters.Add(key, counter);
        }
        counter.Packets++; counter.Bytes += bytes;
    }

    public string Format() => "network_traffic " + string.Join(" ", counters.OrderBy(pair => pair.Key)
        .Select(pair => pair.Key + "_packets=" + pair.Value.Packets.ToString(CultureInfo.InvariantCulture) +
            " " + pair.Key + "_bytes=" + pair.Value.Bytes.ToString(CultureInfo.InvariantCulture))) +
        " dropped_series=" + DroppedSeries.ToString(CultureInfo.InvariantCulture);

    private sealed class Counter { public long Packets; public long Bytes; }
}
