# Derail Valley Multiplayer Mod API
API for interfacing with DV Multiplayer mod. Provides events and interfaces for server/client interactions in Derail Valley multiplayer scenarios.

## Persistent player identities

Server-side `IPlayer` wrappers implement the optional `IPersistentPlayerIdentity` capability. The local client provider implements `IPersistentLocalPlayerIdentity`, exposing the same GUID used by the authenticated login handshake. A client-side GUID is only a claim: integrations must send it to the server and authorize mutations exclusively after the server compares it with the authenticated peer identity.

## Persistent individual wallets

API 1.3 adds the optional host-only `IPersistentPlayerWallets` capability. Cast `MultiplayerAPI.Server` to this interface to read, credit, debit, or atomically transfer a mod-owned individual balance. Every call requires an immutable non-empty `Guid requestId`; retries return the persisted terminal result exactly once. Players are authorized from server-issued `IPlayer` wrappers and their authenticated peer, never from a client-provided username, session ID, or GUID. This capability is independent of the vanilla shared wallet. See `docs/INDIVIDUAL-WALLETS.md` for limits and the exact contract.

This package is licenced under Apache 2.0, please see the [repository](https://github.com/AMacro/dv-multiplayer) for the full licence and source code.

For full documentation and examples, please see the [wiki](https://github.com/AMacro/dv-multiplayer/wiki/API-Overview).

All issues should be reported in the repository's [issue tracker](https://github.com/AMacro/dv-multiplayer/issues).

General support can be found in the [Multiplayer mod.](https://discord.com/channels/332511223536943105/1234574186161377363) thread on the [Altfuture Discord](https://discord.gg/7QKaeuHkKC) server.
