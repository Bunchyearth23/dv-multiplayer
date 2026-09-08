using System;
using Multiplayer.Utils;

internal static class IdPoolTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("ID allocation invariant failed"); }

    public static void DuplicateAndUnknownReleaseIgnored()
    {
        var pool = new IdPool<ushort>();
        Check(pool.NextId == 1);
        pool.ReleaseId(1); pool.ReleaseId(1); pool.ReleaseId(0); pool.ReleaseId(500);
        Check(pool.NextId == 1 && pool.NextId == 2);
    }

    public static void ReservedIdsAreNotReissued()
    {
        var pool = new IdPool<ushort>(); pool.ReserveId(1);
        Check(pool.NextId == 2);
        pool.ReleaseId(2); pool.ReserveId(2);
        Check(pool.NextId == 3);
        pool.ReleaseId(2); Check(pool.NextId == 2);
    }

    public static void ExhaustionNeverReturnsZero()
    {
        var pool = new IdPool<byte>();
        for (int id = 1; id <= 255; id++) Check(pool.NextId == id);
        try { _ = pool.NextId; throw new Exception("Overflow not detected"); }
        catch (OverflowException) { }
        pool.ReleaseId(255); Check(pool.NextId == 255);
    }

    public static void ResetClearsReservations()
    {
        var pool = new IdPool<uint>(); pool.ReserveId(1); Check(pool.NextId == 2);
        pool.ReleaseId(2); pool.Reset(); Check(pool.NextId == 1 && pool.NextId == 2);
    }
}
