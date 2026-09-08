using System;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using DV.CabControls;
using DV.Shops;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Components.Networking.World;

/// <summary>Stages all Unity objects before charging the shared wallet.</summary>
public sealed class ShopPurchaseBackend : IShopPurchaseBackend
{
    private sealed class StagedItem
    {
        public ShopItemData Data;
        public GameObject Object;
        public bool Counted;
    }

    private readonly NetworkedCashRegisterWithModules register;
    private readonly ServerPlayer player;
    private readonly string[] ids;
    private readonly int[] quantities;
    private readonly List<StagedItem> staged = new();
    private GameObject stagingRoot;
    private Shop shop;
    private double charged;

    public ShopPurchaseBackend(NetworkedCashRegisterWithModules register, ServerPlayer player,
        string[] ids, int[] quantities)
    {
        this.register = register;
        this.player = player;
        this.ids = (string[])ids.Clone();
        this.quantities = (int[])quantities.Clone();
    }

    public ShopQuote Validate() => register == null
        ? new ShopQuote(ShopQuoteStatus.InvalidShop)
        : register.Server_QuoteShopCart(player, ids, quantities);

    public void Prepare()
    {
        shop = GlobalShopController.Instance.globalShopList.First(candidate =>
            NetworkedCashRegisterWithModules.TryGet(candidate.cashRegister, out var networked) && networked == register);
        if (shop.itemSpawnTransform == null)
            throw new InvalidOperationException("Shop delivery transform is missing.");
        stagingRoot = new GameObject("Multiplayer purchase staging");
        stagingRoot.SetActive(false);
        for (int line = 0; line < ids.Length; line++)
        {
            var data = GlobalShopController.Instance.GetShopItemData(ids[line]);
            var prefab = Resources.Load<GameObject>(ids[line]);
            if (data == null || prefab == null || prefab.GetComponent<ItemBase>() == null ||
                prefab.GetComponent<InventoryItemSpec>() == null || prefab.GetComponent<ShopRestocker>() == null)
                throw new InvalidOperationException("Shop item prefab is missing required components: " + ids[line]);
            for (int unit = 0; unit < quantities[line]; unit++)
            {
                var go = Object.Instantiate(prefab, stagingRoot.transform, false);
                staged.Add(new StagedItem { Data = data, Object = go });
                go.SetActive(false);
                go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
                go.GetComponent<ShopRestocker>().restockOnItemDestroyed = false;
            }
        }
    }

    public bool TryDebit(double total)
    {
        if (Inventory.Instance.PlayerMoney < total)
            return false;
        // RemoveMoney mutates the wallet before firing MoneyChanged. Account for a throwing subscriber.
        charged = total;
        if (Inventory.Instance.RemoveMoney(total))
            return true;
        charged = 0;
        return false;
    }

    public void Commit()
    {
        foreach (var item in staged)
        {
            item.Data.purchasedItems++;
            item.Counted = true;
            var go = item.Object;
            go.transform.SetParent(WorldMover.OriginShiftParent, true);
            go.transform.position = shop.itemSpawnTransform.position;
            go.transform.rotation = shop.itemSpawnTransform.rotation;
            go.SetActive(true);
            var itemBase = go.GetComponent<ItemBase>();
            StorageController.Instance.AddItemToWorldStorage(itemBase);
            go.GetComponent<ShopRestocker>().restockOnItemDestroyed = true;
            if (itemBase.ItemRigidbody != null)
            {
                itemBase.ItemRigidbody.velocity = Vector3.zero;
                itemBase.ItemRigidbody.angularVelocity = Vector3.zero;
            }
        }
        Object.Destroy(stagingRoot);
        stagingRoot = null;
    }

    public void Rollback()
    {
        var errors = new List<Exception>();
        foreach (var item in staged)
        {
            // Compensate counts explicitly; destruction must not restock a second time.
            if (item.Counted) { item.Data.purchasedItems--; item.Counted = false; }
            try
            {
                if (item.Object == null) continue;
                item.Object.GetComponent<ShopRestocker>().restockOnItemDestroyed = false;
                item.Object.SetActive(false);
                StorageController.Instance.RemoveItemFromStorageItemList(item.Object);
                Object.Destroy(item.Object);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        Object.Destroy(stagingRoot);
        stagingRoot = null;
        if (charged != 0)
        {
            double refund = charged;
            charged = 0;
            try { Inventory.Instance.AddMoney(refund); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public void NotifyCommitted()
    {
        // Run non-transactional game hooks only after the ledger has recorded success.
        foreach (var item in staged)
        {
            try
            {
                UnlockablesManager.Instance.UnlockItem(item.Data.item.ItemPrefabName);
                item.Object.GetComponent<APurchaseTrigger>()?.OnPurchased(item.Object);
            }
            catch (Exception ex) { Multiplayer.LogError("Purchase delivered, but its game hook failed: " + ex); }
        }
        try { GlobalShopController.Instance.Fire_GlobalShopDataChanged(); }
        catch (Exception ex) { Multiplayer.LogError("Purchase stock notification failed: " + ex); }
    }
}
