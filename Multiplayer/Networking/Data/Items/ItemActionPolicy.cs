using Multiplayer.Components.Networking.World;

namespace Multiplayer.Networking.Data.Items;

/// <summary>Validates client intent against facts supplied by the server.</summary>
public static class ItemActionPolicy
{
    public static bool CanApply(ItemUpdateData request, byte senderId, byte? ownerId,
        float distanceToItem, float reach, float distanceToDestination, bool validAttachment)
    {
        const ItemUpdateData.ItemUpdateType allowed = ItemUpdateData.ItemUpdateType.FullSync;
        if (request == null || request.ItemNetId == 0 ||
            request.UpdateType == ItemUpdateData.ItemUpdateType.None ||
            (request.UpdateType & ~allowed) != 0 ||
            request.ItemState < ItemState.Dropped || request.ItemState > ItemState.Attached ||
            !Finite(reach) || reach <= 0)
            return false;

        if (ownerId.HasValue && ownerId.Value != senderId)
            return false;

        bool changesLocation = (request.UpdateType & (ItemUpdateData.ItemUpdateType.ItemState |
            ItemUpdateData.ItemUpdateType.ItemPosition)) != 0;
        if (!changesLocation)
            return ownerId.HasValue || WithinReach(distanceToItem, reach);

        switch (request.ItemState)
        {
            case ItemState.InHand:
            case ItemState.InInventory:
                // This field is present on the wire only for ownership transitions.
                return request.Player == senderId &&
                    (ownerId.HasValue || WithinReach(distanceToItem, reach));
            case ItemState.Dropped:
            case ItemState.Thrown:
                return ownerId.HasValue && WithinReach(distanceToDestination, reach) &&
                    Finite(request.ItemPosition.x) && Finite(request.ItemPosition.y) && Finite(request.ItemPosition.z) &&
                    ValidRotation(request) &&
                    (request.ItemState != ItemState.Thrown ||
                        (Finite(request.ThrowDirection.x) && Finite(request.ThrowDirection.y) && Finite(request.ThrowDirection.z)));
            case ItemState.Attached:
                return ownerId.HasValue && validAttachment && WithinReach(distanceToDestination, reach);
            default:
                return false;
        }
    }

    private static bool WithinReach(float distance, float reach) =>
        Finite(distance) && distance >= 0 && distance <= reach;

    private static bool ValidRotation(ItemUpdateData request)
    {
        var q = request.ItemRotation;
        float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        return Finite(norm) && norm > 0.000001f && norm < 4f;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
