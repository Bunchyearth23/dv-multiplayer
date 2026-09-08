using System;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Serverbound.Train;

internal static class TrainRecoveryTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Train recovery assertion failed."); }
    public static void CompositionDetectsReplacementAndOrder()
    {
        ushort[] original = { 1, 2, 3, 4 };
        Check(TrainCompositionPolicy.Matches(original, new ushort[] { 4, 3, 2, 1 }));
        Check(!TrainCompositionPolicy.Matches(original, new ushort[] { 1, 3, 2, 4 }));
        Check(!TrainCompositionPolicy.Matches(original, new ushort[] { 1, 2, 5, 4 }));
        Check(!TrainCompositionPolicy.Matches(original, new ushort[] { 1, 4 }));
        Check(!TrainCompositionPolicy.Matches(null, original));
    }
    public static void InvalidManifestsAreRejected()
    {
        Check(!TrainCompositionPolicy.IsValid(new ushort[] { 1, 1 }));
        Check(!TrainCompositionPolicy.IsValid(new ushort[] { 1, 0 }));
        Check(!TrainCompositionPolicy.IsValid(Array.Empty<ushort>()));
        var ids = new ushort[TrainCompositionPolicy.MaxCars + 1];
        for (int i = 0; i < ids.Length; i++) ids[i] = (ushort)(i + 1);
        Check(!TrainCompositionPolicy.IsValid(ids));
    }
    public static void TravelRetriesCannotChargeTwice()
    {
        var op = new FastTravelOperation();
        Check(op.Begin(1, 4, "Harbor"));
        Check(!op.Begin(1, 4, "Harbor") && op.Matches(1, 4, "Harbor"));
        Check(!op.Begin(2, 4, "Harbor"));
        op.Status = 1;
        Check(!op.Begin(1, 4, "Harbor") && !op.Matches(1, 5, "Harbor"));
        Check(!op.Matches(1, 4, "Steel Mill"));
        Check(op.Begin(2, 4, "Steel Mill"));
        op.Status = 0;
        Check(!op.Begin(1, 4, "Harbor") && op.Begin(3, 4, "Harbor"));
    }
    public static void TravelRecoveryBlocksNewOperations()
    {
        var op = new FastTravelOperation();
        Check(!op.Begin(0, 1, "Harbor") && !op.Begin(1, 0, "Harbor"));
        Check(!op.Begin(1, 1, new string('x', 257)) && !op.Begin(1, 1, " "));
        Check(op.Begin(1, 1, "Harbor"));
        op.Status = 3;
        Check(!op.Begin(2, 1, "Harbor") && op.Status == 3 && op.Id == 1);
    }
    public static void TravelPacketsRoundTrip()
    {
        var writer = new NetDataWriter();
        var processor = new NetPacketProcessor();
        bool request = false, response = false;
        processor.SubscribeReusable<ServerboundFastTravelPacket>(p => request = p.OperationId == 42 && p.CarId == 65535 && p.Destination == "Harbor");
        processor.Write(writer, new ServerboundFastTravelPacket { OperationId = 42, CarId = 65535, Destination = "Harbor" });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        writer.Reset();
        processor.SubscribeReusable<ClientboundFastTravelPacket>(p => response = p.OperationId == 42 && p.CarId == 65535 && p.Status == 3);
        processor.Write(writer, new ClientboundFastTravelPacket { OperationId = 42, CarId = 65535, Status = 3 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(request && response);
    }
}
