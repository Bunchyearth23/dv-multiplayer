using System;
using System.Diagnostics;

namespace Multiplayer.Networking.Data.RPCs;

public class RpcTicket
{
    public uint TicketId { get; }
    public bool IsResolved { get; private set; }
    public bool IsExpired { get; private set; }
    public bool IsCancelled { get; private set; }
    public bool IsFinished => IsResolved || IsExpired || IsCancelled;
    
    private Action<IRpcResponse> onResolve;
    private Action onTimeout;
    private Action onCancel;
    private readonly double expiryTime;
    private readonly Func<double> clock;

    public RpcTicket(uint ticketId, float timeOut)
        : this(ticketId, timeOut, () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }

    public RpcTicket(uint ticketId, float timeOut, Func<double> clock)
    {
        if (ticketId == 0) throw new ArgumentOutOfRangeException(nameof(ticketId));
        if (float.IsNaN(timeOut) || float.IsInfinity(timeOut) || timeOut < 0)
            throw new ArgumentOutOfRangeException(nameof(timeOut));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        TicketId = ticketId;
        expiryTime = clock() + timeOut;
    }

    public RpcTicket OnResolve(Action<IRpcResponse> callback)
    {
        onResolve = callback;
        return this;
    }

    public RpcTicket OnTimeout(Action callback)
    {
        onTimeout = callback;
        return this;
    }

    public void Resolve(IRpcResponse response)
    {
        if (IsFinished) return;
        if (response == null) throw new ArgumentNullException(nameof(response));
        CheckExpiry();
        if (IsFinished) return;
        
        IsResolved = true;
        onResolve?.Invoke(response);
    }

    public RpcTicket OnCancelled(Action callback)
    {
        onCancel = callback;
        return this;
    }

    public void Cancel()
    {
        if (IsFinished) return;
        IsCancelled = true;
        // Existing callers use their timeout path to release pending UI state.
        (onCancel ?? onTimeout)?.Invoke();
    }

    public void CheckExpiry()
    {
        if (IsFinished) return;
        
        if (clock() >= expiryTime)
        {
            IsExpired = true;
            onTimeout?.Invoke();
        }
    }
}
