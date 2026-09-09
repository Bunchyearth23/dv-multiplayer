using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Common;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        var tests = new Action[] { IndividualWalletTests.PlayersAreIsolatedAndTransfersAreAtomic,
            IndividualWalletTests.RetriesAreExactlyOnceAndImmutable,
            IndividualWalletTests.InvalidAndOverflowValuesAreRejected,
            IndividualWalletTests.SaveReloadPreservesBalancesAndReplayProtection,
            IndividualWalletTests.ConcurrentRetryMutatesOnceAndCallbacksAreIsolated,
            IndividualWalletTests.ReadsStayFreshAndDoNotConsumeReplayStorage,
            IndividualWalletTests.ConcurrentEnsureInitializesExactlyOnce,
            IndividualWalletTests.ReloadedZeroBalanceIsNotRecredited,
            IndividualWalletTests.VersionOneZeroBalanceMigratesAsInitialized,
            IndividualWalletTests.EnsureRequestIdConflictIsRejected,
            LoginRequestPolicyTests.ValidEnvelopeAndModsAreAccepted,
            LoginRequestPolicyTests.NullAndOversizedLoginFieldsAreRejected,
            ModCompatibilityTests.ExactSetsAndVersionsAreRequired,
            ModCompatibilityTests.InvalidAdvertisementsAreRejected,
            ModCompatibilityTests.AdapterFailuresAreIsolated,
            ModCompatibilityTests.ApiVersionStaysWithinMajorLine,
            TrainRecoveryTests.CompositionDetectsReplacementAndOrder,
            TrainRecoveryTests.InvalidManifestsAreRejected, TrainRecoveryTests.TravelRetriesCannotChargeTwice,
            TrainRecoveryTests.TravelRecoveryBlocksNewOperations, TrainRecoveryTests.TravelPacketsRoundTrip,
            ServerActionPolicyTests.RejectsNonFiniteAndOverflowDistances,
            ServerActionPolicyTests.HostPermissionsNeverBypassReadiness,
            ServerActionPolicyTests.SelectionRejectsFractionalAndStaleIndices,
            ServerActionPolicyTests.JunctionBranchesUseAuthoritativeCardinality,
            ServerActionPolicyTests.ParallelPayloadsAreBoundedAndAligned,
            ServerActionPolicyTests.RerailMustMatchTrackGeometry,
            ServerActionPolicyTests.RemoteFlagsCannotSmugglePhysicalActions,
            ServerActionPolicyTests.PreservesAdvancedUiAndPhysicalRelease,
            ServerActionPolicyTests.PermissionRefusalsRoundTrip, WorldItemLoadingTests.RecoveryIsBoundedAndRateLimited,
            WorldItemLoadingTests.RecoveryDoesNotResetDeadline, WorldItemLoadingTests.PacketsRoundTrip,
            WorldItemLoadingTests.RecoveryDoesNotStarveLaterItems,
            InventorySaveTests.InvalidContainerLocationsPreserveProfile, TrainCampaignTests.BurstPreservesBusinessOrder,
            TrainCampaignTests.DrainBudgetAndFailureKeepPendingOrder,
            TrainCampaignTests.IndependentHandPosesSurviveWireAndDeltaMerge,
            TrainCampaignTests.DuplicateAndReorderedTicksAreIgnored,
            TrainCampaignTests.TickWrapAndCorrectionWatermark,
            TrainCampaignTests.LongSyntheticCampaignRemainsBounded,
            TrainCampaignTests.MetricsRejectInvalidSamplesAndCountThresholds, RawRoundTrip, CompressedRoundTrip, TruncatedBatchIsAtomic,
            TrainCampaignTests.TrafficMetricsAreBoundedAndCorrelated,
            InvalidCountsRejected, CorruptCompressionRejected, DecompressionBounded,
            CompressionUsesPayloadLength, SerializationFailurePropagates, PositionOnlyRoundTrip,
            PickupRequiresReach, OwnershipCannotBeStolen, IdentityCannotBeSpoofed,
            DropRequiresOwnershipAndReach, AttachmentRequiresValidTarget, InvalidActionsRejected,
            ObjectChangesRequireAccess, AllPlayerIdsSupported,
            ShopCartTests.ServerPricesAndWallet, ShopCartTests.InvalidCartShape,
            ShopCartTests.QuantitiesAndDuplicateLines, ShopCartTests.AvailabilityAndStock,
            ShopCartTests.InvalidPricesAndOverflow, ShopCartTests.QuotesAreReadOnlyAndRevalidated,
            ShopCartTests.QuoteRequestWireRoundTrip, ShopCartTests.QuoteResponseWireRoundTrip,
            ShopPurchaseTests.SuccessfulPurchaseAndReplay, ShopPurchaseTests.OperationPayloadConflict,
            ShopPurchaseTests.PreparationFailureCompensated, ShopPurchaseTests.DebitAndCommitFailuresCompensated,
            ShopPurchaseTests.StockRecheckedAfterPreparation, ShopPurchaseTests.FailedRecoveryIsRetained,
            ShopPurchaseTests.CapacityDoesNotEvictReplayProtection, ShopPurchaseTests.ReentrantPurchasesDoNotOverlap,
            ShopPurchaseTests.PlayersHaveSeparateOperationNamespaces, ShopPurchaseTests.InvalidOperationsHaveNoSideEffects,
            ShopPurchaseTests.PurchasePacketsRoundTrip,
            LicensePurchaseTests.RequestRoundTrips, LicensePurchaseTests.ResponseRoundTrips,
            LicensePurchaseTests.InvalidResponsesAreRejected,
            IdPoolTests.DuplicateAndUnknownReleaseIgnored, IdPoolTests.ReservedIdsAreNotReissued,
            IdPoolTests.ExhaustionNeverReturnsZero, IdPoolTests.ResetClearsReservations,
            ShopPurchaseOperationTests.RetryKeepsOriginalIntent, ShopPurchaseOperationTests.ResponsesMustMatchPendingPurchase,
            ShopPurchaseOperationTests.InProgressAndRecoveryPreserveIntent,
            ShopPurchaseOperationTests.LostResponseReplaysBeforeWorldValidation,
            RpcTerminalResponseTests.PostCommitFailureStillRespondsExactlyOnce,
            RpcTerminalResponseTests.ReporterFailureStillRespondsExactlyOnce,
            RpcTerminalResponseTests.SuccessStillRespondsExactlyOnce,
            RpcTicketTests.DeadlineRejectsLateResponses, RpcTicketTests.ResolutionIsTerminalDespiteCallbackFailure,
            RpcTicketTests.CancellationRunsExactlyOnce, RpcTicketTests.TimeoutRetryDoesNotCorruptCollection,
            RpcTicketTests.CancelAllIsolatesCallbacksAndPreventsRetry, RpcTicketTests.ResolveRemovesBeforeReentrantCallback,
            LoadingTests.StalledLoadingReportsStageAndProgress, LoadingTests.ProgressExtendsIdleButNotTotalDeadline,
            LoadingTests.CancelledRoutineCannotResume, LoadingTests.RoutineFailureRunsCleanupAndReportsOnce,
            LoadingTests.ReentrantCancellationStopsAtYield, LoadingTests.ManifestRequiresAppliedObjects,
            LoadingTests.InvalidManifestCannotReplaceExpectedObjects, LoadingTests.JobManifestWireRoundTrip,
            LoadingRetryTests.RetryIsRateLimitedAndMonotone, LoadingRetryTests.InvalidIntervalsAreRejected,
            TrainLoadingTests.ManifestAndRecoveryRoundTrip, TrainLoadingTests.ConflictingManifestIsRejected,
            ItemPayloadTests.TrackedValueCountsAreBounded, ItemPayloadTests.DuplicateAndOversizedKeysRejected,
            ItemPayloadTests.InvalidValueContentsRejected, ItemPayloadTests.ReusedDeserializerClearsAbsentFields,
            ItemPayloadTests.InvalidIdentityAndFlagsRejected,
            InventorySaveTests.InventorySaveSurvivesRestartWithoutSessionIds,
            InventorySaveTests.UnknownAndEmptyInventoriesRemainDistinct, InventorySaveTests.FailedInventoryReplacementIsAtomic,
            InventorySaveTests.InventoryDuplicatesAndAliasesRejected, InventorySaveTests.InventoryStateDoesNotAliasSavedProfile,
            InventoryLocationTests.InventoryLocationSurvivesItemProtocol, InventoryLocationTests.PartialOrWronglyTypedLocationsRejected,
            InventoryLocationTests.InvalidContainerAndSlotCombinationsRejected, InventoryLocationTests.InventoryRestorePacketPreservesBindingAndState,
            LoadingTests.CoroutineDisposalFailureIsReportedOnce, LoadingTests.CoroutineAndDisposalFailuresArePreserved,
            LoadingTests.FailureNotificationRunsAfterResourceRelease,
            InventorySlotAllocatorTests.FillsHolesWithoutMovingExistingItems,
            InventorySlotAllocatorTests.FullAndZeroCapacityPreserveOverflow,
            InventorySlotAllocatorTests.ContainerContentsDoNotConsumeSlots,
            InventorySlotAllocatorTests.DuplicateSlotsFailWithoutMutatingInput,
            StartingInventoryGrantsTests.FirstJoinAndRetryKeepIdentities,
            StartingInventoryGrantsTests.DroppedGrantsAreNotRecreated,
            StartingInventoryGrantsTests.ExistingItemsAreAdoptedWithoutDuplication,
            StartingInventoryGrantsTests.NewlyUnlockedLicenseIsGrantedOnce,
            StartingInventoryGrantsTests.InvalidLedgerAndCatalogPreserveProfile,
            ScopedRoutineTests.ScopeDoesNotLeakAcrossYields,
            ScopedRoutineTests.FailureAndCancellationReleaseScope,
            ScopedRoutineTests.NestedScopesRestoreOuterContext,
            ItemQueueAndCompatibilityTests.BatchAdmissionIsAtomic,
            ItemQueueAndCompatibilityTests.QueuePreservesOrderAndCanBeCleared,
            ItemQueueAndCompatibilityTests.ProtocolHandshakeSeparatesIncompatibleBuilds };
        int failures = 0;
        var duration = System.Diagnostics.Stopwatch.StartNew();
        foreach (var test in tests)
        {
            try { test(); Console.WriteLine("PASS " + test.Method.Name); }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex); failures++; }
        }
        Console.WriteLine($"{tests.Length - failures} tests passed; {failures} failed; duration_ms={duration.ElapsedMilliseconds}");
        return failures == 0 ? 0 : 1;
    }

    private static ItemUpdateData Item(ushort id) => new ItemUpdateData
    {
        ItemNetId = id, UpdateType = ItemUpdateData.ItemUpdateType.Create,
        ItemState = ItemState.Dropped, PrefabName = "TestItem",
        ItemPosition = new Vector3(12, -3, 45), ItemRotation = new Quaternion(0, 0, 0, 1),
        States = new Dictionary<string, object> { ["on"] = true, ["count"] = 3, ["id"] = 5u,
            ["fuel"] = 2.5f, ["label"] = "test" }
    };

    private static byte[] Encode(CommonItemChangePacket packet)
    {
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        return writer.CopyData();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RoundTrip(int count)
    {
        var sent = new CommonItemChangePacket();
        for (ushort i = 1; i <= count; i++) sent.Items.Add(Item(i));
        var received = new CommonItemChangePacket();
        received.Deserialize(new NetDataReader(Encode(sent)));
        Check(received.Items.Count == count, "Wrong batch size");
        for (int i = 0; i < count; i++)
        {
            var actual = received.Items[i];
            Check(actual.ItemNetId == i + 1 && actual.PrefabName == "TestItem", "Wrong identity");
            Check(actual.ItemPosition.x == 12 && actual.ItemPosition.y == -3 && actual.ItemRotation.w == 1, "Wrong transform");
            foreach (var state in sent.Items[i].States)
                Check(Equals(actual.States[state.Key], state.Value), "Wrong tracked state");
        }
    }

    private static void RawRoundTrip() => RoundTrip(2);
    private static void CompressedRoundTrip() => RoundTrip(51);

    private static void PositionOnlyRoundTrip()
    {
        var item = Item(1); item.UpdateType = ItemUpdateData.ItemUpdateType.ItemPosition;
        var received = new CommonItemChangePacket();
        received.Deserialize(new NetDataReader(Encode(new CommonItemChangePacket
            { Items = new List<ItemUpdateData> { item } })));
        var actual = received.Items.Single();
        Check(actual.ItemPosition.x == 12 && actual.ItemPosition.y == -3 && actual.ItemPosition.z == 45 &&
            actual.ItemRotation.w == 1, "Position-only update lost its transform");
        Check(actual.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState), "Missing compatible state flag");
        Check(item.UpdateType == ItemUpdateData.ItemUpdateType.ItemPosition, "Serialization mutated source");
    }

    private static ItemUpdateData Intent(ItemState state, byte player = 7)
    {
        var request = Item(1); request.ItemState = state; request.Player = player;
        request.UpdateType = ItemUpdateData.ItemUpdateType.ItemState;
        return request;
    }

    private static bool Allowed(ItemUpdateData request, byte? owner = null, float distance = 1,
        float destination = 1, bool attachment = false, byte sender = 7) =>
        ItemActionPolicy.CanApply(request, sender, owner, distance, 4.5f, destination, attachment);

    private static void PickupRequiresReach()
    {
        var request = Intent(ItemState.InHand);
        Check(Allowed(request, distance: 4.5f), "Reach boundary rejected");
        Check(!Allowed(request, distance: 4.6f), "Remote pickup accepted");
        Check(!Allowed(request, distance: float.NaN), "NaN distance accepted");
        Check(!Allowed(request, distance: float.PositiveInfinity), "Infinite distance accepted");
        Check(Allowed(Intent(ItemState.InInventory), 7, distance: 100), "Owner cannot stow held item");
    }

    private static void OwnershipCannotBeStolen()
    {
        foreach (ItemState state in Enum.GetValues(typeof(ItemState)))
            Check(!Allowed(Intent(state), 8, attachment: true), "Another owner's item was modified");
        // Simulate two successive requests: first grant must be visible to the second validation.
        Check(Allowed(Intent(ItemState.InHand)), "Initial pickup rejected");
        Check(!Allowed(Intent(ItemState.InHand, 8), 7, sender: 8), "Second claimant accepted");
    }

    private static void IdentityCannotBeSpoofed()
    {
        Check(!Allowed(Intent(ItemState.InHand, 8)), "Spoofed hand owner accepted");
        Check(!Allowed(Intent(ItemState.InInventory, 8), 7), "Spoofed inventory accepted");
    }

    private static void DropRequiresOwnershipAndReach()
    {
        foreach (var state in new[] { ItemState.Dropped, ItemState.Thrown })
        {
            var request = Intent(state);
            Check(!Allowed(request), "Unowned drop accepted");
            Check(Allowed(request, 7), "Valid release rejected");
            Check(!Allowed(request, 7, destination: 100), "Remote drop accepted");
            request.ItemPosition = new Vector3(float.NaN, 0, 0);
            Check(!Allowed(request, 7), "NaN transform accepted");
        }
        var invalid = Intent(ItemState.Thrown);
        invalid.ThrowDirection = new Vector3(float.PositiveInfinity, 0, 0);
        Check(!Allowed(invalid, 7), "Infinite throw accepted");
        invalid = Intent(ItemState.Dropped); invalid.ItemRotation = new Quaternion(0, 0, 0, 0);
        Check(!Allowed(invalid, 7), "Zero quaternion accepted");
    }

    private static void AttachmentRequiresValidTarget()
    {
        var request = Intent(ItemState.Attached);
        Check(!Allowed(request, 7), "Missing snap point accepted");
        Check(Allowed(request, 7, attachment: true), "Valid attachment rejected");
        Check(!Allowed(request, 7, destination: 100, attachment: true), "Remote attachment accepted");
    }

    private static void InvalidActionsRejected()
    {
        Check(!Allowed(null), "Null intent accepted");
        foreach (var flags in new[] { ItemUpdateData.ItemUpdateType.Create, ItemUpdateData.ItemUpdateType.Destroy,
            ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.ItemState,
            ItemUpdateData.ItemUpdateType.None, (ItemUpdateData.ItemUpdateType)128 })
        {
            var request = Intent(ItemState.InHand); request.UpdateType = flags;
            Check(!Allowed(request), "Invalid intent flags accepted");
        }
        var invalid = Intent((ItemState)255);
        Check(!Allowed(invalid), "Unknown state accepted");
        invalid = Intent(ItemState.InHand); invalid.ItemNetId = 0;
        Check(!Allowed(invalid), "Missing identity accepted");
    }

    private static void ObjectChangesRequireAccess()
    {
        var request = Intent(ItemState.Dropped); request.UpdateType = ItemUpdateData.ItemUpdateType.ObjectState;
        Check(Allowed(request), "Nearby world interaction rejected");
        Check(!Allowed(request, distance: 100), "Remote world interaction accepted");
        Check(Allowed(request, 7, distance: 100), "Held object change rejected");
        Check(!Allowed(request, 8), "Another owner's object changed");
    }

    private static void AllPlayerIdsSupported()
    {
        foreach (byte id in new byte[] { 0, 127, 128, 255 })
            Check(Allowed(Intent(ItemState.InInventory, id), id, sender: id), "Valid byte player ID rejected");
    }

    private static void TruncatedBatchIsAtomic()
    {
        var sent = new CommonItemChangePacket { Items = new List<ItemUpdateData> { Item(1), Item(2) } };
        var bytes = Encode(sent);
        var received = new CommonItemChangePacket();
        received.Deserialize(new NetDataReader(bytes.Take(bytes.Length - 1).ToArray()));
        Check(received.Items.Count == 0, "Partial batch escaped");
        received.Deserialize(new NetDataReader(bytes));
        Check(received.Items.Count == 2, "Decoder failed to recover");
    }

    private static void InvalidCountsRejected()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (int count in new[] { -1, int.MaxValue })
        {
            var writer = new NetDataWriter(); writer.Put(compressed); writer.Put(count);
            var packet = new CommonItemChangePacket(); packet.Items.Add(Item(1));
            packet.Deserialize(new NetDataReader(writer.CopyData()));
            Check(packet.Items.Count == 0, "Invalid count retained data");
        }
    }

    private static void CorruptCompressionRejected()
    {
        var writer = new NetDataWriter(); writer.Put(true); writer.Put(51);
        writer.PutBytesWithLength(new byte[] { 1, 2, 3, 4 });
        var packet = new CommonItemChangePacket(); packet.Items.Add(Item(1));
        packet.Deserialize(new NetDataReader(writer.CopyData()));
        Check(packet.Items.Count == 0, "Corrupt data retained items");
    }

    private static void DecompressionBounded()
    {
        var compressed = PacketCompression.Compress(new byte[8193]);
        Check(PacketCompression.Decompress(compressed, 8193).Length == 8193, "Exact limit rejected");
        try { PacketCompression.Decompress(compressed, 8192); }
        catch (InvalidDataException) { return; }
        throw new Exception("Oversized decompression accepted");
    }

    private static void CompressionUsesPayloadLength()
    {
        var packet = new CommonItemChangePacket(); var raw = new NetDataWriter();
        for (ushort i = 1; i <= 51; i++) { var item = Item(i); packet.Items.Add(item); item.Serialize(raw); }
        var reader = new NetDataReader(Encode(packet));
        Check(reader.GetBool() && reader.GetInt() == 51, "Wrong compressed header");
        var bytes = PacketCompression.Decompress(reader.GetBytesWithLength());
        Check(bytes.SequenceEqual(raw.CopyData()), "Writer capacity leaked into payload");
    }

    private static void SerializationFailurePropagates()
    {
        var item = Item(1); item.States["bad"] = new object();
        try { Encode(new CommonItemChangePacket { Items = new List<ItemUpdateData> { item } }); }
        catch (NotSupportedException) { return; }
        throw new Exception("Invalid serialization was silently accepted");
    }
}

// Only the Unity mod logger is replaced; serialization and compression are linked production sources.
namespace Multiplayer
{
    internal static class Multiplayer
    {
        public static void LogError(object message) { }
        public static void LogDebug(Func<object> message) { }
    }
}


