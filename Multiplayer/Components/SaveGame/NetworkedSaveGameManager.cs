using DV;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.Utils;
using DV.UserManagement;
using JetBrains.Annotations;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Multiplayer.Components.SaveGame;

public class NetworkedSaveGameManager : SingletonBehaviour<NetworkedSaveGameManager>
{
    private const string ROOT_KEY = "Multiplayer";
    private const string PLAYERS_KEY = "Players";

    private Action unsubscribe;

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        var inventory = Inventory.Instance;
        var licenses = LicenseManager.Instance;
        inventory.MoneyChanged += Server_OnMoneyChanged;
        licenses.LicenseAcquired += Server_OnLicenseAcquired;
        licenses.JobLicenseAcquired += Server_OnJobLicenseAcquired;
        licenses.GarageUnlocked += Server_OnGarageUnlocked;
        unsubscribe = () =>
        {
            if (inventory != null) inventory.MoneyChanged -= Server_OnMoneyChanged;
            if (licenses == null) return;
            licenses.LicenseAcquired -= Server_OnLicenseAcquired;
            licenses.JobLicenseAcquired -= Server_OnJobLicenseAcquired;
            licenses.GarageUnlocked -= Server_OnGarageUnlocked;
        };
    }

    protected override void OnDestroy()
    {
        try { unsubscribe?.Invoke(); }
        finally { unsubscribe = null; base.OnDestroy(); }
    }

    #region Server

    private static void Server_OnMoneyChanged(double oldAmount, double newAmount)
    {
        NetworkLifecycle.Instance.Server?.SendMoney((float)newAmount);
    }

    private static void Server_OnLicenseAcquired(GeneralLicenseType_v2 license)
    {
        NetworkLifecycle.Instance.Server?.SendLicense(license.id, false);
    }

    private static void Server_OnJobLicenseAcquired(JobLicenseType_v2 license)
    {
        NetworkLifecycle.Instance.Server?.SendLicense(license.id, true);
    }

    private static void Server_OnGarageUnlocked(GarageType_v2 garage)
    {
        NetworkLifecycle.Instance.Server?.SendGarage(garage.id);
    }

    public void Server_UpdateInternalData(SaveGameData data)
    {
        JObject root = (JObject)data.GetJObject(ROOT_KEY)?.DeepClone() ?? new JObject();
        JObject players = root.GetJObject(PLAYERS_KEY) ?? [];

        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer || player.LoadingState != PlayerLoadingState.Complete)
                continue;

            JObject playerData = (JObject)players.GetJObject(player.Guid.ToString())?.DeepClone() ?? new JObject();
            playerData.SetVector3(SaveGameKeys.Player_position, player.AbsoluteWorldPosition);
            playerData.SetFloat(SaveGameKeys.Player_rotation, player.WorldRotationY);
            if (player.InventoryRestoreComplete)
            {
                try { PlayerInventorySaveCodec.Write(playerData, NetworkedItemManager.Instance.CapturePlayerInventory(player)); }
                catch (Exception ex) { Multiplayer.LogError($"Inventory save retained for {player.Guid}: {ex}"); }
            }
            players.SetJObject(player.Guid.ToString(), playerData);
        }

        foreach (var inventory in NetworkedItemManager.Instance.CaptureOfflineInventories())
        {
            var profile = (JObject)players.GetJObject(inventory.Key.ToString())?.DeepClone() ?? new JObject();
            try { PlayerInventorySaveCodec.Write(profile, inventory.Value); }
            catch (Exception ex) { Multiplayer.LogError($"Offline inventory save retained for {inventory.Key}: {ex}"); continue; }
            players.SetJObject(inventory.Key.ToString(), profile);
        }
        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
    }

    public void Server_SavePlayerInventory(SaveGameData data, Guid guid, IEnumerable<PlayerItemSaveData> items)
    {
        if (!NetworkLifecycle.Instance.IsHost()) throw new InvalidOperationException("Only the server saves player inventories.");
        var root = (JObject)data.GetJObject(ROOT_KEY)?.DeepClone() ?? new JObject();
        var players = root.GetJObject(PLAYERS_KEY) ?? new JObject();
        var profile = players.GetJObject(guid.ToString()) ?? new JObject();
        PlayerInventorySaveCodec.Write(profile, items);
        players.SetJObject(guid.ToString(), profile);
        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
    }

    public JObject Server_GetPlayerData(SaveGameData data, Guid guid)
    {
        return data?.GetJObject(ROOT_KEY)?.GetJObject(PLAYERS_KEY)?.GetJObject(guid.ToString());
    }

    public JObject Server_PrepareStartingInventory(SaveGameData data, Guid guid)
    {
        if (!NetworkLifecycle.Instance.IsHost()) throw new InvalidOperationException("Only the server grants starting items.");
        var catalog = new List<PlayerItemSaveData>();
        // Catalog access also works while the host's StartingItemsController is still loading.
        int mode = data.GetInt("Starting_items") ?? 0;
        if (!Enum.IsDefined(typeof(GameParams.StartingItemsType), mode)) mode = 0;
        var starting = Globals.G.Items.GetStartingItemsAsset((GameParams.StartingItemsType)mode).GetStartingItems()
            ?? Globals.G.Items.GetStartingItemsAsset(GameParams.StartingItemsType.Basic).GetStartingItems();
        foreach (var item in starting)
            catalog.Add(new PlayerItemSaveData
            {
                ItemPrefabName = item.ItemPrefabName, BelongsToPlayer = true,
                InventorySlotIndex = item.preferredRelativeSlot - 1 + (item.backpackPriority ? 12 : 0),
                ContainerSlotIndex = -1, ItemRotationW = 1
            });
        var root = (JObject)data.GetJObject(ROOT_KEY)?.DeepClone() ?? new JObject();
        var players = root.GetJObject(PLAYERS_KEY) ?? new JObject();
        // Native starting items use 36 slots (12 belt + 24 backpack), including before Inventory initializes.
        var profile = StartingInventoryGrants.Prepare(players.GetJObject(guid.ToString()), catalog, 36);
        players.SetJObject(guid.ToString(), profile);
        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
        return profile;
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedSaveGameManager)}]";
    }
}
