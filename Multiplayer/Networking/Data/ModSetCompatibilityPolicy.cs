using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Networking.Data;

public sealed class ModSetCompatibilityResult
{
    public bool IsValid => Missing.Count == 0 && Extra.Count == 0 && VersionMismatches.Count == 0 && InvalidEntries.Count == 0;
    public List<CompatibleModDescriptor> Missing { get; } = [];
    public List<CompatibleModDescriptor> Extra { get; } = [];
    public List<CompatibleModDescriptor> VersionMismatches { get; } = [];
    public List<CompatibleModDescriptor> InvalidEntries { get; } = [];
}

public readonly struct CompatibleModDescriptor
{
    public string Id { get; }
    public string Version { get; }
    public CompatibleModDescriptor(string id, string version) { Id = id; Version = version; }
}

/// <summary>Validates the symmetric, session-required mod set advertised during login.</summary>
public static class ModSetCompatibilityPolicy
{
    public static ModSetCompatibilityResult Validate(IEnumerable<CompatibleModDescriptor> hostMods, IEnumerable<CompatibleModDescriptor> clientMods)
    {
        var result = new ModSetCompatibilityResult();
        var host = Index(hostMods, result);
        var client = Index(clientMods, result);

        foreach (var pair in host)
        {
            if (!client.TryGetValue(pair.Key, out var candidate))
                result.Missing.Add(pair.Value);
            else if (!string.Equals(pair.Value.Version, candidate.Version, StringComparison.OrdinalIgnoreCase))
                result.VersionMismatches.Add(candidate);
        }

        foreach (var pair in client)
            if (!host.ContainsKey(pair.Key)) result.Extra.Add(pair.Value);

        return result;
    }

    private static Dictionary<string, CompatibleModDescriptor> Index(IEnumerable<CompatibleModDescriptor> mods, ModSetCompatibilityResult result)
    {
        var indexed = new Dictionary<string, CompatibleModDescriptor>(StringComparer.OrdinalIgnoreCase);
        if (mods == null) return indexed;

        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Id) || string.IsNullOrWhiteSpace(mod.Version) || indexed.ContainsKey(mod.Id))
            {
                result.InvalidEntries.Add(mod);
                continue;
            }
            indexed.Add(mod.Id, mod);
        }
        return indexed;
    }
}
