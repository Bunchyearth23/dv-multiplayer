using DV.CashRegister;
using DV.CabControls;
using DV.Interaction;
using DV.InventorySystem;
using DV.Localization;
using HarmonyLib;
using Multiplayer.Components.Networking.World;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

namespace Multiplayer.Patches.World;

internal static class ShopWalletInteraction
{
    public static bool TryGet(CashRegisterBase register, out NetworkedCashRegisterWithModules shop)
    {
        shop = null;
        return register is CashRegisterWithModules modules &&
            NetworkedCashRegisterWithModules.TryGet(modules, out shop) && shop.IsShopRegister;
    }
}

// Non-VR uses MoneyUse directly; it does not necessarily enter the physical trigger.
[HarmonyPatch(typeof(MoneyUse), nameof(MoneyUse.HandleUse))]
internal static class ShopWalletUsePatch
{
    private static bool Prefix(MoneyUse __instance, ItemUseTarget target, ref bool __result)
    {
        if (__instance.GetComponent<Wallet>() == null ||
            !target.TryGetComponent<CashRegisterBase>(out var register) ||
            !ShopWalletInteraction.TryGet(register, out var shop)) return true;
        __result = shop.PresentShopWallet();
        return false;
    }
}

// VR presents the held wallet collider to the register. Suppress vanilla TrySpend.
[HarmonyPatch(typeof(CashRegisterBase), "OnTriggerEnter")]
internal static class ShopWalletTriggerPatch
{
    private static bool Prefix(CashRegisterBase __instance, Collider col)
    {
        if (col == null || col.GetComponentInParent<Wallet>() == null ||
            !ShopWalletInteraction.TryGet(__instance, out var shop)) return true;
        var item = col.GetComponentInParent<ItemBase>();
        if (item != null && item.IsGrabbed()) shop.PresentShopWallet();
        return false;
    }
}

// Render the server quote without putting fictional refundable cash into DepositedCash.
[HarmonyPatch(typeof(CashRegisterWithModulesTextController))]
internal static class ShopWalletTextPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(CashRegisterWithModulesTextController.UpdateCashText))]
    private static void Cash(CashRegisterWithModules ___cashRegister)
    {
        if (ShopWalletInteraction.TryGet(___cashRegister, out var shop) && shop.ShopPaymentAmount is double amount)
            ___cashRegister.cashText.text = "$" + amount.ToString("N2", LocalizationAPI.CC);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(CashRegisterWithModulesTextController.UpdateInfoText))]
    private static void Info(CashRegisterWithModules ___cashRegister)
    {
        if (ShopWalletInteraction.TryGet(___cashRegister, out var shop) && shop.ShopPaymentAmount.HasValue &&
            !___cashRegister.IsProcessingTransaction && ___cashRegister.TotalUnitsInBasket() > 0)
            ___cashRegister.infoText.text = LocalizationAPI.L("cashreg/ready_to_buy");
    }
}

[HarmonyPatch]
internal static class ShopWalletInvalidationPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CashRegisterWithModules), "OnUnitsToBuyChanged");
        yield return AccessTools.Method(typeof(CashRegisterWithModules), "OnDisable");
        yield return AccessTools.Method(typeof(CashRegisterWithModules), nameof(CashRegisterWithModules.Cancel));
    }

    [HarmonyPrefix]
    private static void Clear(CashRegisterWithModules __instance)
    {
        if (ShopWalletInteraction.TryGet(__instance, out var shop)) shop.ClearShopPayment();
    }
}
