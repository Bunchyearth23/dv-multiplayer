// Native lifecycle fixture: serialized Item spec creates ItemBase only on activation.
using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Networking.Data.Items;

namespace UnityEngine
{
    public class Object
    {
        public static GameObject Instantiate(GameObject prefab, Transform parent, bool worldPositionStays)
        {
            var clone = new GameObject(prefab.Name) { FailInitialization = prefab.FailInitialization };
            clone.transform.SetParent(parent, false);
            foreach (var type in prefab.Components.Keys)
                clone.Components.Add(type, Activator.CreateInstance(type));
            GameObject.Clones.Add(clone);
            return clone;
        }
        public static void Destroy(GameObject go)
        {
            if (go == null) return;
            go.Destroyed = true;
            if (go.GetComponent<DV.Shops.ShopRestocker>()?.restockOnItemDestroyed == true)
                DV.Shops.GlobalShopController.Instance.Data.purchasedItems--;
        }
    }
    public class GameObject : Object
    {
        public static readonly List<GameObject> Clones = new();
        public readonly string Name;
        public readonly Dictionary<Type, object> Components = new();
        public readonly Transform transform;
        public bool Active = true, Destroyed, FailInitialization;
        public GameObject(string name) { Name = name; transform = new Transform(this); }
        public T GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out var value) ? value as T : null;
        public void SetActive(bool active)
        {
            Active = active;
            if (active && !FailInitialization && GetComponent<DV.CabControls.Spec.Item>() != null &&
                GetComponent<DV.CabControls.ItemBase>() == null)
            {
                if (transform.Parent?.Owner?.Active == false) throw new Exception("Activated before leaving staging");
                Components.Add(typeof(DV.CabControls.ItemBase), new DV.CabControls.ItemBase {
                    Object = this, InventorySpecs = GetComponent<InventoryItemSpec>() });
            }
        }
    }
    public class Transform
    {
        public readonly GameObject Owner;
        public Transform Parent;
        public Vector3 position;
        public Quaternion rotation;
        public Transform(GameObject owner = null) { Owner = owner; }
        public void SetParent(Transform parent, bool worldPositionStays) { Parent = parent; }
    }
    public struct Vector3 { public static Vector3 zero => default; }
    public struct Quaternion { }
    public class Rigidbody { public Vector3 velocity, angularVelocity; }
    public static class Resources
    {
        public static GameObject Prefab;
        public static T Load<T>(string name) where T : class => Prefab as T;
    }
}
public class InventoryItemSpec { public bool BelongsToPlayer; public string ItemPrefabName = "lamp"; }
public class Shop { public object cashRegister; public UnityEngine.Transform itemSpawnTransform = new(); }
public static class WorldMover { public static UnityEngine.Transform OriginShiftParent = new(); }
public class StorageController
{
    public static StorageController Instance = new();
    public readonly HashSet<UnityEngine.GameObject> Items = new();
    public bool FailAdd;
    public void AddItemToWorldStorage(DV.CabControls.ItemBase item)
    {
        if (item == null || item.InventorySpecs == null) throw new Exception("Uninitialized item");
        Items.Add(item.Object);
        if (FailAdd) throw new Exception("Storage failure");
    }
    public void RemoveItemFromStorageItemList(UnityEngine.GameObject item) { Items.Remove(item); }
}
public class UnlockablesManager
{
    public static UnlockablesManager Instance = new();
    public void UnlockItem(string id) { }
}
namespace DV.CabControls.Spec { public class Item { } }
namespace DV.CabControls
{
    public class ItemBase
    {
        public UnityEngine.GameObject Object;
        public InventoryItemSpec InventorySpecs;
        public UnityEngine.Rigidbody ItemRigidbody = new();
    }
}
namespace DV.InventorySystem
{
    public class Inventory
    {
        public static Inventory Instance = new();
        public double PlayerMoney = 500;
        public int Debits, Refunds;
        public bool RemoveMoney(double total)
        {
            if (PlayerMoney < total) return false;
            PlayerMoney -= total; Debits++; return true;
        }
        public void AddMoney(double total) { PlayerMoney += total; Refunds++; }
    }
}
namespace DV.Shops
{
    public class ShopRestocker { public bool restockOnItemDestroyed; }
    public class APurchaseTrigger { public void OnPurchased(UnityEngine.GameObject go) { } }
    public class ShopItemData
    {
        public InventoryItemSpec item = new();
        public int purchasedItems, Stock = 1;
    }
    public class GlobalShopController
    {
        public static GlobalShopController Instance = new();
        public readonly List<Shop> globalShopList = new();
        public ShopItemData Data = new();
        public ShopItemData GetShopItemData(string id) => Data;
        public void Fire_GlobalShopDataChanged() { }
    }
}
namespace Multiplayer
{
    public static class Multiplayer { public static void LogError(string message) { } }
}
namespace Multiplayer.Networking.Data { public class ServerPlayer { } }
namespace Multiplayer.Components.Networking.World
{
    public class NetworkedCashRegisterWithModules
    {
        public static NetworkedCashRegisterWithModules Register;
        public static bool TryGet(object register, out NetworkedCashRegisterWithModules result)
        { result = Register; return register != null; }
        public ShopQuote Server_QuoteShopCart(global::Multiplayer.Networking.Data.ServerPlayer player, string[] ids, int[] quantities)
            => ShopCartPolicy.Quote(ids, quantities, DV.InventorySystem.Inventory.Instance.PlayerMoney,
                _ => new ShopOffer(100, DV.Shops.GlobalShopController.Instance.Data.Stock - DV.Shops.GlobalShopController.Instance.Data.purchasedItems));
    }
}

namespace Multiplayer.Networking.Data.Wallets
{
    public static class PlayerWallet
    {
        public static double Read(global::Multiplayer.Networking.Data.ServerPlayer player) => DV.InventorySystem.Inventory.Instance.PlayerMoney;
        public static bool TryDebit(global::Multiplayer.Networking.Data.ServerPlayer player, double amount) => DV.InventorySystem.Inventory.Instance.RemoveMoney(amount);
        public static void Credit(global::Multiplayer.Networking.Data.ServerPlayer player, double amount) => DV.InventorySystem.Inventory.Instance.AddMoney(amount);
    }
}
