using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Serialization;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Networking.Data.Items;

public class ItemUpdateData
{
    public const int MaxTrackedValues = 256;
    public const int MaxKeyLength = 128;
    public const int MaxStringLength = 4096;
    public const int MaxPrefabLength = 256;
    [Flags]
    public enum ItemUpdateType : byte
    {
        None = 0,
        Create = 1,
        Destroy = 2,
        ItemState = 4,
        ItemPosition = 8,
        ObjectState = 16,
        FullSync = ItemState | ItemPosition | ObjectState,
    }

    public ItemUpdateType UpdateType { get; set; }
    public ushort ItemNetId { get; set; }
    public string PrefabName { get; set; }
    public ItemState ItemState { get; set; }
    public Vector3 ItemPosition { get; set; }
    public Quaternion ItemRotation { get; set; }
    public Vector3 ThrowDirection { get; set; }
    public byte Player { get; set; }
    public ushort CarNetId { get; set; }
    public bool AttachedFront  { get; set; }
    public Dictionary<string, object> States { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        ValidateHeader();
        if (States != null && States.Count > MaxTrackedValues)
            throw new FormatException("Too many tracked item values.");
        // The existing wire format carries transforms inside ItemState payloads.
        // Promote position-only changes so older readers also consume the transform.
        var wireUpdateType = UpdateType;
        if ((wireUpdateType & ItemUpdateType.ItemPosition) != 0)
            wireUpdateType |= ItemUpdateType.ItemState;
        writer.Put((byte)wireUpdateType);
        writer.Put(ItemNetId);

        if (wireUpdateType == ItemUpdateType.Destroy)
            return;

        writer.Put((byte)ItemState);

        if (wireUpdateType.HasFlag(ItemUpdateType.Create))
            writer.Put(PrefabName);

        if (wireUpdateType.HasFlag(ItemUpdateType.Create) || wireUpdateType.HasFlag(ItemUpdateType.ItemState))
        {
            if (ItemState == ItemState.Dropped || ItemState == ItemState.Thrown) // || UpdateType.HasFlag(ItemUpdateType.ItemPosition)
            {
                Vector3Serializer.Serialize(writer, ItemPosition);
                QuaternionSerializer.Serialize(writer, ItemRotation);

                if (ItemState == ItemState.Thrown)
                    Vector3Serializer.Serialize(writer, ThrowDirection);
            }
            else if (ItemState == ItemState.InInventory || ItemState == ItemState.InHand)
            {
                writer.Put(Player);
            }
            else if (ItemState == ItemState.Attached)
            {
                writer.Put(CarNetId);
                writer.Put(AttachedFront);
            }
        }

        if (wireUpdateType.HasFlag(ItemUpdateType.Create) || wireUpdateType.HasFlag(ItemUpdateType.ObjectState))
        {
            if (States == null)
                writer.Put(0);
            else
            {
                writer.Put(States.Count);
                foreach (var state in States)
                {
                    ValidateKey(state.Key);
                    writer.Put(state.Key);
                    SerializeTrackedValue(writer, state.Value);
                }
            }
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        // Instances may be reused: fields absent from this payload must not retain prior values.
        PrefabName = null;
        States = null;
        Player = 0;
        CarNetId = 0;
        AttachedFront = false;
        ItemPosition = ThrowDirection = default;
        ItemRotation = default;
        ItemState = default;
        UpdateType = (ItemUpdateType)reader.GetByte();
        ItemNetId = reader.GetUShort();
        ValidateFlags();

        if (UpdateType == ItemUpdateType.Destroy)
            return;

        ItemState = (ItemState)reader.GetByte();
        if (!Enum.IsDefined(typeof(ItemState), ItemState))
            throw new FormatException("Invalid item state.");

        if (UpdateType.HasFlag(ItemUpdateType.Create))
        {
            PrefabName = reader.GetString();
            if (string.IsNullOrWhiteSpace(PrefabName) || PrefabName.Length > MaxPrefabLength)
                throw new FormatException("Invalid item prefab name.");
        }

        if (UpdateType.HasFlag(ItemUpdateType.Create) || UpdateType.HasFlag(ItemUpdateType.ItemState))
        {
            if (ItemState == ItemState.Dropped || ItemState == ItemState.Thrown) // || UpdateType.HasFlag(ItemUpdateType.ItemPosition)
            {
                ItemPosition = Vector3Serializer.Deserialize(reader);
                ItemRotation = QuaternionSerializer.Deserialize(reader);

                if (ItemState == ItemState.Thrown)
                {
                    Multiplayer.LogDebug(() => $"ItemUpdateData.Deserialize() Item Thrown before: {ThrowDirection}");
                    ThrowDirection = Vector3Serializer.Deserialize(reader);
                    Multiplayer.LogDebug(() => $"ItemUpdateData.Deserialize() Item Thrown after: {ThrowDirection}");
                }
            }
            else if (ItemState == ItemState.InInventory || ItemState == ItemState.InHand)
            {
                Player = reader.GetByte();
            }
            else if (ItemState == ItemState.Attached)
            {
                CarNetId = reader.GetUShort();
                AttachedFront = reader.GetBool();
            }
        }

        if (UpdateType.HasFlag(ItemUpdateType.Create) || UpdateType.HasFlag(ItemUpdateType.ObjectState))
        {
            int stateCount = reader.GetInt();
            if (stateCount < 0 || stateCount > MaxTrackedValues)
                throw new FormatException("Invalid tracked item value count.");
            if (stateCount > 0)
            {
                States = new Dictionary<string, object>();
                for (int i = 0; i < stateCount; i++)
                {
                    string key = reader.GetString();
                    ValidateKey(key);
                    if (States.ContainsKey(key)) throw new FormatException("Duplicate tracked item key.");
                    object value = DeserializeTrackedValue(reader);
                    States[key] = value;
                }
            }
        }
    }

    private void ValidateFlags()
    {
        if (ItemNetId == 0 || UpdateType == ItemUpdateType.None ||
            (UpdateType != ItemUpdateType.Create && UpdateType != ItemUpdateType.Destroy &&
             (UpdateType & ~ItemUpdateType.FullSync) != 0))
            throw new FormatException("Invalid item identity or update flags.");
    }

    private void ValidateHeader()
    {
        ValidateFlags();
        if (UpdateType == ItemUpdateType.Destroy) return;
        if (!Enum.IsDefined(typeof(ItemState), ItemState)) throw new FormatException("Invalid item state.");
        if (UpdateType == ItemUpdateType.Create &&
            (string.IsNullOrWhiteSpace(PrefabName) || PrefabName.Length > MaxPrefabLength))
            throw new FormatException("Invalid item prefab name.");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
            throw new FormatException("Invalid tracked item key.");
    }

    private void SerializeTrackedValue(NetDataWriter writer, object value)
    {
        if (value is bool boolValue)
        {
            writer.Put((byte)0);
            writer.Put(boolValue);
        }
        else if (value is int intValue)
        {
            writer.Put((byte)1);
            writer.Put(intValue);
        }
        else if (value is uint uintValue)
        {
            writer.Put((byte)2);
            writer.Put(uintValue);
        }
        else if (value is float floatValue)
        {
            if (float.IsNaN(floatValue) || float.IsInfinity(floatValue)) throw new FormatException("Non-finite item value.");
            writer.Put((byte)3);
            writer.Put(floatValue);
        }
        else if (value is string stringValue)
        {
            if (stringValue.Length > MaxStringLength) throw new FormatException("Item string exceeds limit.");
            writer.Put((byte)4);
            writer.Put(stringValue);
        }
        else
        {
            throw new NotSupportedException($"ItemUpdateData.SerializeTrackedValue({ItemNetId}, {PrefabName??""}) Unsupported type for serialization: {value?.GetType()}");
        }
    }

    private object DeserializeTrackedValue(NetDataReader reader)
    {
        byte typeCode = reader.GetByte();
        switch (typeCode)
        {
            case 0: return reader.GetBool();
            case 1: return reader.GetInt();
            case 2: return reader.GetUInt();
            case 3:
                float number = reader.GetFloat();
                if (float.IsNaN(number) || float.IsInfinity(number)) throw new FormatException("Non-finite item value.");
                return number;
            case 4:
                string text = reader.GetString();
                if (text.Length > MaxStringLength) throw new FormatException("Item string exceeds limit.");
                return text;

            default:
                throw new NotSupportedException($"ItemUpdateData.DeserializeTrackedValue({ItemNetId}, {PrefabName ?? ""}) Unsupported type code for deserialization: {typeCode}");
        }
    }
}
