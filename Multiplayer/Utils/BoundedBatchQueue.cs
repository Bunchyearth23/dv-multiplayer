using System;
using System.Collections.Generic;

namespace Multiplayer.Utils;

public sealed class BoundedBatchQueue<T>
{
    private readonly Queue<T> queue;
    public int Capacity { get; }
    public int Count => queue.Count;

    public BoundedBatchQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        queue = new Queue<T>(capacity);
    }

    public bool TryEnqueue(IReadOnlyList<T> batch)
    {
        if (batch == null || batch.Count > Capacity - queue.Count) return false;
        for (int i = 0; i < batch.Count; i++) queue.Enqueue(batch[i]);
        return true;
    }

    public bool TryDequeue(out T value)
    {
        if (queue.Count == 0) { value = default; return false; }
        value = queue.Dequeue();
        return true;
    }

    public void Clear() => queue.Clear();
}
