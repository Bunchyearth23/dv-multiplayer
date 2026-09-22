using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(RespawnOnDrop))]
public static class RespawnOnDropPatch
{
    // Preserve initialization, listeners and UpdateSpawnParams for native storage.
    // Only the host may decide to respawn/destroy items or change their distant physics.
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(RespawnOnDrop), "Checker");
        yield return AccessTools.Method(typeof(RespawnOnDrop), nameof(RespawnOnDrop.RespawnOrDestroy));
    }

    [HarmonyPrefix]
    private static bool RunAuthoritativeRoutine(ref IEnumerator __result)
    {
        if (!NetworkLifecycle.Instance.IsClientRunning || NetworkLifecycle.Instance.IsHost()) return true;
        __result = Empty();
        return false;
    }

    private static IEnumerator Empty() { yield break; }
}
