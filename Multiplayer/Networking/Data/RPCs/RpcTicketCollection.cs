using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Networking.Data.RPCs;

/// <summary>Owns pending calls and isolates callbacks, including synchronous retries.</summary>
public sealed class RpcTicketCollection
{
    private readonly Dictionary<uint, RpcTicket> tickets = new();
    private readonly Action<Exception> reportError;
    private readonly Func<double> clock;
    private uint nextId;
    private bool cancelling;
    public int Count => tickets.Count;

    public RpcTicketCollection(Action<Exception> reportError, Func<double> clock = null)
    {
        this.reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        this.clock = clock;
    }

    public RpcTicket Create(float timeout)
    {
        if (cancelling) throw new InvalidOperationException("RPC session is being cancelled.");
        do { nextId = unchecked(nextId + 1); } while (nextId == 0 || tickets.ContainsKey(nextId));
        var ticket = clock == null ? new RpcTicket(nextId, timeout) : new RpcTicket(nextId, timeout, clock);
        tickets.Add(nextId, ticket);
        return ticket;
    }

    public bool Resolve(uint id, IRpcResponse response)
    {
        if (response == null || !tickets.TryGetValue(id, out var ticket)) return false;
        tickets.Remove(id);
        Invoke(() => ticket.Resolve(response));
        return true;
    }

    public void Poll()
    {
        foreach (var ticket in tickets.Values.ToArray())
        {
            Invoke(ticket.CheckExpiry);
            if (ticket.IsFinished) tickets.Remove(ticket.TicketId);
        }
    }

    public void CancelAll()
    {
        if (cancelling) return;
        cancelling = true;
        var pending = tickets.Values.ToArray();
        tickets.Clear();
        try
        {
            foreach (var ticket in pending) Invoke(ticket.Cancel);
        }
        finally { cancelling = false; }
    }

    private void Invoke(Action callback)
    {
        try { callback(); }
        catch (Exception ex) { reportError(ex); }
    }
}
