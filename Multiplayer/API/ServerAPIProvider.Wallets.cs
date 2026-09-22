using MPAPI.Interfaces;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using MPAPI.Types;
using MPAPI.Util;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Wallets;

namespace Multiplayer.API;
public partial class ServerAPIProvider
{
    private readonly IndividualWalletLedger individualWallets = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerPlayer, object> walletOwners = new();
    internal static ServerAPIProvider Current { get; private set; }
    public event Action<IndividualWalletChange> OnIndividualWalletChanged;
    public IndividualWalletResult ReadIndividualBalance(IPlayer player, Guid requestId)
    {
        if (!TryResolveAuthenticatedIdentity(player, out var id)) return new(requestId, IndividualWalletStatus.InvalidPlayer, 0);
        return individualWallets.Read(id, requestId);
    }
    public IndividualWalletResult EnsureIndividualBalance(IPlayer player, Guid requestId, double initialBalance) => ExecuteWallet(player, null, requestId, WalletOperationKind.Ensure, initialBalance);
    public IndividualWalletResult CreditIndividualBalance(IPlayer player, Guid requestId, double amount) => ExecuteWallet(player, null, requestId, WalletOperationKind.Credit, amount);
    public IndividualWalletResult DebitIndividualBalance(IPlayer player, Guid requestId, double amount) => ExecuteWallet(player, null, requestId, WalletOperationKind.Debit, amount);
    public IndividualWalletResult TransferIndividualBalance(IPlayer source, IPlayer destination, Guid requestId, double amount) => ExecuteWallet(source, destination, requestId, WalletOperationKind.Transfer, amount);

    private IndividualWalletResult ExecuteWallet(IPlayer player, IPlayer counterparty, Guid requestId, WalletOperationKind kind, double amount)
    {
        if (!TryResolveAuthenticatedIdentity(player, out var id) || kind == WalletOperationKind.Transfer && !TryResolveAuthenticatedIdentity(counterparty, out _))
            return new(requestId, IndividualWalletStatus.InvalidPlayer, 0);
        Guid other = Guid.Empty;
        if (counterparty != null) TryResolveAuthenticatedIdentity(counterparty, out other);
        return individualWallets.Execute(id, other, requestId, kind, amount, NotifyIndividualWalletChanged);
    }

    private bool TryResolveAuthenticatedIdentity(IPlayer player, out Guid identity)
    {
        identity = Guid.Empty;
        if (player is not ServerPlayerWrapper wrapper || wrapper.Peer == null || !server.TryGetServerPlayer(wrapper.Peer, out var authenticated) || !ReferenceEquals(authenticated, wrapper._serverPlayer) || authenticated.Guid == Guid.Empty) return false;
        if (server.ServerPlayers.Any(other => !ReferenceEquals(other, authenticated) && other.Guid == authenticated.Guid)) return false;
        identity = authenticated.Guid;
        return true;
    }

    internal JObject SaveIndividualWallets() => IndividualWalletStoreCodec.Write(individualWallets);


    private bool IsCurrentWalletPlayer(ServerPlayer player) => player != null && player.Peer != null &&
        server.TryGetServerPlayer(player.Peer, out var current) && ReferenceEquals(current, player);

    internal double ReadPlayerWallet(ServerPlayer player)
    {
        if (!IsCurrentWalletPlayer(player) || !TryResolveAuthenticatedIdentity(server.GetWrapper(player), out var id))
            throw new InvalidOperationException("Unknown wallet owner.");
        walletOwners.GetValue(player, _ => new object());
        return individualWallets.Read(id, Guid.NewGuid()).Balance;
    }
    internal bool DebitPlayerWallet(ServerPlayer player, double amount)
    {
        if (!IsCurrentWalletPlayer(player) || !TryResolveAuthenticatedIdentity(server.GetWrapper(player), out _)) return false;
        walletOwners.GetValue(player, _ => new object());
        return DebitIndividualBalance(server.GetWrapper(player), Guid.NewGuid(), amount).Status == IndividualWalletStatus.Success;
    }
    // Refunds can finish after disconnect; the initiating authoritative operation owns this identity.
    internal void RefundPlayerWallet(ServerPlayer player, double amount, Guid? operationId = null)
    {
        if (player == null || player.Guid == Guid.Empty || !walletOwners.TryGetValue(player, out _)) throw new InvalidOperationException("Missing refund owner or obsolete wallet session.");
        var result = individualWallets.Execute(player.Guid, Guid.Empty, operationId ?? Guid.NewGuid(), WalletOperationKind.Credit, amount, NotifyIndividualWalletChanged);
        if (result.Status != IndividualWalletStatus.Success) throw new InvalidOperationException("Individual wallet refund failed: " + result.Status);
    }
    private void NotifyIndividualWalletChanged(IndividualWalletChange change)
    {
        var player = server.ServerPlayers.FirstOrDefault(candidate => candidate.Guid == change.PlayerId);
        if (player != null) server.SendIndividualMoney(player, change.Balance);
        EventDispatch.Isolated(OnIndividualWalletChanged, change, exception => server.LogError($"Individual wallet callback failed: {exception}"));
    }
}
