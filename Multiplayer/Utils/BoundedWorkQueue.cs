using System;
using System.Collections.Generic;

namespace Multiplayer.Utils;

/// <summary>FIFO admission bounded by both work count and owned payload bytes.</summary>
public sealed class BoundedWorkQueue<T>
{
    private readonly object gate = new();
    private readonly Queue<Entry> entries = new();
    private readonly int maximumCount;
    private readonly long maximumBytes;
    private long bytes;
    private readonly struct Entry
    {
        public readonly T Value;
        public readonly int Bytes;
        public Entry(T value, int size) { Value = value; Bytes = size; }
    }

    public BoundedWorkQueue(int maximumCount, long maximumBytes)
    {
        if (maximumCount <= 0 || maximumBytes <= 0) throw new ArgumentOutOfRangeException();
        this.maximumCount = maximumCount;
        this.maximumBytes = maximumBytes;
    }
    public int Count { get { lock (gate) return entries.Count; } }
    public long Bytes { get { lock (gate) return bytes; } }
    public bool TryEnqueue(T value, int size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        lock (gate)
        {
            if (entries.Count >= maximumCount || size > maximumBytes - bytes) return false;
            entries.Enqueue(new Entry(value, size));
            bytes += size;
            return true;
        }
    }
    public bool TryDequeue(out T value)
    {
        lock (gate)
        {
            if (entries.Count == 0) { value = default!; return false; }
            var entry = entries.Dequeue();
            bytes -= entry.Bytes;
            value = entry.Value;
            return true;
        }
    }
    public void Clear() { lock (gate) { entries.Clear(); bytes = 0; } }
}
