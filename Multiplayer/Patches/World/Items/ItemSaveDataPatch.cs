using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(ItemSaveData))]
public static class ItemSaveDataPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(ItemSaveData.SaveItemData))]
    private static void Save(ItemSaveData __instance, ref JObject __result)
    {
        if (!__instance.TryGetComponent<NetworkedItem>(out var item)) return;
        __result ??= new JObject();
        __result[PlayerInventorySaveCodec.IdentityKey] = item.PersistentId.ToString("N");
    }

    [HarmonyPrefix, HarmonyPatch(nameof(ItemSaveData.LoadItemData))]
    private static void Load(ItemSaveData __instance, JObject data)
    {
        if (data?[PlayerInventorySaveCodec.IdentityKey] == null ||
            !__instance.TryGetComponent<NetworkedItem>(out var item)) return;
        item.RestorePersistentIdentity(PlayerInventorySaveCodec.Identity(data));
    }
}
