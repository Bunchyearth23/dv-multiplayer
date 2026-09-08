using System;
using System.Collections;

namespace Multiplayer.Utils;

/// <summary>Activates a synchronous context only while an iterator executes, never across a yield.</summary>
public sealed class ScopedRoutine : IEnumerator, IDisposable
{
    private IEnumerator inner;
    private readonly Func<IDisposable> enter;

    public ScopedRoutine(IEnumerator inner, Func<IDisposable> enter)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.enter = enter ?? throw new ArgumentNullException(nameof(enter));
    }

    public object Current => inner?.Current;

    public bool MoveNext()
    {
        if (inner == null) return false;
        using (enter()) return inner.MoveNext();
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        var owned = inner;
        inner = null;
        if (owned is IDisposable disposable)
            using (enter()) disposable.Dispose();
    }
}
