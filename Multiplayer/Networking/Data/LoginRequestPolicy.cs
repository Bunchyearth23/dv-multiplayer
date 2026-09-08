using System;

namespace Multiplayer.Networking.Data;

public static class LoginRequestPolicy
{
    public const int MaxUsernameWireLength = 96;
    public const int MaxBuildVersionLength = 128;
    public const int MaxPasswordLength = 256;
    public const int MaxCharacterIdLength = 128;
    public const int MaxMods = 256;
    public const int MaxModIdLength = 128;
    public const int MaxModVersionLength = 128;

    public static bool IsValidEnvelope(string username, byte[] guid, string password,
        string buildVersion, string characterId, int modCount)
    {
        return !string.IsNullOrWhiteSpace(username)
            && username.Length <= MaxUsernameWireLength
            && guid?.Length == 16
            && password != null && password.Length <= MaxPasswordLength
            && !string.IsNullOrWhiteSpace(buildVersion) && buildVersion.Length <= MaxBuildVersionLength
            && characterId != null && characterId.Length <= MaxCharacterIdLength
            && modCount >= 0 && modCount <= MaxMods;
    }

    public static bool IsValidMod(string id, string version)
    {
        return !string.IsNullOrWhiteSpace(id) && id.Length <= MaxModIdLength
            && version != null && version.Length <= MaxModVersionLength;
    }
}
