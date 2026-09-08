using System;
using System.Linq;
using LiteNetLib.Utils;
using Multiplayer.Utils;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;

internal static class WorldItemLoadingTests
{
    private static void Check(bool value) { if (!value) throw new Exception("World item loading invariant failed."); }
    public static void RecoveryIsBoundedAndRateLimited()
    {
        double now = 0;
        var recovery = new LoadingRecovery(() => now);
        var ids = Enumerable.Range(1, 300).Select(i => (ushort)i).ToArray();
        Check(!recovery.TryTake(ids, out _)); now = 5;
        Check(recovery.TryTake(ids, out var batch) && batch.Length == 128 && batch[127] == 128);
        batch[0] = 99; Check(ids[0] == 1);
        Check(!recovery.TryTake(ids, out _)); now = 10;
        Check(!recovery.TryTake(Array.Empty<ushort>(), out _));
        Check(recovery.TryTake(ids.Skip(128).ToArray(), out batch) && batch[0] == 129);
    }
    public static void RecoveryDoesNotResetDeadline()
    {
        double now = 0;
        var recovery = new LoadingRecovery(() => now);
        var deadline = new LoadingDeadline("world items", 12, 20, () => now);
        deadline.Check("missing 1"); now = 5;
        Check(recovery.TryTake(new ushort[] { 1 }, out _)); deadline.Check("missing 1");
        now = 10; Check(recovery.TryTake(new ushort[] { 1 }, out _)); deadline.Check("missing 1");
        now = 12;
        try { deadline.Check("missing 1"); }
        catch (TimeoutException) { return; }
        throw new Exception("Recovery extended the deadline");
    }
    public static void RecoveryDoesNotStarveLaterItems()
    {
        double now = 0;
        var recovery = new LoadingRecovery(() => now);
        var ids = Enumerable.Range(1, 300).Select(i => (ushort)i).ToArray();
        var seen = new System.Collections.Generic.HashSet<ushort>();
        for (int i = 0; i < 3; i++)
        {
            now += 5;
            Check(recovery.TryTake(ids, out var batch));
            foreach (var id in batch) seen.Add(id);
        }
        Check(seen.Count == 300);
        now += 5;
        foreach (var invalid in new[] { new ushort[] { 0 }, new ushort[] { 1, 1 } })
        {
            try { recovery.TryTake(invalid, out _); throw new Exception("Invalid recovery accepted"); }
            catch (ArgumentException) { }
        }
        Check(recovery.TryTake(new ushort[] { 1 }, out _));
    }
    public static void PacketsRoundTrip()
    {
        var processor = new NetPacketProcessor();
        var manifest = new LoadingManifest(); ushort[] recovery = null;
        processor.SubscribeReusable<ClientboundWorldItemManifestPacket>(p => manifest.SetExpected(p.ItemIds));
        processor.SubscribeReusable<ServerboundWorldItemRecoveryPacket>(p => recovery = p.ItemIds);
        var writer = new NetDataWriter();
        processor.Write(writer, new ClientboundWorldItemManifestPacket { ItemIds = new ushort[] { 1, 65535 } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(!manifest.IsComplete); manifest.MarkApplied(65535);
        writer.Reset(); processor.Write(writer, new ServerboundWorldItemRecoveryPacket { ItemIds = manifest.Missing });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(recovery.Length == 1 && recovery[0] == 1);
        manifest.MarkApplied(1); Check(manifest.IsComplete);
    }
}
