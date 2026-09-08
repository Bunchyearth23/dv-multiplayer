using System;

namespace Multiplayer.Networking.Data;

public static class ProtocolCompatibility
{
    public const int Version = 4;
    private const string Separator = "|dvmp-protocol:";

    public static string HandshakeBuild(string gameBuild)
    {
        if (string.IsNullOrWhiteSpace(gameBuild)) throw new ArgumentException("Game build is required.", nameof(gameBuild));
        return gameBuild + Separator + Version;
    }
}
