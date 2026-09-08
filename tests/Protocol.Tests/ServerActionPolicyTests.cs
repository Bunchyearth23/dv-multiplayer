using System;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Data.Items;

internal static class ServerActionPolicyTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Policy regression"); }
    public static void RejectsNonFiniteAndOverflowDistances()
    {
        foreach (float v in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, float.MaxValue })
            Check(!ServerActionPolicy.InRange(v, 10));
        Check(ServerActionPolicy.InRange(100, 10));
        Check(!ServerActionPolicy.InRange(100.01f, 10));
        Check(!ServerActionPolicy.InRange(0, float.NaN));
    }
    public static void HostPermissionsNeverBypassReadiness()
    {
        Check(!ServerActionPolicy.Allowed(false, true, true));
        Check(ServerActionPolicy.Allowed(true, true, false));
        Check(!ServerActionPolicy.Allowed(true, false, false));
        Check(ServerActionPolicy.Allowed(true, false, true));
    }
    public static void SelectionRejectsFractionalAndStaleIndices()
    {
        foreach (float value in new[] { -1f, 0.5f, 3f, float.NaN, float.PositiveInfinity })
            Check(!ServerActionPolicy.Index(value, 3));
        Check(ServerActionPolicy.Index(2, 3));
        Check(!ServerActionPolicy.Index(0, 0));
    }
    public static void JunctionBranchesUseAuthoritativeCardinality()
    {
        Check(!ServerActionPolicy.JunctionBranch(0, 0));
        Check(!ServerActionPolicy.JunctionBranch(-1, 3));
        Check(ServerActionPolicy.JunctionBranch(0, 1));
        Check(ServerActionPolicy.JunctionBranch(2, 3));
        Check(!ServerActionPolicy.JunctionBranch(3, 3));
    }
    public static void ParallelPayloadsAreBoundedAndAligned()
    {
        Check(ServerActionPolicy.ParallelPayload(1, 1));
        Check(ServerActionPolicy.ParallelPayload(256, 256));
        Check(!ServerActionPolicy.ParallelPayload(0, 0));
        Check(!ServerActionPolicy.ParallelPayload(2, 1));
        Check(!ServerActionPolicy.ParallelPayload(257, 257));
        Check(!ServerActionPolicy.ParallelPayload(1, 1, 0));
    }
    public static void RerailMustMatchTrackGeometry()
    {
        Check(ServerActionPolicy.TrackAlignment(0f, 1f));
        Check(ServerActionPolicy.TrackAlignment(16f, 0.5f));
        Check(!ServerActionPolicy.TrackAlignment(16.01f, 1f));
        Check(!ServerActionPolicy.TrackAlignment(0f, 0.49f));
        Check(!ServerActionPolicy.TrackAlignment(float.NaN, 1f));
        Check(!ServerActionPolicy.TrackAlignment(0f, float.PositiveInfinity));
    }
    public static void RemoteFlagsCannotSmugglePhysicalActions()
    {
        Check(ServerActionPolicy.CouplerFlags(4097, out var remote) && remote);
        Check(ServerActionPolicy.CouplerFlags(8193, out remote) && remote);
        foreach (ushort flags in new ushort[] { 4096, 12289, 4099, 8195, 32768, 48, 192, 768, 3072 })
            Check(!ServerActionPolicy.CouplerFlags(flags, out remote));
    }
    public static void PreservesAdvancedUiAndPhysicalRelease()
    {
        Check(ServerActionPolicy.CouplerFlags((ushort)(CouplerInteractionType.CoupleViaUI | CouplerInteractionType.HoseConnect | CouplerInteractionType.CockOpen), out var remote) && !remote);
        Check(ServerActionPolicy.CouplerFlags(0, out remote) && !remote);
        Check(ServerActionPolicy.CouplerFlags(1, out remote) && !remote);
    }
    public static void PermissionRefusalsRoundTrip()
    {
        var writer = new NetDataWriter();
        new LicensePurchaseResponse { Id = "license", Status = LicensePurchaseStatus.PermissionDenied }.Serialize(writer);
        var license = new LicensePurchaseResponse(); license.Deserialize(new NetDataReader(writer.CopyData()));
        Check(license.Status == LicensePurchaseStatus.PermissionDenied);
        writer.Reset();
        new ShopQuoteResponse { Status = ShopQuoteStatus.PermissionDenied }.Serialize(writer);
        var quote = new ShopQuoteResponse(); quote.Deserialize(new NetDataReader(writer.CopyData()));
        Check(quote.Status == ShopQuoteStatus.PermissionDenied);
    }
}
