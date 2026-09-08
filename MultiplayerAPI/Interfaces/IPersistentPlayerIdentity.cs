using System;

namespace MPAPI.Interfaces
{
    /// <summary>
    /// Optional server-side capability exposing the stable identity authenticated by Multiplayer.
    /// </summary>
    /// <remarks>
    /// This identity survives reconnects and must be used instead of the reusable session byte
    /// <see cref="IPlayer.PlayerId"/> for persistent mod-owned state. Client wrappers do not
    /// implement this capability because clients are not an authority for player identity.
    /// </remarks>
    public interface IPersistentPlayerIdentity
    {
        /// <summary>Gets the persistent player GUID authenticated and stored by the server.</summary>
        Guid PersistentId { get; }
    }
}
