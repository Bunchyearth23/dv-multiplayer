# Persistent individual wallet capability

API version: `1.3.0.0`.

This is an optional, host-only capability. It does not replace or mirror Derail Valley's shared `Inventory.PlayerMoney`. Mods that do not cast the server provider to this interface keep the existing shared-wallet behavior.

```csharp
if (MultiplayerAPI.Server is IPersistentPlayerWallets wallets)
{
    Guid requestId = Guid.NewGuid(); // keep this exact value for every retry
    IndividualWalletResult result = wallets.DebitIndividualBalance(player, requestId, 250d);
}
```

## Exact contract

`IPersistentPlayerWallets` exposes:

- `ReadIndividualBalance(IPlayer player, Guid requestId)`
- `CreditIndividualBalance(IPlayer player, Guid requestId, double amount)`
- `DebitIndividualBalance(IPlayer player, Guid requestId, double amount)`
- `TransferIndividualBalance(IPlayer source, IPlayer destination, Guid requestId, double amount)`
- `OnIndividualWalletChanged`

Use only `IPlayer` instances supplied by the active server provider, such as packet-handler senders or entries from `IServer.Players`. The implementation resolves those wrappers back through their authenticated transport peer and uses the server-held persistent GUID. Fabricated, stale, disconnected, client-side, or foreign wrappers return `InvalidPlayer`. No username, session `PlayerId`, or client-supplied GUID authorizes a wallet operation.

`requestId` must be a non-empty GUID and identifies one immutable operation globally. A retry with the same operation and payload returns the stored terminal result with `IsReplay == true` and performs no mutation. Reusing it with a different operation, player, counterparty, or amount returns `OperationConflict`. Persist the request ID in the calling mod until a terminal result is recorded.

Amounts must be finite, greater than zero, and at most `1,000,000,000`. A balance cannot exceed `1,000,000,000,000` or fall below zero. Transfers update both wallets atomically. The durable ledger retains at most 16,384 operations; when full it returns `StorageLimitReached` and does not evict replay protection.

Balances and terminal operation results are stored under `Multiplayer.IndividualWallets` in the host save. They survive save/reload, host restart, reconnect, username changes, and session `PlayerId` reuse. Invalid persisted data is rejected atomically rather than partially applied.

Change callbacks run after the commit and are isolated per subscriber. A callback exception cannot roll back, duplicate, or hide the result of a completed operation. Read operations and rejected mutations do not raise the event; a transfer raises one change for each affected persistent identity.
