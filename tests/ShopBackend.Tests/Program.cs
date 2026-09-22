using System;
using System.Linq;
using DV.InventorySystem;
using DV.Shops;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using UnityEngine;

internal static class Program
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static ShopPurchaseBackend Setup(bool failedInitialization = false, bool failedStorage = false)
    {
        Inventory.Instance = new Inventory();
        GlobalShopController.Instance = new GlobalShopController();
        StorageController.Instance = new StorageController { FailAdd = failedStorage };
        GameObject.Clones.Clear();
        Resources.Prefab = new GameObject("lamp") { FailInitialization = failedInitialization };
        Resources.Prefab.Components.Add(typeof(DV.CabControls.Spec.Item), new DV.CabControls.Spec.Item());
        Resources.Prefab.Components.Add(typeof(InventoryItemSpec), new InventoryItemSpec());
        Resources.Prefab.Components.Add(typeof(ShopRestocker), new ShopRestocker());
        var register = NetworkedCashRegisterWithModules.Register = new NetworkedCashRegisterWithModules();
        GlobalShopController.Instance.globalShopList.Add(new Shop { cashRegister = new object() });
        return new ShopPurchaseBackend(register, new ServerPlayer(), new[] { "lamp" }, new[] { 1 });
    }

    private static ShopPurchaseOutcome Buy(ShopPurchaseLedger ledger, Guid player, string operation, ShopPurchaseBackend backend)
        => ledger.Execute(player, operation, 1, new[] { "lamp" }, new[] { 1 }, () => backend);

    private static int Main()
    {
        try
        {
            var backend = Setup();
            Check(Resources.Prefab.GetComponent<DV.CabControls.ItemBase>() == null, "Fixture must have no runtime ItemBase on prefab");
            var ledger = new ShopPurchaseLedger();
            var player = Guid.NewGuid(); var operation = Guid.NewGuid().ToString("N");
            var result = Buy(ledger, player, operation, backend);
            Check(result.Quote.Status == ShopQuoteStatus.Success, "Spec-only native prefab rejected: " + result.FailureException);
            Check(Inventory.Instance.PlayerMoney == 400 && Inventory.Instance.Debits == 1 &&
                GlobalShopController.Instance.Data.purchasedItems == 1 && StorageController.Instance.Items.Count == 1,
                "Purchase did not debit and deliver exactly once");
            Check(GameObject.Clones.Single().GetComponent<DV.CabControls.ItemBase>() != null &&
                GameObject.Clones.Single().GetComponent<ShopRestocker>().restockOnItemDestroyed, "Delivered item not initialized/restockable");
            Buy(ledger, player, operation, backend);
            Check(Inventory.Instance.Debits == 1 && GameObject.Clones.Count == 1, "Replay created another purchase");
            var other = Buy(ledger, Guid.NewGuid(), Guid.NewGuid().ToString("N"), backend);
            Check(other.Quote.Status == ShopQuoteStatus.InsufficientStock && Inventory.Instance.Debits == 1,
                "Second player purchased exhausted shared stock");
            backend = Setup();
            GlobalShopController.Instance.globalShopList.Insert(0, null);
            Check(Buy(new ShopPurchaseLedger(), player, Guid.NewGuid().ToString("N"), backend).Quote.Status == ShopQuoteStatus.Success,
                "Missing unrelated shop prevented purchase");
            foreach (bool failStorage in new[] { false, true })
            {
                backend = Setup(failedInitialization: !failStorage, failedStorage: failStorage);
                result = Buy(new ShopPurchaseLedger(), player, Guid.NewGuid().ToString("N"), backend);
                Check(result.Quote.Status == ShopQuoteStatus.ServerError && result.FailurePhase == "commit" &&
                    !result.RecoveryRequired && Inventory.Instance.PlayerMoney == 500 && Inventory.Instance.Refunds == 1 &&
                    GlobalShopController.Instance.Data.purchasedItems == 0 && StorageController.Instance.Items.Count == 0 &&
                    GameObject.Clones.All(go => go.Destroyed), "Activation/storage failure not fully compensated");
            }
            backend = Setup();
            Resources.Prefab.Components.Remove(typeof(DV.CabControls.Spec.Item));
            result = Buy(new ShopPurchaseLedger(), player, Guid.NewGuid().ToString("N"), backend);
            Check(result.FailurePhase == "prepare" && Inventory.Instance.Debits == 0 && GameObject.Clones.Count == 0,
                "Invalid prefab charged before validation");
            Console.WriteLine("Shop backend lifecycle, replay, competing buyers and compensation checks passed (simulated native lifecycle).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
