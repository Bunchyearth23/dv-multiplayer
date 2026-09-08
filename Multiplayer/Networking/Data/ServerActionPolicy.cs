using System;
using Multiplayer.Networking.Data.Train;

namespace Multiplayer.Networking.Data;

// Pure checks shared by server handlers. Distances use one coordinate frame.
public static class ServerActionPolicy
{
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static bool InRange(float squaredDistance, float range) =>
        Finite(squaredDistance) && Finite(range) && squaredDistance >= 0 && range >= 0 &&
        squaredDistance <= (double)range * range;
    public static bool Allowed(bool ready, bool host, bool enabled) => ready && (host || enabled);
    public static bool Index(float value, int count) => Finite(value) && value >= 0 && value < count && value == Math.Truncate(value);

    public static bool CouplerFlags(ushort raw, out bool remote)
    {
        var flags = (CouplerInteractionType)raw;
        const ushort known = 16383;
        remote = (raw & 12288) != 0;
        if ((raw & ~known) != 0) return false;
        if (remote) return flags == (CouplerInteractionType.Start | CouplerInteractionType.CoupleViaRemote) ||
            flags == (CouplerInteractionType.Start | CouplerInteractionType.UncoupleViaRemote);
        return !Both(raw, 2, 4) && !Both(raw, 2, 8) && !Both(raw, 4, 8) &&
            !Both(raw, 16, 32) && !Both(raw, 64, 128) && !Both(raw, 256, 512) && !Both(raw, 1024, 2048);
    }
    private static bool Both(ushort flags, int a, int b) => (flags & a) != 0 && (flags & b) != 0;
}
