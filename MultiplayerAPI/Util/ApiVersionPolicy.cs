using System;

namespace MPAPI.Util;

public static class ApiVersionPolicy
{
    /// <summary>Accepts additive updates within the required major API line only.</summary>
    public static bool IsCompatible(string required, string loaded)
    {
        return Version.TryParse(required, out var requiredVersion) &&
            Version.TryParse(loaded, out var loadedVersion) &&
            loadedVersion.Major == requiredVersion.Major && loadedVersion >= requiredVersion;
    }
}
