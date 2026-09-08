using System;
using Multiplayer.Networking.Data;

internal static class ModCompatibilityTests
{
    public static void ExactSetsAndVersionsAreRequired()
    {
        var host = new[] { Mod("BDVM", "1.2.0"), Mod("Cargo", "2.0") };
        Check(ModSetCompatibilityPolicy.Validate(host, host).IsValid, "Identical mod sets rejected");

        var mismatch = ModSetCompatibilityPolicy.Validate(host, new[] { Mod("bdvm", "1.2.1"), Mod("Cargo", "2.0") });
        Check(!mismatch.IsValid && mismatch.VersionMismatches.Count == 1, "Version mismatch accepted");

        var missing = ModSetCompatibilityPolicy.Validate(host, new[] { Mod("BDVM", "1.2.0") });
        Check(missing.Missing.Count == 1 && missing.Missing[0].Id == "Cargo", "Missing host mod not reported");

        var extra = ModSetCompatibilityPolicy.Validate(host, new[] { Mod("BDVM", "1.2.0"), Mod("Cargo", "2.0"), Mod("Extra", "1") });
        Check(extra.Extra.Count == 1 && extra.Extra[0].Id == "Extra", "Extra client mod not reported");
    }

    public static void InvalidAdvertisementsAreRejected()
    {
        var absent = ModSetCompatibilityPolicy.Validate(new[] { Mod("BDVM", "1") }, null);
        Check(!absent.IsValid && absent.Missing.Count == 1, "Absent client advertisement accepted");

        var duplicate = ModSetCompatibilityPolicy.Validate(Array.Empty<CompatibleModDescriptor>(), new[] { Mod("X", "1"), Mod("x", "1") });
        Check(!duplicate.IsValid && duplicate.InvalidEntries.Count == 1, "Duplicate mod identity accepted");

        var malformed = ModSetCompatibilityPolicy.Validate(Array.Empty<CompatibleModDescriptor>(), new[] { Mod("", "1"), Mod("X", "") });
        Check(!malformed.IsValid && malformed.InvalidEntries.Count == 2, "Malformed mod identity accepted");
    }

    public static void AdapterFailuresAreIsolated()
    {
        int called = 0, errors = 0;
        Action handlers = () => called++;
        handlers += () => throw new InvalidOperationException("adapter failed");
        handlers += () => called++;
        MPAPI.Util.EventDispatch.Isolated(handlers, exception => errors++);
        Check(called == 2 && errors == 1, "One adapter failure interrupted other adapters");
    }

    public static void ApiVersionStaysWithinMajorLine()
    {
        Check(MPAPI.Util.ApiVersionPolicy.IsCompatible("1.2.0.0", "1.2.0.0"), "Exact API rejected");
        Check(MPAPI.Util.ApiVersionPolicy.IsCompatible("1.2.0.0", "1.3.0.0"), "Additive API rejected");
        Check(!MPAPI.Util.ApiVersionPolicy.IsCompatible("1.2.0.0", "1.1.9.0"), "Older API accepted");
        Check(!MPAPI.Util.ApiVersionPolicy.IsCompatible("1.2.0.0", "2.0.0.0"), "Breaking major API accepted");
        Check(!MPAPI.Util.ApiVersionPolicy.IsCompatible("bad", "1.2.0.0"), "Malformed API accepted");
    }

    private static CompatibleModDescriptor Mod(string id, string version) => new CompatibleModDescriptor(id, version);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
