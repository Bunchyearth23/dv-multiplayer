using System;

namespace MPAPI.Interfaces
{
    /// <summary>
    /// Optional client-side capability exposing the local persistent identity sent during the
    /// authenticated Multiplayer login handshake.
    /// </summary>
    /// <remarks>
    /// This value is a claim until the server compares it with the authenticated peer identity.
    /// Mods must never use it to authorize a client-side mutation.
    /// </remarks>
    public interface IPersistentLocalPlayerIdentity
    {
        /// <summary>Gets the local persistent GUID used by the Multiplayer login handshake.</summary>
        Guid PersistentId { get; }
    }
}
