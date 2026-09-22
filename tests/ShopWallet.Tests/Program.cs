using System;
using HarmonyLib;
using DV.CashRegister;
using DV.Interaction;
using DV.CabControls;
using Multiplayer.Components.Networking.World;

internal static class Program
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static int Main()
    {
        try
        {
            new Harmony("shop-wallet-offline-checks").PatchAll(typeof(Program).Assembly);
            var register = new CashRegisterWithModules();
            var shop = new NetworkedCashRegisterWithModules();
            NetworkedCashRegisterWithModules.Registers.Add(register, shop);
            var target = new ItemUseTarget();
            target.Components.Add(typeof(CashRegisterBase), register);
            var wallet = new MoneyUse();
            wallet.Components.Add(typeof(Wallet), new Wallet());
            Check(wallet.HandleUse(target) && wallet.Spends == 0 && shop.Presentations == 1,
                "Non-VR wallet must quote before any native spend");
            shop.Accept = false;
            Check(!wallet.HandleUse(target) && wallet.Spends == 0, "Busy/rejected wallet fell through to spend");
            var banknote = new MoneyUse();
            Check(banknote.HandleUse(target) && banknote.Spends == 1, "Banknote path changed");
            shop.IsShopRegister = false;
            Check(wallet.HandleUse(target) && wallet.Spends == 1, "Service register path changed");
            shop.IsShopRegister = true;
            var collider = new UnityEngine.Collider();
            collider.Components.Add(typeof(Wallet), new Wallet());
            var item = new ItemBase { Held = true };
            collider.Components.Add(typeof(ItemBase), item);
            int before = shop.Presentations;
            register.OnTriggerEnter(collider);
            Check(register.Spends == 0 && shop.Presentations == before + 1, "VR wallet did not intercept spending");
            item.Held = false;
            register.OnTriggerEnter(collider);
            Check(register.Spends == 0 && shop.Presentations == before + 1, "Dropped wallet authorized payment");
            var texts = new CashRegisterWithModulesTextController(register);
            shop.ShopPaymentAmount = 100;
            texts.UpdateCashText(); texts.UpdateInfoText();
            Check(register.cashText.text == "$100.00" && register.infoText.text == "cashreg/ready_to_buy" &&
                register.DepositedCash == 0, "Preview became refundable cash or did not render");
            register.IsProcessingTransaction = true;
            texts.UpdateInfoText();
            Check(register.infoText.text == "native", "Quote overwrote transaction status");
            register.OnUnitsToBuyChanged();
            Check(!shop.ShopPaymentAmount.HasValue && shop.Invalidations == 1, "Basket edit did not invalidate quote");
            register.Cancel(); register.OnDisable();
            Check(shop.Invalidations == 3, "Cancel or disable hook not applied");
            texts.UpdateCashText();
            Check(register.cashText.text == "$0.00", "Cancelled quote retained display");
            Console.WriteLine("11 shop wallet Harmony checks passed (mock native surfaces; no Unity runtime proof).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
