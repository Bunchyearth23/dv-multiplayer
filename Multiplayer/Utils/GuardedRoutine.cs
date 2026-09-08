using System;
using System.Collections;

namespace Multiplayer.Utils;

/// <summary>Owns a coroutine so session shutdown can cancel it even while a callback is executing.</summary>
public sealed class GuardedRoutine : IEnumerator, IDisposable
{
    private readonly IEnumerator inner;
    private readonly Action<Exception> onFailure;
    private bool finished, advancing, disposed;
    private Exception failure;
    private bool failureReported;
    public object Current { get; private set; }

    public GuardedRoutine(IEnumerator inner, Action<Exception> onFailure)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
    }

    public bool MoveNext()
    {
        if (finished) return false;
        advancing = true;
        try
        {
            if (!inner.MoveNext() || finished)
            {
                finished = true;
                return false;
            }
            Current = inner.Current;
            return true;
        }
        catch (Exception ex)
        {
            finished = true;
            failure = ex;
            return false;
        }
        finally
        {
            advancing = false;
            if (finished) Finish();
        }
    }

    public void Dispose()
    {
        finished = true;
        if (!advancing) Finish();
    }

    private void Finish()
    {
        if (!disposed)
        {
            disposed = true;
            Current = null;
            try { (inner as IDisposable)?.Dispose(); }
            catch (Exception ex)
            {
                failure = failure == null ? ex : new AggregateException("Coroutine and cleanup failed.", failure, ex);
            }
        }
        // Release owned resources before notifying a caller that may start a replacement.
        if (failure != null && !failureReported)
        {
            failureReported = true;
            onFailure(failure);
        }
    }

    public void Reset() => throw new NotSupportedException();
}
