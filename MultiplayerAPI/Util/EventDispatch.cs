using System;
using System.Diagnostics;

namespace MPAPI.Util;

/// <summary>Dispatches adapter callbacks independently so one failing mod cannot stop the others.</summary>
public static class EventDispatch
{
    public static void Isolated(Action handlers, Action<Exception> onError = null)
    {
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception)
            {
                try
                {
                    if (onError != null) onError(exception);
                    else Trace.TraceError("MultiplayerAPI adapter callback failed: " + exception);
                }
                catch { Trace.TraceError("MultiplayerAPI adapter callback and error reporter failed: " + exception); }
            }
        }
    }

    public static void Isolated<T>(Action<T> handlers, T value, Action<Exception> onError = null)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception exception)
            {
                try
                {
                    if (onError != null) onError(exception);
                    else Trace.TraceError("MultiplayerAPI adapter callback failed: " + exception);
                }
                catch { Trace.TraceError("MultiplayerAPI adapter callback and error reporter failed: " + exception); }
            }
        }
    }
}
