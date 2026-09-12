using System;
using Multiplayer.Networking.Data;

internal static class LocalServerIdentityTests
{
    public static void AbsentLocalClientNeverAliasesRemotePlayers()
    {
        foreach (var local in new byte?[] { null, 0, 255 })
        {
            var identity = LocalServerIdentity.Resolve(false, local);
            for (var remote = 0; remote <= byte.MaxValue; remote++)
                if ((byte)remote == identity) throw new Exception("Absent local player matched a remote identity.");
        }
        if (LocalServerIdentity.Resolve(true, null).HasValue) throw new Exception("Unassigned client invented an identity.");
    }

    public static void HostedIdentityRetainsEveryByteValue()
    {
        for (var id = 0; id <= byte.MaxValue; id++)
        {
            if (LocalServerIdentity.Resolve(true, (byte)id) != id) throw new Exception("Hosted identity changed.");
            if (LocalServerIdentity.Resolve(false, (byte)id).HasValue) throw new Exception("Stopped client retained identity.");
        }
    }

    public static void DedicatedStartupNeverCreatesALocalClient()
    {
        if (ServerStartupModes.StartsLocalClient(ServerStartupMode.Dedicated))
            throw new Exception("Dedicated mode started a loopback client.");
        if (!ServerStartupModes.StartsLocalClient(ServerStartupMode.Hosted))
            throw new Exception("Hosted mode omitted its loopback client.");
        if (ServerStartupModes.FromDedicatedFlag(true) != ServerStartupMode.Dedicated)
            throw new Exception("Dedicated setting selected the hosted mode.");
        if (ServerStartupModes.FromDedicatedFlag(false) != ServerStartupMode.Hosted)
            throw new Exception("Hosted setting selected the dedicated mode.");
    }
}
