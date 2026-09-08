using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Utils;

/// <summary>Completion records application, not packet receipt; ordering and duplicate receipts do not matter.</summary>
public sealed class LoadingManifest
{
    private HashSet<ushort> expected;
    private readonly HashSet<ushort> applied = new();
    public bool HasManifest => expected != null;
    public ushort[] Missing => expected == null ? Array.Empty<ushort>() : expected.Where(id => !applied.Contains(id)).ToArray();
    public bool IsComplete => expected != null && expected.IsSubsetOf(applied);

    public void SetExpected(ushort[] ids)
    {
        if (ids == null || ids.Any(id => id == 0)) throw new ArgumentException("Invalid loading manifest.");
        var values = new HashSet<ushort>(ids);
        if (values.Count != ids.Length || (expected != null && !expected.SetEquals(values)))
            throw new ArgumentException("Conflicting loading manifest.");
        expected = values;
    }

    public void MarkApplied(ushort id)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        applied.Add(id);
    }
}
