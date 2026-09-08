using System;
using System.Collections.Generic;

namespace Multiplayer.Utils;

public class IdPool<T> where T : struct
{
    private T currentId;
    private readonly Queue<T> releasedIds;
    private readonly HashSet<T> allocatedIds = new();

    public IdPool()
    {
        currentId = default;
        releasedIds = new Queue<T>();
    }

    public T NextId {
        get {
            while (releasedIds.Count > 0)
            {
                T released = releasedIds.Dequeue();
                if (allocatedIds.Add(released)) return released;
            }
            do
            {
                dynamic incrementedId = currentId;
                incrementedId++;
                if (incrementedId.CompareTo(default(T)) == 0)
                    throw new OverflowException("IdPool has reached the maximum possible id.");
                currentId = incrementedId;
            } while (!allocatedIds.Add(currentId));
            return currentId;
        }
    }

    public void ReleaseId(T id)
    {
        if (!EqualityComparer<T>.Default.Equals(id, default) && allocatedIds.Remove(id))
            releasedIds.Enqueue(id);
    }

    public void ReserveId(T id)
    {
        if (!EqualityComparer<T>.Default.Equals(id, default)) allocatedIds.Add(id);
    }

    public void Reset()
    {
        currentId = default;
        releasedIds.Clear();
        allocatedIds.Clear();
    }
}
