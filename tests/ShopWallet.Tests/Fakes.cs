// Minimal native surfaces: exercise the real Harmony patches without running Unity.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class Component
    {
        public readonly Dictionary<Type, object> Components = new();
        public T GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out var value) ? value as T : null;
        public T GetComponentInParent<T>() where T : class => GetComponent<T>();
    }
    public class Collider : Component { }
}
public class Wallet { }
namespace DV.InventorySystem { }
namespace DV.CabControls
{
    public class ItemBase { public bool Held; public bool IsGrabbed() => Held; }
}
namespace DV.Localization
{
    public static class LocalizationAPI
    {
        public static CultureInfo CC => CultureInfo.InvariantCulture;
        public static string L(string key) => key;
    }
}
namespace DV.Interaction
{
    public sealed class ItemUseTarget : UnityEngine.Component
    {
        public bool TryGetComponent<T>(out T value) where T : class { value = GetComponent<T>(); return value != null; }
    }
    public class MoneyUse : UnityEngine.Component
    {
        public int Spends;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HandleUse(ItemUseTarget target) { Spends++; return true; }
    }
}
namespace DV.CashRegister
{
    public class Text { public string text; }
    public class CashRegisterBase
    {
        public int Spends;
        public double DepositedCash;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnTriggerEnter(UnityEngine.Collider col) { Spends++; }
    }
    public class CashRegisterWithModules : CashRegisterBase
    {
        public Text cashText = new(), infoText = new();
        public bool IsProcessingTransaction;
        public float Units = 1;
        public float TotalUnitsInBasket() => Units;
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnUnitsToBuyChanged() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnDisable() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Cancel() { }
    }
    public class CashRegisterWithModulesTextController
    {
        private readonly CashRegisterWithModules cashRegister;
        public CashRegisterWithModulesTextController(CashRegisterWithModules register) { cashRegister = register; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateCashText() { cashRegister.cashText.text = "$0.00"; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateInfoText() { cashRegister.infoText.text = "native"; }
    }
}
namespace Multiplayer.Components.Networking.World
{
    public class NetworkedCashRegisterWithModules
    {
        public static readonly Dictionary<DV.CashRegister.CashRegisterWithModules, NetworkedCashRegisterWithModules> Registers = new();
        public static bool TryGet(DV.CashRegister.CashRegisterWithModules register, out NetworkedCashRegisterWithModules shop)
            => Registers.TryGetValue(register, out shop);
        public bool IsShopRegister = true, Accept = true;
        public double? ShopPaymentAmount;
        public int Presentations, Invalidations;
        public bool PresentShopWallet() { Presentations++; return Accept; }
        public void ClearShopPayment() { Invalidations++; ShopPaymentAmount = null; }
    }
}
