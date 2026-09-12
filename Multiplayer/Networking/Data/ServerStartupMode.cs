namespace Multiplayer.Networking.Data;

/// <summary>Defines whether a server also starts a local loopback player.</summary>
public enum ServerStartupMode
{
    Hosted,
    Dedicated,
}

public static class ServerStartupModes
{
    public static bool StartsLocalClient(ServerStartupMode mode) => mode == ServerStartupMode.Hosted;

    public static ServerStartupMode FromDedicatedFlag(bool dedicated) =>
        dedicated ? ServerStartupMode.Dedicated : ServerStartupMode.Hosted;
}
