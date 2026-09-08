using System.Collections.Generic;

namespace Multiplayer.Networking.Data.Train;

public static class TrainCompositionPolicy
{
    public const int MaxCars = 512;

    public static bool IsValid(ushort[] ids)
    {
        if (ids == null || ids.Length == 0 || ids.Length > MaxCars) return false;
        var seen = new HashSet<ushort>();
        foreach (var id in ids)
            if (id == 0 || !seen.Add(id)) return false;
        return true;
    }

    // Trainset orientation can legitimately be reversed on a replica.
    public static bool Matches(ushort[] local, ushort[] authoritative)
    {
        if (!IsValid(local) || !IsValid(authoritative) || local.Length != authoritative.Length) return false;
        bool forward = true, reverse = true;
        for (int i = 0; i < local.Length; i++)
        {
            forward &= local[i] == authoritative[i];
            reverse &= local[i] == authoritative[local.Length - 1 - i];
        }
        return forward || reverse;
    }
}
