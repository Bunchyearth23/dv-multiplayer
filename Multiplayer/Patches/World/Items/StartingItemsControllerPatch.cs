using HarmonyLib;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsControllerPatch
{
    [ThreadStatic] private static int authoritativeDepth;

    private sealed class LoadScope : IDisposable
    {
        private bool disposed;
        public LoadScope() { authoritativeDepth++; }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            authoritativeDepth--;
        }
    }

    [HarmonyPostfix, HarmonyPatch("AddStartingItemsCoro")]
    private static void Load(SaveGameData saveGameData, ref IEnumerator __result)
    {
        if (saveGameData.GetBool(StartingInventoryGrants.AuthoritativeLoadKey) == true)
            __result = new ScopedRoutine(__result, () => new LoadScope());
    }

    [HarmonyPrefix, HarmonyPatch("CombineAndResolveIllegalDuplicates")]
    private static bool Combine(List<StorageItemData> itemsInventory, List<StorageItemData> itemsLostAndFound,
        List<StorageItemData> itemsWorld, List<StorageItemData> itemsInstalledGadgets,
        List<StorageItemData> itemsItemContainers, ref List<StorageItemData> __result)
    {
        if (authoritativeDepth == 0) return true;
        // Two purchases of the same prefab are distinct objects. Native safeguards deduplicate
        // essentials and licenses by prefab; authoritative saves instead require unique identities.
        var combined = itemsInventory.Concat(itemsLostAndFound).Concat(itemsWorld)
            .Concat(itemsInstalledGadgets).Concat(itemsItemContainers).ToList();
        var identities = new HashSet<Guid>();
        foreach (var item in combined)
            if (item == null || string.IsNullOrWhiteSpace(item.itemPrefabName) ||
                !identities.Add(PlayerInventorySaveCodec.Identity(item.state)))
                throw new FormatException("Invalid or duplicate authoritative inventory item.");
        __result = combined;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("StartingItemsSafeguard")]
    private static bool StartingItems(SaveGameData saveGameData) =>
        saveGameData.GetBool(StartingInventoryGrants.AuthoritativeLoadKey) != true;

    [HarmonyPrefix, HarmonyPatch("LicensesSafeguard")]
    private static bool Licenses(SaveGameData saveGameData) =>
        saveGameData.GetBool(StartingInventoryGrants.AuthoritativeLoadKey) != true;
}
