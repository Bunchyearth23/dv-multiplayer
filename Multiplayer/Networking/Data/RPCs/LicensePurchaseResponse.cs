using System;
using LiteNetLib.Utils;

namespace Multiplayer.Networking.Data.RPCs;

public enum LicensePurchaseStatus : byte
{
    Success,
    InvalidRequest,
    NotReady,
    DebtOutstanding,
    InsufficientFunds,
    PrerequisiteMissing,
    OutOfRange,
    ServerError,
    PermissionDenied
}

public sealed class LicensePurchaseResponse : IRpcResponse
{
    public string Id { get; set; }
    public bool IsJobLicense { get; set; }
    public LicensePurchaseStatus Status { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        Validate();
        writer.Put(Id);
        writer.Put(IsJobLicense);
        writer.Put((byte)Status);
    }

    public void Deserialize(NetDataReader reader)
    {
        Id = reader.GetString();
        IsJobLicense = reader.GetBool();
        Status = (LicensePurchaseStatus)reader.GetByte();
        Validate();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 256)
            throw new FormatException("License identifier is invalid.");
        if (!Enum.IsDefined(typeof(LicensePurchaseStatus), Status))
            throw new FormatException("License purchase status is invalid.");
    }
}
