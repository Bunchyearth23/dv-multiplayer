using DV.Teleporters;
using HarmonyLib;
using Multiplayer.Components.Networking;
using UnityEngine;

namespace Multiplayer.Patches.Train;

[HarmonyPatch(typeof(FastTravelController))]
public static class CoordinatedFastTravelPatch
{
    // Used only during the synchronous native quote calculation on the server.
    internal static Vector3? QuoteOrigin;

    [HarmonyPrefix, HarmonyPatch("GetDistanceTo")]
    private static bool Distance(FastTravelDestination marker, ref float __result)
    {
        if (!QuoteOrigin.HasValue) return true;
        __result = Vector3.Distance(QuoteOrigin.Value, marker.playerTeleportAnchor.position);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("OnFastTravelRequested")]
    private static bool Request(bool withLoco, FastTravelDestination ___lastMarkerClicked)
    {
        if (!withLoco) return true;
        NetworkLifecycle.Instance.Client.RequestFastTravel(___lastMarkerClicked);
        return false;
    }
}
