using System;
using LiteNetLib.Utils;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Utils;

internal static class TrainLoadingTests
{
    public static void ManifestAndRecoveryRoundTrip()
    {
        var processor = new NetPacketProcessor();
        var manifest = new LoadingManifest();
        ushort[] recovery = null;
        bool manifestRecovery = false;
        processor.SubscribeReusable<ClientboundTrainManifestPacket>(p => manifest.SetExpected(p.CarNetIds));
        processor.SubscribeReusable<ServerboundTrainRecoveryPacket>(p => recovery = p.CarNetIds);
        processor.SubscribeReusable<ServerboundTrainManifestRecoveryPacket>(_ => manifestRecovery = true);

        var writer = new NetDataWriter();
        processor.Write(writer, new ClientboundTrainManifestPacket { CarNetIds = new ushort[] { 2, 65535 } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        manifest.MarkApplied(2);
        Check(!manifest.IsComplete && manifest.Missing.Length == 1 && manifest.Missing[0] == 65535);

        writer.Reset();
        processor.Write(writer, new ServerboundTrainRecoveryPacket { CarNetIds = manifest.Missing });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(recovery.Length == 1 && recovery[0] == 65535);
        writer.Reset();
        processor.Write(writer, new ServerboundTrainManifestRecoveryPacket());
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(manifestRecovery);
        manifest.MarkApplied(65535);
        Check(manifest.IsComplete);
    }

    public static void ConflictingManifestIsRejected()
    {
        var manifest = new LoadingManifest();
        manifest.SetExpected(new ushort[] { 1, 2 });
        manifest.SetExpected(new ushort[] { 2, 1 });
        try
        {
            manifest.SetExpected(new ushort[] { 1, 3 });
            throw new Exception("Conflicting train manifest accepted");
        }
        catch (ArgumentException) { }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Train loading invariant failed");
    }
}
