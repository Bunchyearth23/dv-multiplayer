using System;
using MPAPI.Interfaces;

namespace MPAPI;

/// <summary>Host-side, synchronous veto for physical use of a persistent car. Never authorizes an action by itself.</summary>
public static class RollingStockAccess
{
    private static Func<IPlayer, string, bool> validator;
    /// <summary>Installs the authority provider; null removes it. Call on the Unity owner thread.</summary>
    public static void SetValidator(Func<IPlayer, string, bool> value) => validator = value;
    /// <summary>Whether a provider requires authorization.</summary>
    public static bool IsRestricted => validator != null;
    /// <summary>Checks the current actor and car; provider failures deny access.</summary>
    public static bool Allows(IPlayer player, string carGuid)
    {
        var current = validator;
        if (current == null) return true;
        if (player == null || string.IsNullOrEmpty(carGuid)) return false;
        try { return current(player, carGuid); }
        catch { return false; }
    }
    // The native loader may be called from a remote player's request. Preserve that actor
    // across synchronous native prefixes; never use the host identity on their behalf.
    [ThreadStatic] private static IPlayer actor;
    /// <summary>Actor of the current synchronous native operation, or null for local input.</summary>
    public static IPlayer CurrentActor => actor;
    /// <summary>Scopes a native operation to its authenticated actor. Dispose on the same thread.</summary>
    public static IDisposable ForActor(IPlayer player) => new Scope(player);
    private sealed class Scope : IDisposable
    {
        private readonly IPlayer previous = actor;
        private bool disposed;
        public Scope(IPlayer player) { actor = player; }
        public void Dispose() { if (!disposed) { actor = previous; disposed = true; } }
    }
}
