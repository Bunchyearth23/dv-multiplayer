namespace Multiplayer.Networking.Data;

/// <summary>Server authority does not imply the existence of a local player.</summary>
public static class LocalServerIdentity
{
    public static byte? Resolve(bool isClientRunning, byte? playerId) => isClientRunning ? playerId : null;
}
