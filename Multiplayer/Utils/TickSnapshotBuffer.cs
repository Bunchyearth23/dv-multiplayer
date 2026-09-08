using System;
using System.Collections.Generic;

namespace Multiplayer.Utils;

// FIFO: bogie snapshots can carry track changes and derailment, not just replaceable poses.
public sealed class TickSnapshotBuffer<T>
{
    private readonly Queue<(uint Tick, T Value)> queue = new();
    private readonly int capacity;
    private bool hasTick;
    private uint latestTick;
    public int Count => queue.Count;
    public int HighWater { get; private set; }
    public long Accepted { get; private set; }
    public long Stale { get; private set; }
    public long Overflows { get; private set; }

    public TickSnapshotBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public bool TryEnqueue(T value, uint tick)
    {
        // Serial arithmetic also accepts uint wrap. Half-range jumps are ambiguous and rejected.
        if (hasTick && unchecked((int)(tick - latestTick)) <= 0) { Stale++; return true; }
        if (Count == capacity) { Overflows++; return false; }
        queue.Enqueue((tick, value));
        latestTick = tick;
        hasTick = true;
        Accepted++;
        HighWater = Math.Max(HighWater, Count);
        return true;
    }

    public bool TryDequeue(out T value, out uint tick)
    {
        if (Count == 0) { value = default; tick = 0; return false; }
        var entry = queue.Dequeue();
        value = entry.Value; tick = entry.Tick;
        return true;
    }

    public int Drain(int budget, Action<T, uint> apply)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        if (apply == null) throw new ArgumentNullException(nameof(apply));
        int count = 0;
        while (count < budget && TryDequeue(out var value, out var tick))
        {
            apply(value, tick);
            count++;
        }
        return count;
    }

    // Corrections clear pending work but must not make old network packets admissible again.
    public void Clear() => queue.Clear();
    public void Reset()
    {
        Clear(); hasTick = false; latestTick = 0;
        HighWater = 0; Accepted = Stale = Overflows = 0;
    }
}
