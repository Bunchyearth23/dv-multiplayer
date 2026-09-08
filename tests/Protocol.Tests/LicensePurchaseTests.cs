using System;
using LiteNetLib.Utils;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Packets.Serverbound;

internal static class LicensePurchaseTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void ResponseRoundTrips()
    {
        var writer = new NetDataWriter();
        IRpcResponse sent = new LicensePurchaseResponse
        {
            Id = "train_driver",
            IsJobLicense = true,
            Status = LicensePurchaseStatus.PrerequisiteMissing
        };
        sent.Serialize(writer);

        var result = new LicensePurchaseResponse();
        result.Deserialize(new NetDataReader(writer.CopyData()));
        Check(result.Id == "train_driver" && result.IsJobLicense &&
            result.Status == LicensePurchaseStatus.PrerequisiteMissing, "License response changed");
    }

    public static void RequestRoundTrips()
    {
        var processor = new NetPacketProcessor();
        bool received = false;
        processor.SubscribeReusable<ServerboundLicensePurchaseRequestPacket>(packet =>
        {
            Check(packet.TicketId == 27 && packet.Id == "train_driver" && packet.IsJobLicense,
                "License request changed");
            received = true;
        });
        var writer = new NetDataWriter();
        processor.Write(writer, new ServerboundLicensePurchaseRequestPacket
            { TicketId = 27, Id = "train_driver", IsJobLicense = true });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received, "License request not dispatched");
    }

    public static void InvalidResponsesAreRejected()
    {
        var writer = new NetDataWriter();
        writer.Put("train_driver");
        writer.Put(false);
        writer.Put(byte.MaxValue);
        bool rejected = false;
        try { new LicensePurchaseResponse().Deserialize(new NetDataReader(writer.CopyData())); }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Unknown license status accepted");

        rejected = false;
        try
        {
            new LicensePurchaseResponse { Id = "", Status = LicensePurchaseStatus.Success }.Serialize(new NetDataWriter());
        }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Empty license identifier accepted");
    }
}
