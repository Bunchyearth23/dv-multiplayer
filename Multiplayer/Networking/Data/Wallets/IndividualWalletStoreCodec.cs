using MPAPI.Types;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace Multiplayer.Networking.Data.Wallets;

public static class IndividualWalletStoreCodec
{
    public const string Key = "IndividualWallets";
    public static JObject Write(IndividualWalletLedger ledger)
    {
        var balances = new JObject();
        foreach (var pair in ledger.SnapshotBalances()) balances[pair.Key.ToString("N")] = pair.Value;
        var operations = new JArray();
        foreach (var op in ledger.SnapshotOperations()) operations.Add(new JObject { ["RequestId"] = op.RequestId.ToString("N"), ["Fingerprint"] = op.Fingerprint, ["Status"] = (byte)op.Status, ["Balance"] = op.Balance, ["CounterpartyBalance"] = op.CounterpartyBalance });
        return new JObject { ["Version"] = 1, ["Balances"] = balances, ["Operations"] = operations };
    }
    public static bool TryRead(JObject value, IndividualWalletLedger ledger)
    {
        if (ledger == null) return false;
        if (value == null) return ledger.TryReplace(Array.Empty<KeyValuePair<Guid, double>>(), Array.Empty<WalletOperationRecord>());
        if ((int?)value["Version"] != 1 || value["Balances"] is not JObject balances || value["Operations"] is not JArray operations) return false;
        var parsedBalances = new List<KeyValuePair<Guid, double>>(); var parsedOperations = new List<WalletOperationRecord>();
        foreach (var property in balances.Properties()) { if (!Guid.TryParseExact(property.Name, "N", out var id) || property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer) return false; parsedBalances.Add(new(id, (double)property.Value)); }
        foreach (var token in operations)
        {
            if (token is not JObject op || !Guid.TryParseExact((string)op["RequestId"], "N", out var id) || op["Fingerprint"]?.Type != JTokenType.String || op["Status"]?.Type != JTokenType.Integer || !Enum.IsDefined(typeof(IndividualWalletStatus), (byte)op["Status"]) || (op["Balance"]?.Type != JTokenType.Float && op["Balance"]?.Type != JTokenType.Integer) || (op["CounterpartyBalance"]?.Type != JTokenType.Float && op["CounterpartyBalance"]?.Type != JTokenType.Integer)) return false;
            parsedOperations.Add(new() { RequestId = id, Fingerprint = (string)op["Fingerprint"], Status = (IndividualWalletStatus)(byte)op["Status"], Balance = (double)op["Balance"], CounterpartyBalance = (double)op["CounterpartyBalance"] });
        }
        return ledger.TryReplace(parsedBalances, parsedOperations);
    }
}
