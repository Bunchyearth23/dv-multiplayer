using DV.CashRegister;
using HarmonyLib;
using Multiplayer.Components.Networking.World;

namespace Multiplayer.Patches.World;
[HarmonyPatch(typeof(CashRegisterBase), "ReturnDepositedMoneyToWallet")]
internal static class IndividualCashRegisterRefundPatch
{
    private static bool Prefix(CashRegisterBase __instance) =>
        !(__instance is CashRegisterWithModules register &&
          NetworkedCashRegisterWithModules.TryGet(register, out var networked) && networked.ReturnIndividualDeposit());
}
