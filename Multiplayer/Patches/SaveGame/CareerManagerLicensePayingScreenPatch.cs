using DV.ServicePenalty.UI;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data.RPCs;
using System;
using System.Collections;
using System.Collections.Generic;
using DV.InventorySystem;
using DV.Utils;
using UnityEngine;

namespace Multiplayer.Patches.SaveGame;

[HarmonyPatch(typeof(CareerManagerLicensePayingScreen), nameof(CareerManagerLicensePayingScreen.HandleInputAction))]
public static class CareerManagerLicensePayingScreenPatch
{
    private static readonly HashSet<CareerManagerLicensePayingScreen> pending = new();

    private static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
    {
        if (input != InputAction.Confirm || !NetworkLifecycle.Instance.IsClientRunning)
            return true;
        if (pending.Contains(__instance))
            return false;

        string id = __instance.IsJobLicense ? __instance.jobLicenseToBuy?.id : __instance.generalLicenseToBuy?.id;
        if (string.IsNullOrWhiteSpace(id)) return false;
        double price = __instance.IsJobLicense ? __instance.jobLicenseToBuy.price : __instance.generalLicenseToBuy.price;
        if (__instance.cashReg.DepositedCash + double.Epsilon < price)
        {
            __instance.cashReg.notEnoughMoneyAudio?.Play(__instance.cashReg.transform.position);
            return false;
        }

        // Deposited money was already removed locally. Return it to the shared wallet view;
        // the server will perform the single authoritative debit and return any remainder.
        Inventory.Instance.AddMoney(__instance.cashReg.DepositedCash);
        __instance.cashReg.SetCash(0);
        CoroutineManager.Instance.StartCoroutine(Purchase(__instance, id, __instance.IsJobLicense));
        return false;
    }

    private static IEnumerator Purchase(CareerManagerLicensePayingScreen screen, string id, bool isJob)
    {
        pending.Add(screen);
        bool finished = false;
        LicensePurchaseResponse result = null;
        try
        {
            var client = NetworkLifecycle.Instance.Client;
            var ticket = RpcManager.Instance.CreateTicket(client.RPC_Timeout)
                .OnResolve(response => { result = response as LicensePurchaseResponse; finished = true; })
                .OnTimeout(() => finished = true);
            client.SendLicensePurchaseRequest(ticket.TicketId, id, isJob);
            yield return new WaitUntil(() => finished);
            if (result != null && (result.Id != id || result.IsJobLicense != isJob))
            {
                Multiplayer.LogError("License purchase response did not match the request.");
            }
            else if (result?.Status == LicensePurchaseStatus.Success)
            {
                screen.licensePrinter?.Print(ignoreCooldown: true);
                screen.screenSwitcher.SetActiveDisplay(screen.licensesScreen);
            }
            else if (result == null)
                Multiplayer.LogWarning("License purchase outcome is unknown; confirm again to retry safely.");
            else
                Multiplayer.LogWarning($"License purchase rejected: {result.Status}");
        }
        finally { pending.Remove(screen); }
    }
}
