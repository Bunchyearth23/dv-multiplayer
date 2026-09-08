using DV;
using DV.Common;
using DV.Customization.Paint;
using DV.Damage;
using DV.InventorySystem;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.MultipleUnit;
using DV.ServicePenalty.UI;
using DV.ThingTypes;
using DV.UI;
using DV.UserManagement;
using DV.WeatherSystem;
using LiteNetLib;
using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.API;
using Multiplayer.Components.MainMenu;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.UI;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Clientbound.World;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.Packets.Serverbound.Jobs;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Patches.MainMenu;
using Multiplayer.Patches.SaveGame;
using Multiplayer.Patches.World;
using Multiplayer.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Networking.Managers.Client;

public partial class NetworkClient : NetworkManager
{
    protected override string LogPrefix => "[Client]";

    private Action<DisconnectReason, string> onDisconnect;
    private string disconnectMessage;

    private ITransportPeer selfPeer;
    public byte PlayerId { get; private set; }
    public string Username { get; private set; }
    private string characterModelId;
    public string CrewName { get; private set; }
    public string DisplayName => string.IsNullOrEmpty(CrewName) ? Username : $"[{CrewName}] {Username}";

    public readonly ClientPlayerManager ClientPlayerManager;
    public readonly Dictionary<byte, ClientPlayerWrapper> PlayerWrapperCache = [];
    public IReadOnlyCollection<ClientPlayerWrapper> ClientPlayerWrappers => PlayerWrapperCache.Values;

    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    internal uint trainSetsToSpawn = uint.MaxValue;
    internal uint trainSetsSpawned = 0;
    internal bool railwayStateLoaded = false;

    // One way ping in milliseconds
    public int Ping { get; private set; }
    private ITransportPeer serverPeer;
    public float RPC_Timeout => Mathf.Max(2f, (Ping * 8f) / 1000);

    private ChatGUI chatGUI;
    private readonly bool isSinglePlayer;

    private bool isAlsoHost;
    IGameSession originalSession;

    // Allow mods to add to the wait Queue
    private readonly List<string> readyBlocks = [];
    private Action beginWorldSync;
    private GuardedRoutine worldSyncRoutine;
    private bool stopped;
    private bool disconnectHandled;
    private LoadingManifest initialJobs;
    private bool inventoryRestored;
    private LoadingManifest initialWorldItems;

    public NetworkClient(Settings settings, bool singlePlayer) : base(settings)
    {
        Log($"Client created for {(singlePlayer ? "single player" : "multiplayer")} game");
        isSinglePlayer = singlePlayer;
        ClientPlayerManager = new ClientPlayerManager();

        WorldStreamingInit.LoadingFinished += NetworkedPlayer.CaptureItemAnchorOffset;

        Username = Multiplayer.Settings.GetUserName();
        characterModelId = Multiplayer.Settings.CharacterId;

        Settings.OnSettingsUpdated += OnSettingsUpdated;
    }

    public void Start(string address, int port, string password, bool isSinglePlayer, Action<DisconnectReason, string> onDisconnect)
    {
        LogDebug(() => $"NetworkClient Constructor");

        this.onDisconnect = onDisconnect;
        //netManager.Start();
        base.Start();

        ServerboundClientLoginPacket serverboundClientLoginPacket = new()
        {
            Username = this.Username,
            Guid = Multiplayer.Settings.GetGuid().ToByteArray(),
            Password = password,
            BuildVersion = ProtocolCompatibility.HandshakeBuild(MainMenuControllerPatch.MenuProvider.BuildVersionString),
            Mods = ModCompatibilityManager.Instance.GetLocalMods(),
            CharacterId = Multiplayer.Settings.CharacterId,
            IsVR = VRManager.IsVREnabled()
        };

        Log("Sending Login Packet");
        netPacketProcessor.Write(cachedWriter, serverboundClientLoginPacket);
        selfPeer = Connect(address, port, cachedWriter);

        isAlsoHost = NetworkLifecycle.Instance.IsServerRunning;
        originalSession = UserManager.Instance.CurrentUser.CurrentSession;

        LogDebug(() => $"NetworkClient.Start() isAlsoHost: {isAlsoHost}, Original session is Null: {originalSession == null}");
    }

    public override void Stop()
    {
        if (stopped) return;
        stopped = true;
        arrivalRoutine?.Dispose();
        arrivalRoutine = null;
        pendingTravel = null;
        worldSyncRoutine?.Dispose();
        worldSyncRoutine = null;
        readyBlocks.Clear();
        Log("Stopping client");
        Settings.OnSettingsUpdated -= OnSettingsUpdated;
        WorldStreamingInit.LoadingFinished -= NetworkedPlayer.CaptureItemAnchorOffset;
        if (beginWorldSync != null)
        {
            WorldStreamingInit.LoadingFinished -= beginWorldSync;
            beginWorldSync = null;
        }
        try
        {
            if (!isAlsoHost && originalSession != null)
                Client_GameSession.SetCurrent(originalSession);
        }
        finally { base.Stop(); }
    }

    private void OnSettingsUpdated(Settings settings)
    {
        Dictionary<PlayerPreference, string> preferences = [];

        if (settings.CharacterId != characterModelId)
        {
            characterModelId = settings.CharacterId;
            preferences[PlayerPreference.CharacterModel] = characterModelId;
        }

        if (preferences.Count > 0)
            SendPlayerPreferences(preferences);
    }

    protected override void Subscribe()
    {
        // Login, Connection & Initial Sync
        netPacketProcessor.SubscribeReusable<ClientboundServerLoadingPacket>(OnClientboundServerLoadingPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLoginResponsePacket>(OnClientboundLoginResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundDisconnectPacket>(OnClientboundDisconnectPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPingUpdatePacket>(OnClientboundPingUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundLoadStateInfoPacket>(OnClientboundLoadStateInfoPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRemoveLoadingScreenPacket>(OnClientboundRemoveLoadingScreen);

        netPacketProcessor.SubscribeReusable<ClientboundGameParamsPacket>(OnClientboundGameParamsPacket);
        netPacketProcessor.SubscribeReusable<ClientboundSaveGameDataPacket>(OnClientboundSaveGameDataPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRailwayStatePacket>(OnClientboundRailwayStatePacket);


        // General Sync
        netPacketProcessor.SubscribeNetSerializable<ClientboundRpcResponsePacket>(OnClientboundRpcResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundTickSyncPacket>(OnClientboundTickSyncPacket);
        netPacketProcessor.SubscribeReusable<ClientboundWeatherPacket>(OnClientboundWeatherPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTimeAdvancePacket>(OnClientboundTimeAdvancePacket);
        netPacketProcessor.SubscribeReusable<CommonChangeJunctionPacket>(OnCommonChangeJunctionPacket);
        netPacketProcessor.SubscribeReusable<CommonRotateTurntablePacket>(OnCommonRotateTurntablePacket);


        // Player Management
        netPacketProcessor.SubscribeReusable<ClientboundPlayerJoinedPacket>(OnClientboundPlayerJoinedPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPlayerDisconnectPacket>(OnClientboundPlayerDisconnectPacket);

        netPacketProcessor.SubscribeReusable<ClientboundPlayerPositionPacket>(OnClientboundPlayerPositionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPlayerPreferencesUpdatePacket>(OnClientboundPlayerPreferencesUpdatePacket);


        // Train Sync
        netPacketProcessor.SubscribeReusable<ClientboundSpawnTrainSetPacket>(OnClientboundSpawnTrainSetPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainRepairPacket>(p =>
        {
            if (NetworkLifecycle.Instance.IsHost()) return;
            try { NetworkedCarSpawner.RepairCars(p.Parts, p.Relocate, p.Tick); }
            catch (Exception ex) { FailWorldSync(ex); }
        });
        netPacketProcessor.SubscribeReusable<ClientboundFastTravelPacket>(OnFastTravelResponse);
        netPacketProcessor.SubscribeReusable<ClientboundDestroyTrainCarPacket>(OnClientboundDestroyTrainCarPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRerailTrainPacket>(OnClientboundRerailTrainPacket);
        netPacketProcessor.SubscribeReusable<ClientboundMoveTrainPacket>(OnClientboundMoveTrainPacket);

        netPacketProcessor.SubscribeReusable<ClientboundTrainsetPhysicsPacket>(OnClientboundTrainPhysicsPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainPortsPacket>(OnCommonSimFlowPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainFusesPacket>(OnCommonTrainFusesPacket);
        netPacketProcessor.SubscribeReusable<CommonBrakeCylinderReleasePacket>(OnCommonBrakeCylinderReleasePacket);
        netPacketProcessor.SubscribeReusable<CommonHandbrakePositionPacket>(OnCommonHandbrakePositionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundBrakeStateUpdatePacket>(OnClientboundBrakeStateUpdatePacket);

        netPacketProcessor.SubscribeReusable<CommonCouplerInteractionPacket>(OnCommonCouplerInteractionPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainUncouplePacket>(OnCommonTrainUncouplePacket);
        netPacketProcessor.SubscribeReusable<CommonHoseConnectedPacket>(OnCommonHoseConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonHoseDisconnectedPacket>(OnCommonHoseDisconnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonCockFiddlePacket>(OnCommonCockFiddlePacket);

        netPacketProcessor.SubscribeReusable<CommonMuConnectedPacket>(OnCommonMuConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonMuDisconnectedPacket>(OnCommonMuDisconnectedPacket);

        netPacketProcessor.SubscribeReusable<CommonPaintThemePacket>(OnCommonPaintThemePacket);
        netPacketProcessor.SubscribeReusable<ClientboundRestorationStateChangePacket>(OnClientboundRestorationStateChangePacket);

        netPacketProcessor.SubscribeReusable<ClientboundTrainControlAuthorityUpdatePacket>(OnClientboundTrainControlAuthorityUpdatePacket);

        netPacketProcessor.SubscribeReusable<ClientboundCargoStatePacket>(OnClientboundCargoStatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundCargoHealthUpdatePacket>(OnClientboundCargoHealthUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundCarHealthUpdatePacket>(OnClientboundCarHealthUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundWarehouseControllerUpdatePacket>(OnClientboundWarehouseControllerUpdatePacket);

        netPacketProcessor.SubscribeReusable<ClientboundWindowsBrokenPacket>(OnClientboundWindowsBrokenPacket);
        netPacketProcessor.SubscribeReusable<ClientboundWindowsRepairedPacket>(OnClientboundWindowsRepairedPacket);
        netPacketProcessor.SubscribeReusable<ClientboundMoneyPacket>(OnClientboundMoneyPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLicenseAcquiredPacket>(OnClientboundLicenseAcquiredPacket);
        netPacketProcessor.SubscribeReusable<ClientboundGarageUnlockPacket>(OnClientboundGarageUnlockPacket);

        // Job Sync
        netPacketProcessor.SubscribeReusable<ClientboundDebtStatusPacket>(OnClientboundDebtStatusPacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobsUpdatePacket>(OnClientboundJobsUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundInventoryRestorePacket>(OnInventoryRestore);
        netPacketProcessor.SubscribeReusable<ClientboundWorldItemManifestPacket>(packet =>
        {
            if (stopped || LoadingState != PlayerLoadingState.ReadyForItems || initialWorldItems == null) return;
            try { initialWorldItems.SetExpected(packet.ItemIds); }
            catch (Exception ex) { FailWorldSync(ex); }
        });
        netPacketProcessor.SubscribeReusable<ClientboundJobsCreatePacket>(OnClientboundJobsCreatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobManifestPacket>(packet =>
        {
            if (LoadingState != PlayerLoadingState.ReadyForJobs || initialJobs == null || stopped) return;
            try { initialJobs.SetExpected(packet.JobIds); }
            catch (Exception ex) { FailWorldSync(ex); }
        });
        netPacketProcessor.SubscribeReusable<ClientboundJobValidateResponsePacket>(OnClientboundJobValidateResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundTaskUpdatePacket>(OnClientboundTaskUpdatePacket);

        // World Sync
        netPacketProcessor.SubscribeNetSerializable<CommonItemChangePacket>(OnCommonItemChangePacket);
        netPacketProcessor.SubscribeReusable<CommonPitStopInteractionPacket>(OnCommonPitStopInteractionPacket);
        netPacketProcessor.SubscribeNetSerializable<CommonPitStopPlugInteractionPacket>(OnCommonPitStopPlugInteractionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPitStopBulkUpdatePacket>(OnClientboundPitStopBulkUpdatePacket);
        netPacketProcessor.SubscribeReusable<CommonCashRegisterWithModulesActionPacket>(OnCommonCashRegisterWithModulesActionPacket);
        netPacketProcessor.SubscribeReusable<CommonGenericSwitchStatePacket>(OnCommonGenericSwitchStatePacket);

        netPacketProcessor.SubscribeReusable<CommonChatPacket>(OnCommonChatPacket);
    }

    // Allow mods to register their own packets
    public void RegisterExternalPacket<T>(ClientPacketHandler<T> handler) where T : class, IPacket, new()
    {
        netPacketProcessor.SubscribeReusable<T>((packet) =>
        {
            handler(packet);
        });
    }

    public void RegisterExternalSerializablePacket<T>(ClientPacketHandler<T> handler) where T : class, ISerializablePacket, new()
    {
        netPacketProcessor.SubscribeNetSerializable<ExternalSerializablePacketWrapper<T>>((wrapper) =>
        {
            handler(wrapper.Packet);
        },
        () => new ExternalSerializablePacketWrapper<T>()
        );
    }

    // Allow mods to register ready blocks
    internal void RegisterReadyBlock(string modName)
    {
        Log($"Ready Block has been registered by {modName}");

        if (readyBlocks.Contains(modName))
            return;

        readyBlocks.Add(modName);
    }

    internal void CancelReadyBlock(string modName)
    {
        Log($"Ready Block has been cleared by {modName}");

        if (readyBlocks.Contains(modName))
        {
            readyBlocks.Remove(modName);
            DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
            displayLoadingInfo?.OnLoadingStatusChanged($"Mod {modName} loaded", false, 100);
        }
    }

    private IEnumerator SyncWorldState()
    {
        /*
         * This coroutine must not be started prior to WorldStreamingInit.LoadingFinished, otherwise it will be killed
         * Both GameSettings and SaveGameData have been applied at this point and the current loading state is PlayerLoadingState.ReadyForGameData
         */

        Log($"World loaded beginning sync");
        var inventoryDeadline = new LoadingDeadline("native inventory restoration", 120, 300);
        while (StartingItemsController.Instance == null || !StartingItemsController.Instance.itemsLoaded)
        {
            inventoryDeadline.Check("waiting for StartingItemsController");
            yield return null;
        }

        Log($"Starting Item Manager...");
        NetworkedItemManager.Instance.CheckInstance();
        Log($"Caching World Items...");
        NetworkedItemManager.Instance.CacheWorldItems();
        Log($"Initialising Cash Registers...");
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();
        Log($"Initialising Pit Stops...");
        NetworkedPitStopStation.InitialisePitStops();

        // Wait for ready blocks to clear
        Log($"Waiting for ready-blocks...");
        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        foreach (string modName in readyBlocks)
            displayLoadingInfo?.OnLoadingStatusChanged($"Waiting for mod {modName} to load", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        var deadline = new LoadingDeadline("mod readiness", 120, 600);
        while (readyBlocks.Count > 0)
        {
            deadline.Check(string.Join(", ", readyBlocks));
            yield return null;
        }

        /* 
         * ReadyForWorldState
         * Request world state data (tracks and turntables)
         */

        Log("Syncing world state");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForWorldState);
        displayLoadingInfo?.OnLoadingStatusChanged(Locale.LOADING_INFO__SYNC_WORLD_STATE, false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        deadline = new LoadingDeadline("railway state", 60, 180);
        while (!railwayStateLoaded)
        {
            deadline.Check("waiting for tracks and turntables");
            yield return null;
        }

        /*
         * ReadyForTrainSets
         * Trainsets have been requested
         */

        Log("Requesting cars");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForTrainSets);
        displayLoadingInfo?.OnLoadingStatusChanged("Syncing rolling stock", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        uint lastLoggedSets = 0;

        // Wait for trainset count from server
        deadline = new LoadingDeadline("trainset manifest", 60, 180);
        while (trainSetsToSpawn == uint.MaxValue)
        {
            deadline.Check("waiting for server trainset count");
            yield return null;
        }

        // Wait for all Trainsets to be spawned
        deadline = new LoadingDeadline("trainset spawning", 120, 900);
        while (trainSetsSpawned < trainSetsToSpawn)
        {
            deadline.Check($"{trainSetsSpawned}/{trainSetsToSpawn} trainsets spawned");
            if (lastLoggedSets != trainSetsSpawned)
            {
                Log($"Waiting for train sets to spawn... {trainSetsSpawned}/{trainSetsToSpawn}");
                lastLoggedSets = trainSetsSpawned;
            }

            yield return null;
        }

        // Artificial delay to allow cargo to be loaded prior to applying restoration states
        yield return new WaitForSecondsRealtime(0.5f);

        // Trainsets spawned, apply restoration states for demonstrators
        NetworkedCarSpawner.ApplyRestorationStates();

        /*
         * ReadyForCustomizers
         */

        //TODO: implement
        yield return new WaitForSecondsRealtime(0.25f);

        /* 
         * ReadyForItems
         */

        Log($"Train sets spawned, requesting items");
        inventoryRestored = false;
        initialWorldItems = new LoadingManifest();
        var itemRecovery = new LoadingRecovery();
        SendLoadStateUpdate(PlayerLoadingState.ReadyForItems);
        deadline = new LoadingDeadline("authoritative inventory restoration", 120, 300);
        while (!inventoryRestored || !initialWorldItems.IsComplete)
        {
            deadline.Check(!inventoryRestored ? "waiting for inventory bindings from server" :
                !initialWorldItems.HasManifest ? "waiting for world item manifest" :
                $"missing world item IDs: {string.Join(", ", initialWorldItems.Missing)}");
            if (initialWorldItems.HasManifest && itemRecovery.TryTake(initialWorldItems.Missing, out var missingItems))
                SendPacket(serverPeer, new ServerboundWorldItemRecoveryPacket { ItemIds = missingItems }, DeliveryMethod.ReliableOrdered);
            yield return null;
        }

        /* 
         * ReadyForJobs
         */

        Log($"Requesting jobs");
        initialJobs = new LoadingManifest();
        SendLoadStateUpdate(PlayerLoadingState.ReadyForJobs);
        deadline = new LoadingDeadline("job initialization", 120, 600);
        while (!initialJobs.IsComplete)
        {
            deadline.Check(initialJobs.HasManifest
                ? $"missing job IDs: {string.Join(", ", initialJobs.Missing)}"
                : "waiting for server job manifest");
            yield return null;
        }


        /* 
         * ReadyForTiles
         */

        Log($"Requesting Hazmat Tiles");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForTiles);

        yield return new WaitForSecondsRealtime(0.5f);

        SendLoadStateUpdate(PlayerLoadingState.Complete);
        displayLoadingInfo?.OnLoadingStatusChanged("Complete", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);
        yield return new WaitForSecondsRealtime(0.25f);

        // Start culling player models
        ClientPlayerManager.StartCulling();
    }

    private void OnInventoryRestore(ClientboundInventoryRestorePacket packet)
    {
        if (stopped || LoadingState != PlayerLoadingState.ReadyForItems || inventoryRestored) return;
        try
        {
            if (!string.IsNullOrEmpty(packet.Error)) throw new InvalidOperationException(packet.Error);
            if (packet.Items == null || packet.Items.Length > PlayerInventorySaveCodec.MaxItems)
                throw new FormatException("Invalid inventory restoration packet.");
            var bindings = new List<(NetworkedItem Item, PlayerItemSaveData Data)>();
            var networkIds = new HashSet<ushort>();
            var persistentIds = new HashSet<Guid>();
            foreach (var saved in packet.Items)
            {
                var identity = PlayerInventorySaveCodec.Identity(saved.State);
                var item = NetworkedItem.FindPersistentItem(identity);
                if (saved.NetId == 0 || !networkIds.Add(saved.NetId) || !persistentIds.Add(identity) ||
                    item == null || !item.RegistrationComplete || item.Item?.InventorySpecs?.ItemPrefabName != saved.ItemPrefabName)
                    throw new InvalidOperationException($"Restored inventory object is missing or duplicated: {saved.ItemPrefabName}, {identity}");
                if (NetworkedItem.TryGet(saved.NetId, out var existing) && existing != item)
                    throw new InvalidOperationException($"Inventory network ID {saved.NetId} is already assigned.");
                bindings.Add((item, saved));
            }
            foreach (var binding in bindings)
            {
                binding.Item.NetId = binding.Data.NetId;
                var states = new Dictionary<string, object>
                {
                    [PlayerInventorySaveCodec.IdentityKey] = binding.Item.PersistentId.ToString("N")
                };
                new ItemInventoryLocation(binding.Data.InventorySlotIndex, binding.Data.ContainerId,
                    binding.Data.ContainerSlotIndex, binding.Data.InLockedSlot).Write(states);
                binding.Item.ReceiveSnapshot(new ItemUpdateData
                {
                    ItemNetId = binding.Data.NetId, UpdateType = ItemUpdateData.ItemUpdateType.FullSync,
                    ItemState = ItemState.InInventory, Player = PlayerId, States = states
                });
            }
            inventoryRestored = true;
        }
        catch (Exception ex) { FailWorldSync(ex); }
    }

    public void NotifyInitialJobApplied(ushort jobId)
    {
        if (!stopped && LoadingState == PlayerLoadingState.ReadyForJobs)
            initialJobs?.MarkApplied(jobId);
    }

    public void NotifyInitialWorldItemApplied(ushort itemId)
    {
        if (!stopped && LoadingState == PlayerLoadingState.ReadyForItems)
            initialWorldItems?.MarkApplied(itemId);
    }

    internal void FailWorldSync(Exception error)
    {
        disconnectMessage = error.Message;
        LogError($"World synchronization failed: {error}");
        OnPeerDisconnected(selfPeer, error is TimeoutException ? DisconnectReason.Timeout : DisconnectReason.ConnectionFailed);
    }

    public ClientPlayerWrapper GetWrapper(NetworkedPlayer networkedPlayer)
    {
        if (!PlayerWrapperCache.TryGetValue(networkedPlayer.PlayerId, out var wrapper))
        {
            wrapper = new ClientPlayerWrapper(networkedPlayer);
            PlayerWrapperCache[networkedPlayer.PlayerId] = wrapper;
        }
        return wrapper;
    }

    #region Net Events

    public override void OnPeerConnected(ITransportPeer peer)
    {
        serverPeer = peer;
    }

    public override void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectReason)
    {

        if (disconnectHandled) return;
        disconnectHandled = true;
        LogDebug(() => $"OnPeerDisconnected({peer?.Id}, {disconnectReason}) disconnect message: {disconnectMessage}");

        NetworkLifecycle.Instance.Stop();

        TrainStress.globalIgnoreStressCalculation = false;

        if (MainMenuThingsAndStuff.Instance != null)
        {
            //MainMenuThingsAndStuff.Instance.SwitchToDefaultMenu();
            LogDebug(() => $"OnPeerDisconnected() queuing GoBackToMainMenu via NetworkLifecycle");
            NetworkLifecycle.Instance.TriggerMainMenuEventLater();
        }
        else
        {
            LogDebug(() => $"OnPeerDisconnected() {nameof(MainMenuThingsAndStuff.Instance)} is null, calling GoBackToMainMenu directly");
            MainMenu.GoBackToMainMenu();
            NetworkLifecycle.Instance.TriggerMainMenuEventLater();
        }

        LogDebug(() => $"OnPeerDisconnected() calling onDisconnect({disconnectReason}, {disconnectMessage})");
        onDisconnect?.Invoke(disconnectReason, disconnectMessage);
    }

    public override void OnNetworkLatencyUpdate(ITransportPeer peer, int latency)
    {
        Ping = latency;

        if (latency > LATENCY_FLAG)
            LogWarning($"High Ping Detected! {latency}ms");
    }

    public override void OnConnectionRequest(NetDataReader dataReader, IConnectionRequest request)
    {
        // Clients don't receive incomming requests.
        request.Reject();
    }

    #endregion

    #region Listeners

    private void OnClientboundLoginResponsePacket(ClientboundLoginResponsePacket packet)
    {
        if (packet.Accepted)
        {
            Log($"Player accepted");
            PlayerId = packet.PlayerId;

            if (!string.IsNullOrEmpty(packet.OverrideUsername))
            {
                Log($"A player with username '{Username}' already exists, your temporary username is '{packet.OverrideUsername}'");
                Username = packet.OverrideUsername;
            }

            if (NetworkLifecycle.Instance.IsHost())
            {
                // Host can skip straight to world state sync as they already have the world loaded
                SendLoadStateUpdate(PlayerLoadingState.ReadyForWorldState);
                return;
            }

            // Request Game Params and Save Game Data
            SendLoadStateUpdate(PlayerLoadingState.ReadyForGameData);

            if (beginWorldSync != null)
                WorldStreamingInit.LoadingFinished -= beginWorldSync;
            beginWorldSync = () =>
            {
                WorldStreamingInit.LoadingFinished -= beginWorldSync;
                beginWorldSync = null;
                LogDebug(() => "Loading finished, beginning sync");
                if (stopped) return;
                worldSyncRoutine?.Dispose();
                worldSyncRoutine = new GuardedRoutine(SyncWorldState(), FailWorldSync);
                CoroutineManager.Instance.StartCoroutine(worldSyncRoutine);
                if (stopped) return;

                if (WorldMover.Instance.playerTracker != null)
                {
                    var tracker = WorldMover.Instance.playerTracker.GetComponent<LocalPlayerTrackerBase>();
                    if (tracker == null)
                    {
                        LogWarning($"LocalPlayerTracker not found, adding {(VRManager.IsVREnabled() ? "" : "non")}VR tracker.");
                        if (VRManager.IsVREnabled())
                            tracker = WorldMover.Instance.playerTracker.gameObject.AddComponent<LocalPlayerTrackerVR>();
                        else
                            tracker = WorldMover.Instance.playerTracker.gameObject.AddComponent<LocalPlayerTrackerNonVR>();
                    }
                }
            };
            WorldStreamingInit.LoadingFinished += beginWorldSync;

            return;
        }

        string text = Locale.Get(packet.ReasonKey, packet.ReasonArgs);

        if (packet.Missing.Length != 0 || packet.Extra.Length != 0)
        {
            text += "\n\n";

            if (packet.Missing.Length != 0)
            {
                text += Locale.Get(Locale.DISCONN_REASON__MODS_MISSING_KEY, placeholders: string.Join("\n - ", packet.Missing));
            }

            if (packet.Extra.Length != 0)
            {
                if (packet.Missing.Length != 0)
                    text += "\n";
                text += Locale.Get(Locale.DISCONN_REASON__MODS_EXTRA_KEY, placeholders: string.Join("\n - ", packet.Extra));
            }
        }

        Log($"Player denied: {text}");
        onDisconnect(DisconnectReason.ConnectionRejected, text);
    }

    private void OnClientboundLoadStateInfoPacket(ClientboundLoadStateInfoPacket packet)
    {
        Log($"Received load state info for loading state: {packet.LoadingState}, item count: {packet.ItemsToLoad}");
        switch (packet.LoadingState)
        {
            case PlayerLoadingState.ReadyForTrainSets:
                trainSetsToSpawn = packet.ItemsToLoad;
                break;
            default:
                LogWarning($"Unexpected loading state: {packet.LoadingState}");
                break;
        }
    }

    private void OnClientboundRpcResponsePacket(ClientboundRpcResponsePacket packet)
    {
        LogDebug(() => $"Received RPC response for ticket: {packet.TicketId}, response type: {packet.ResponseType}");
        RpcManager.Instance.ResolveTicket(packet.TicketId, packet.Response);
    }

    private void OnClientboundPlayerJoinedPacket(ClientboundPlayerJoinedPacket packet)
    {
        Log($"Received player joined packet for player id: {packet.PlayerId}, username: {packet.Username}");
        ClientPlayerManager.AddPlayer(packet.PlayerId, packet.Username, packet.CrewName, packet.CharacterId, packet.IsVR);

        ClientPlayerManager.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    //For other player left the game
    private void OnClientboundPlayerDisconnectPacket(ClientboundPlayerDisconnectPacket packet)
    {
        Log($"Received player disconnect packet for player id: {packet.PlayerId}");
        ClientPlayerManager.RemovePlayer(packet.PlayerId);
    }

    //For server shutting down / player kicked
    private void OnClientboundDisconnectPacket(ClientboundDisconnectPacket packet)
    {
        if (packet.Kicked)
        {
            Log($"Player was kicked!");
            disconnectMessage = "You were kicked!";
        }
        else
        {
            Log($"Server Shutting Down");
            disconnectMessage = "Server Shutting Down";
        }
    }

    private void OnClientboundPlayerPositionPacket(ClientboundPlayerPositionPacket packet)
    {
        ClientPlayerManager.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    private void OnClientboundPlayerPreferencesUpdatePacket(ClientboundPlayerPreferencesUpdatePacket packet)
    {
        Log($"Received player preferences update for '{packet.PlayerId}'");

        if (packet.PlayerId == PlayerId)
        {
            if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CrewName, out string crewName))
                CrewName = crewName;
        }

        ClientPlayerManager.UpdatePreferences(packet.PlayerId, packet.GetPreferencesDictionary());
    }

    private void OnClientboundPingUpdatePacket(ClientboundPingUpdatePacket packet)
    {
        ClientPlayerManager.UpdatePing(packet.PlayerId, packet.Ping);
    }

    private void OnClientboundTickSyncPacket(ClientboundTickSyncPacket packet)
    {
        NetworkLifecycle.Instance.Tick = (uint)(packet.ServerTick + Ping / 2.0f * (1f / NetworkLifecycle.TICK_RATE));
    }

    private void OnClientboundServerLoadingPacket(ClientboundServerLoadingPacket packet)
    {
        Log("Waiting for server to load");

        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        if (displayLoadingInfo == null)
        {
            LogDebug(() => $"Received {nameof(ClientboundServerLoadingPacket)} but couldn't find {nameof(DisplayLoadingInfo)}!");
            return;
        }

        displayLoadingInfo.OnLoadingStatusChanged(Locale.LOADING_INFO__WAIT_FOR_SERVER, false, 100);
    }

    private void OnClientboundGameParamsPacket(ClientboundGameParamsPacket packet)
    {
        LogDebug(() => $"Received {nameof(ClientboundGameParamsPacket)} ({packet.SerializedGameParams.Length} chars)");
        if (Globals.G.GameParams != null)
            packet.Apply(Globals.G.GameParams);
        if (Globals.G.gameParamsInstance != null)
            packet.Apply(Globals.G.gameParamsInstance);

        TimeAdvancePatch.FastTravelAdvancesTime = packet.FastTravelAdvancesTime;
    }

    private void OnClientboundSaveGameDataPacket(ClientboundSaveGameDataPacket packet)
    {
        if (WorldStreamingInit.isLoaded)
        {
            LogWarning("Received save game data packet while already in game!");
            return;
        }

        Log("Received save game data, loading world");

        AStartGameData.DestroyAllInstances();

        GameObject go = new("Server Start Game Data");

        //create a new save and load it
        go.AddComponent<StartGameData_ServerSave>().SetFromPacket(packet);

        //ensure save is not destroyed on scene switch
        Object.DontDestroyOnLoad(go);

        SceneSwitcher.SwitchToScene(DVScenes.Game);

        TrainStress.globalIgnoreStressCalculation = true;

    }

    private void OnClientboundWeatherPacket(ClientboundWeatherPacket packet)
    {
        if (LoadingState < PlayerLoadingState.Complete)
            Log("Received weather state");

        WeatherDriver.Instance.LoadSaveData(JObject.FromObject(packet), Globals.G.GameParams.WeatherEditorAlwaysAllowed);
    }

    private void OnClientboundRemoveLoadingScreen(ClientboundRemoveLoadingScreenPacket packet)
    {
        Log("World sync finished, removing loading screen");

        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        if (displayLoadingInfo == null)
        {
            LogDebug(() => $"Received {nameof(ClientboundRemoveLoadingScreenPacket)} but couldn't find {nameof(DisplayLoadingInfo)}!");
            return;
        }

        displayLoadingInfo.OnLoadingFinished();

        //if not single player, add in chat
        if (!isSinglePlayer)
        {
            GameObject common = GameObject.Find("[MAIN]/[GameUI]/[NewCanvasController]/Auxiliary Canvas, EventSystem, Input Module");
            if (common != null)
            {
                //
                GameObject chat = new("Chat GUI", typeof(ChatGUI));
                chat.transform.SetParent(common.transform, false);
                chatGUI = chat.GetComponent<ChatGUI>();
            }
        }
    }

    private void OnClientboundTimeAdvancePacket(ClientboundTimeAdvancePacket packet)
    {
        TimeAdvance.AdvanceTime(packet.amountOfTimeToSkipInSeconds);
    }

    private void OnClientboundRailwayStatePacket(ClientboundRailwayStatePacket packet)
    {
        Log("Received railway state");

        for (int i = 0; i < packet.SelectedJunctionBranches.Length; i++)
        {
            if (!NetworkedJunction.Get((ushort)(i + 1), out NetworkedJunction junction))
                return;
            junction.Switch((byte)Junction.SwitchMode.NO_SOUND, packet.SelectedJunctionBranches[i], true);
        }

        for (int i = 0; i < packet.TurntableRotations.Length; i++)
        {
            if (!NetworkedTurntable.Get((byte)(i + 1), out NetworkedTurntable turntable))
                return;
            turntable.SetRotation(packet.TurntableRotations[i], true, true);
        }

        railwayStateLoaded = true;
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet)
    {
        if (!NetworkedJunction.Get(packet.NetId, out NetworkedJunction junction))
            return;
        junction.Switch(packet.Mode, packet.SelectedBranch);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet)
    {
        if (!NetworkedTurntable.Get(packet.NetId, out NetworkedTurntable turntable))
            return;
        turntable.SetRotation(packet.rotation);
    }

    private void OnClientboundSpawnTrainSetPacket(ClientboundSpawnTrainSetPacket packet)
    {
        LogDebug(() => $"Spawning trainset consisting of {string.Join(", ", packet.SpawnParts.Select(p => $"{p.CarId} ({p.LiveryId}) with netId: {p.NetId}"))}");

        foreach (var part in packet.SpawnParts)
        {
            if (NetworkedTrainCar.GetTrainCarFromTrainId(part.CarId, out TrainCar car))
            {
                LogError($"ClientboundSpawnTrainSetPacket() Tried to spawn trainset with carId: {part.CarId}, but car already exists!");
                return;
            }
        }

        NetworkedCarSpawner.SpawnCars(packet.SpawnParts, packet.AutoCouple);

        if (LoadingState == PlayerLoadingState.ReadyForTrainSets)
            trainSetsSpawned++;
    }

    private void OnClientboundDestroyTrainCarPacket(ClientboundDestroyTrainCarPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogWarning($"Received DestroyTrainCarPacket for netId: {packet.NetId}, but NetworkedTrainCar was not found.");
            return;
        }

        Log($"Received DestroyTrainCarPacket for [{netTrainCar.CurrentID} {packet.NetId}]");

        //Protect myself from getting deleted in race conditions
        if (PlayerManager.Car == netTrainCar.TrainCar)
        {
            LogWarning($"Server attempted to delete car I'm on: {PlayerManager.Car?.ID}, netId: {packet?.NetId}");
            PlayerManager.SetCar(null);
        }

        //Protect other players from getting deleted in race conditions - this should be a temporary fix, if another playe's game object is deleted we should just recreate it
        if (netTrainCar == null || netTrainCar.gameObject == null || netTrainCar.TrainCar == null)
        {
            LogDebug(() => $"OnClientboundDestroyTrainCarPacket({packet?.NetId}) networkedTrainCar: {netTrainCar != null}, trainCar: {netTrainCar?.TrainCar != null}");
        }
        else
        {
            NetworkedPlayer[] componentsInChildren = (netTrainCar?.gameObject != null) ? netTrainCar.GetComponentsInChildren<NetworkedPlayer>() : [];

            foreach (NetworkedPlayer networkedPlayer in componentsInChildren)
            {
                networkedPlayer.UpdateCar(0);
            }

            netTrainCar.TrainCar.UpdateJobIdOnCarPlates(string.Empty);
            CarSpawner.Instance.DeleteCar(netTrainCar.TrainCar);
        }
    }

    public void OnClientboundTrainPhysicsPacket(ClientboundTrainsetPhysicsPacket packet)
    {
        //LogDebug(() => $"Received Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        NetworkTrainsetWatcher.Instance.Client_HandleTrainsetPhysicsUpdate(packet);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogError($"OnCommonCouplerInteractionPacket netId: {packet.NetId}, TrainCar not found!");
            return;
        }

        netTrainCar.Common_ReceiveCouplerInteraction(packet);
    }

    //private void OnCommonTrainCouplePacket(CommonTrainCouplePacket packet)
    //{
    //    TrainCar trainCar = null;
    //    TrainCar otherTrainCar = null;

    //    if (!NetworkedTrainCar.TryGet(packet.NetId, out trainCar) || !NetworkedTrainCar.TryGet(packet.OtherNetId, out otherTrainCar))
    //    {
    //        LogDebug(() => $"OnCommonTrainCouplePacket() netId: {packet.NetId}, trainCar found?: {trainCar != null}, otherNetId: {packet.OtherNetId}, otherTrainCar found?: {otherTrainCar != null}");
    //        return;
    //    }

    //    LogDebug(() => $"OnCommonTrainCouplePacket() netId: {packet.NetId}, trainCar: {trainCar.ID}, otherNetId: {packet.OtherNetId}, otherTrainCar: {otherTrainCar.ID}");

    //    Coupler coupler = packet.IsFrontCoupler ? trainCar.frontCoupler : trainCar.rearCoupler;
    //    Coupler otherCoupler = packet.OtherCarIsFrontCoupler ? otherTrainCar.frontCoupler : otherTrainCar.rearCoupler;

    //    if (coupler.CoupleTo(otherCoupler, packet.PlayAudio, false/*B99 packet.ViaChainInteraction*/) == null)
    //        LogDebug(() => $"OnCommonTrainCouplePacket() netId: {packet.NetId}, trainCar: {trainCar.ID}, otherNetId: {packet.OtherNetId}, otherTrainCar: {otherTrainCar.ID} Failed to couple!");
    //}

    private void OnCommonTrainUncouplePacket(CommonTrainUncouplePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
        {
            LogDebug(() => $"OnCommonTrainUncouplePacket() netId: {packet.NetId}, trainCar found?: {trainCar != null}");
            return;
        }

        //LogDebug(() => $"OnCommonTrainUncouplePacket() netId: {packet.NetId}, trainCar: {trainCar.ID}, isFront: {packet.IsFrontCoupler}, playAudio: {packet.PlayAudio}, DueToBrokenCouple: {packet.DueToBrokenCouple}, viaChainInteraction: {packet.ViaChainInteraction}");

        Coupler coupler = packet.IsFrontCoupler ? trainCar.frontCoupler : trainCar.rearCoupler;
        coupler.Uncouple(packet.PlayAudio, false, packet.DueToBrokenCouple, false/*B99 packet.ViaChainInteraction*/);
    }

    private void OnCommonHoseConnectedPacket(CommonHoseConnectedPacket packet)
    {
        bool foundTrainCar = NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar);
        bool foundOtherTrainCar = NetworkedTrainCar.TryGet(packet.OtherNetId, out TrainCar otherTrainCar);

        if (!foundTrainCar || trainCar == null ||
            !foundOtherTrainCar || otherTrainCar == null)
        {
            LogError($"OnCommonHoseConnectedPacket() netId: {packet.NetId}, trainCar found: {foundTrainCar}, trainCar is null: {trainCar == null}, otherNetId: {packet.OtherNetId}, otherTrainCar found: {foundOtherTrainCar}, other trainCar is null:  {otherTrainCar == null}");
            return;
        }

        string carId = $"[{trainCar?.ID}, {packet.NetId}]";
        string otherCarId = $"[{otherTrainCar?.ID}, {packet.OtherNetId}]";

        //LogDebug(() => $"OnCommonHoseConnectedPacket() trainCar: {carId}, isFront: {packet.IsFront}, otherTrainCar: {otherCarId}, isFront: {packet.OtherIsFront}, playAudio: {packet.PlayAudio}");

        Coupler coupler = packet.IsFront ? trainCar.frontCoupler : trainCar.rearCoupler;
        Coupler otherCoupler = packet.OtherIsFront ? otherTrainCar.frontCoupler : otherTrainCar.rearCoupler;

        if (coupler == null || coupler.hoseAndCock == null ||
            otherCoupler == null || otherCoupler.hoseAndCock == null)
        {
            LogError($"OnCommonHoseConnectedPacket() trainCar: {carId}, coupler found: {coupler != null}, otherCoupler found: {otherCoupler != null}, hoseAndCock found: {coupler.hoseAndCock != null}, otherHoseAndCock found: {otherCoupler.hoseAndCock != null}");
            return;
        }

        if (coupler.hoseAndCock.IsHoseConnected || otherCoupler.hoseAndCock.IsHoseConnected)
        {
            Coupler connectedTo = null;
            Coupler otherConnectedTo = null;

            if (coupler?.hoseAndCock?.connectedTo != null)
                NetworkedTrainCar.TryGetCoupler(coupler.hoseAndCock.connectedTo, out connectedTo);
            if (otherCoupler?.hoseAndCock?.connectedTo != null)
                NetworkedTrainCar.TryGetCoupler(otherCoupler.hoseAndCock.connectedTo, out otherConnectedTo);

            LogWarning($"OnCommonHoseConnectedPacket() trainCar: {carId}, isFront: {packet.IsFront}, IsHoseConnected: {coupler?.hoseAndCock?.IsHoseConnected}, connectedTo: {connectedTo?.train?.ID}," +
                       $" otherTrainCar: {otherCarId}, other isFront: {otherCoupler?.isFrontCoupler}, other IsHoseConnected: {otherCoupler?.hoseAndCock?.IsHoseConnected}, other connectedTo: {otherConnectedTo?.train?.ID}");
        }
        else
        {
            coupler.ConnectAirHose(otherCoupler, packet.PlayAudio);
        }
    }

    private void OnCommonHoseDisconnectedPacket(CommonHoseDisconnectedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar) || netTrainCar.IsDestroying)
            return;

        TrainCar trainCar = netTrainCar.TrainCar;

        //LogDebug(() => $"OnCommonHoseDisconnectedPacket() netId: {packet.NetId}, trainCar: {trainCar.ID}, isFront: {packet.IsFront}, playAudio: {packet.PlayAudio}");

        Coupler coupler = packet.IsFront ? trainCar.frontCoupler : trainCar.rearCoupler;

        coupler.DisconnectAirHose(packet.PlayAudio);
    }

    private void OnCommonMuConnectedPacket(CommonMuConnectedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar) || !NetworkedTrainCar.TryGet(packet.OtherNetId, out TrainCar otherTrainCar))
            return;

        MultipleUnitCable cable = packet.IsFront ? trainCar.muModule.frontCable : trainCar.muModule.rearCable;
        MultipleUnitCable otherCable = packet.OtherIsFront ? otherTrainCar.muModule.frontCable : otherTrainCar.muModule.rearCable;

        cable.Connect(otherCable, packet.PlayAudio);
    }

    private void OnCommonMuDisconnectedPacket(CommonMuDisconnectedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        MultipleUnitCable cable = packet.IsFront ? trainCar.muModule.frontCable : trainCar.muModule.rearCable;

        cable.Disconnect(packet.PlayAudio);
    }

    private void OnCommonCockFiddlePacket(CommonCockFiddlePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        Coupler coupler = packet.IsFront ? trainCar.frontCoupler : trainCar.rearCoupler;

        coupler.IsCockOpen = packet.IsOpen;
    }

    private void OnCommonBrakeCylinderReleasePacket(CommonBrakeCylinderReleasePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        trainCar.brakeSystem.ReleaseBrakeCylinderPressure();
    }

    private void OnCommonHandbrakePositionPacket(CommonHandbrakePositionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        trainCar.brakeSystem.SetHandbrakePosition(packet.Position);
    }

    private void OnCommonSimFlowPacket(CommonTrainPortsPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Common_UpdatePorts(packet);
    }

    private void OnCommonTrainFusesPacket(CommonTrainFusesPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Common_UpdateFuses(packet);
    }

    private void OnClientboundTrainControlAuthorityUpdatePacket(ClientboundTrainControlAuthorityUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Client_ReceiveAuthorityUpdate(packet.PortNetId, packet.State);
    }

    private void OnClientboundBrakeStateUpdatePacket(ClientboundBrakeStateUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Client_ReceiveBrakeStateUpdate(packet);

        //LogDebug(() => $"Received Brake Pressures netId {packet.NetId}: {packet.MainReservoirPressure}, {packet.IndependentPipePressure}, {packet.BrakePipePressure}, {packet.BrakeCylinderPressure}");
    }

    private void OnClientboundCargoStatePacket(ClientboundCargoStatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        LogDebug(() => $"OnClientboundCargoStatePacket() {networkedTrainCar.CurrentID}, IsLoading: {packet.IsLoading}, CargoType: {packet.CargoTypeNetId}, CargoAmount: {packet.CargoAmount}, Health: {packet.CargoHealth}, CargoModelIndex: {packet.CargoModelIndex}, WarehouseMachineId: {packet.WarehouseMachineNetId}");

        networkedTrainCar.CargoModelIndex = packet.CargoModelIndex;
        Car logicCar = networkedTrainCar.TrainCar.logicCar;

        if (logicCar == null)
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find logic car for [{networkedTrainCar.TrainCar.ID}, {packet.NetId}] is initialised: {networkedTrainCar.Client_Initialized}");
            return;
        }

        CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out CargoType cargoType);

        if (cargoType == CargoType.None && logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        //packet.CargoAmount is the total amount, not the amount to load/unload
        float cargoAmount = Mathf.Clamp(packet.CargoAmount, 0, logicCar.capacity);

        WarehouseMachine warehouseMachine = null;
        if (packet.WarehouseMachineNetId != 0 && (!WarehouseMachineLookup.TryGet(packet.WarehouseMachineNetId, out warehouseMachine) || warehouseMachine == null))
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find WarehouseMachine for netId {packet.WarehouseMachineNetId}");
            return;
        }

        if (packet.IsLoading)
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Loading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");
            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //We need either no cargo or the same cargo - if it's different, we need to remove it first
            if (logicCar.CurrentCargoTypeInCar != CargoType.None && logicCar.CurrentCargoTypeInCar != cargoType)
                logicCar.DumpCargo();

            //We have the correct cargo, but not the right amount, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount -= logicCar.LoadedCargoAmount;

            if (cargoAmount > 0)
            {
                logicCar.LoadCargo(cargoAmount, cargoType, warehouseMachine);
            }

            networkedTrainCar.TrainCar.CargoDamage.LoadCargoDamageState(packet.CargoHealth);
        }
        else
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Unloading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");

            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //If there is different cargo we need to remove it, then load the appropriate amount
            if (logicCar.CurrentCargoTypeInCar == CargoType.None || logicCar.CurrentCargoTypeInCar != cargoType)
            {
                //avoid triggering the load event by backdooring it
                logicCar.LastUnloadedCargoType = logicCar.CurrentCargoTypeInCar;
                logicCar.CurrentCargoTypeInCar = cargoType;
                logicCar.LoadedCargoAmount = cargoAmount;
            }

            //We have the correct cargo, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount = logicCar.LoadedCargoAmount - cargoAmount;

            if (cargoAmount > 0)
                logicCar.UnloadCargo(cargoAmount, cargoType, warehouseMachine);
        }
    }

    private void OnClientboundCargoHealthUpdatePacket(ClientboundCargoHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        CargoDamageModel cargoDamageModel = networkedTrainCar.TrainCar.CargoDamage;

        if (networkedTrainCar.TrainCar == null || cargoDamageModel == null)
            return;

        float deltaHealth = cargoDamageModel.currentHealth - packet.CargoHealth;

        //LogDebug(() => $"OnClientboundCargoHealthUpdatePacket() {networkedTrainCar.CurrentID}, current health: {cargoDamageModel.currentHealth}, new health: {packet.CargoHealth}, delta: {cargoDamageModel}, applySensitivity: {packet.CargoHealth > 0}");

        if (deltaHealth > 0)
            cargoDamageModel.ApplyDamageToCargo(deltaHealth, packet.CargoHealth > 0);
    }

    private void OnClientboundCarHealthUpdatePacket(ClientboundCarHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        packet.Health.LoadTo(trainCar);
    }

    private void OnClientboundWarehouseControllerUpdatePacket(ClientboundWarehouseControllerUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundWarehouseControllerUpdatePacket() NetId: {packet.NetId}, IsLoading: {packet.IsLoading}, JobNetId: {packet.JobNetId}, CarNetId: {packet.CarNetId}, CargoType: {packet.CargoTypeNetId}, Preset: [{(WarehouseMachineController.TextPreset)packet.Preset}, {packet.Preset}]");
        if (!NetworkedWarehouseMachineController.Get(packet.NetId, out NetworkedWarehouseMachineController networkedWarehouseMachineController))
        {
            LogWarning($"OnClientboundWarehouseControllerUpdatePacket() Failed to find networked warehouse machine controller for [{packet.NetId}]");
            return;
        }

        networkedWarehouseMachineController.ClientProcessUpdate(packet);
    }

    private void OnClientboundRerailTrainPacket(ClientboundRerailTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundRerailTrainPacket() NetId: {packet.NetId}, TrackId: {packet.TrackId}, Position: {packet.Position}, Forward: {packet.Forward}, currentMove: {WorldMover.currentMove}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;

        Log($"Rerailing [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Rerailing [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}");
        trainCar.Rerail(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
    }

    private void OnClientboundMoveTrainPacket(ClientboundMoveTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundMoveTrainPacket() received for netId: {packet.NetId}, trackId: {packet.TrackId}, position: {packet.Position}, forward: {packet.Forward}, isTeleporting: {packet.IsTeleporting}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;

        Log($"Moving [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Moving [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}, front coupled: {trainCar.frontCoupler.coupledTo != null}, rear coupled:{trainCar.rearCoupler.coupledTo != null}, derailed: {trainCar.derailed}");

        if (!packet.IsTeleporting)
            trainCar.MoveToTrackWithCarUncouple(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
        else
        {
            trainCar.MoveToTrack(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
            trainCar.GetComponent<TrainCarInteriorPhysics>()?.SyncPosition();
            SendTrainSyncRequest(packet.NetId);
        }
    }

    private void OnClientboundWindowsBrokenPacket(ClientboundWindowsBrokenPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.BreakWindowsFromCollision(packet.ForceDirection);
    }

    private void OnClientboundWindowsRepairedPacket(ClientboundWindowsRepairedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.RepairWindows();
    }

    private void OnClientboundMoneyPacket(ClientboundMoneyPacket packet)
    {
        LogDebug(() => $"Received new money amount ${packet.Amount}");
        Inventory.Instance.SetMoney(packet.Amount);
    }

    private void OnClientboundLicenseAcquiredPacket(ClientboundLicenseAcquiredPacket packet)
    {
        LogDebug(() => $"Received new {(packet.IsJobLicense ? "job" : "general")} license {packet.Id}");

        if (packet.IsJobLicense)
            LicenseManager.Instance.AcquireJobLicense(Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id));
        else
            LicenseManager.Instance.AcquireGeneralLicense(Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id));

        foreach (CareerManagerLicensesScreen screen in Object.FindObjectsOfType<CareerManagerLicensesScreen>())
            screen.PopulateTextsFromIndex(screen.IndexOfFirstDisplayedEntry); //B99
    }

    private void OnClientboundGarageUnlockPacket(ClientboundGarageUnlockPacket packet)
    {
        LogDebug(() => $"Received new garage {packet.Id}");
        LicenseManager.Instance.UnlockGarage(Globals.G.types.garages.Find(g => g.id == packet.Id));
    }

    private void OnClientboundDebtStatusPacket(ClientboundDebtStatusPacket packet)
    {
        CareerManagerDebtControllerPatch.HasDebt = packet.HasDebt;
    }

    private void OnCommonChatPacket(CommonChatPacket packet)
    {
        chatGUI?.ReceiveMessage(packet.message);
    }

    private void OnClientboundJobsCreatePacket(ClientboundJobsCreatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsCreatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.Jobs.Length} jobs for station {networkedStationController.StationController.logicStation.ID}");

        networkedStationController.AddJobs(packet.Jobs);
    }

    private void OnClientboundJobsUpdatePacket(ClientboundJobsUpdatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsUpdatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.JobUpdates.Length} job updates for station {networkedStationController.StationController.logicStation.ID}");

        networkedStationController.UpdateJobs(packet.JobUpdates);
    }

    private void OnClientboundTaskUpdatePacket(ClientboundTaskUpdatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedTask.TryGet(packet.TaskNetId, out Task task) || task == null)
        {
            LogError($"Received task update for taskNetId {packet.TaskNetId}, task was not found");
            return;
        }

        task.SetState(packet.NewState);
        task.taskStartTime = packet.TaskStartTime;
        task.taskFinishTime = packet.TaskFinishTime;
    }

    private void OnClientboundJobValidateResponsePacket(ClientboundJobValidateResponsePacket packet)
    {
        Log($"Job validation response received JobNetId: {packet.JobNetId}, Status: {packet.Invalid}");

        if (!NetworkedJob.Get(packet.JobNetId, out NetworkedJob networkedJob))
            return;

        Object.Destroy(networkedJob.gameObject);
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet)
    {
        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop))
        {
            LogWarning($"Pit Stop Interaction received for netId: {packet.NetId}, but pit stop does not exist!");
        }

        Log($"Pit stop interaction received for {netPitStop.StationName}");

        LogDebug(() => $"OnCommonPitStopInteractionPacket() [{netPitStop.StationName}, {packet.NetId}], interaction: [{packet.InteractionType}], resource: {packet?.ResourceType}, State: {packet.Value}");
        netPitStop.ProcessInteractionPacketAsClient(packet);
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet)
    {
        if (!NetworkedPluggableObject.Get(packet.NetId, out var netPlug))
        {
            LogWarning($"Pit Stop Plug Interaction received for plug netId: {packet.NetId}, but pit stop plug does not exist!");
            return;
        }

        Log($"Pit Stop Plug Interaction received for {netPlug.NetId}");

        LogDebug(() => $"OnCommonPitStopPlugInteractionPacket() [{netPlug?.transform?.name}, {packet.NetId}], interaction: [{(PlugInteractionType)packet.InteractionType}]");
        netPlug.ProcessPacket(packet);
    }

    private void OnClientboundPitStopBulkUpdatePacket(ClientboundPitStopBulkUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundPitStopBulkUpdatePacket() NetId: {packet.NetId}, CarCount: {packet.CarCount}, CarSelection: {packet.CarSelection}, FaucetNotch: {packet.FaucetNotch}, ResourceData Count: {packet.ResourceData.Length}, PlugData: {packet.PlugData.Length}");

        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop))
        {
            LogWarning($"Pit Stop Bulk Data received for station netId: {packet.NetId}, but pit stop does not exist!");
            return;
        }

        Log($"Pit Stop Bulk Data received for {netPitStop.StationName}");

        netPitStop.ProcessBulkUpdate(packet);
    }


    private void OnCommonItemChangePacket(CommonItemChangePacket packet)
    {
        //LogDebug(() => $"OnCommonItemChangePacket({packet?.Items?.Count})");


        //LogDebug(() =>
        //{
        //    string debug = "";

        //    foreach (var item in packet?.Items)
        //    {
        //        debug += "UpdateType: " + item?.UpdateType + "\r\n";
        //        debug += "itemNetId: " + item?.ItemNetId + "\r\n";
        //        debug += "PrefabName: " + item?.PrefabName + "\r\n";
        //        debug += "Equipped: " + item?.ItemState + "\r\n";
        //        debug += "Position: " + item?.ItemPosition + "\r\n";
        //        debug += "Rotation: " + item?.ItemRotation + "\r\n";
        //        debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
        //        debug += "Player: " + item?.Player + "\r\n";
        //        debug += "CarNetId: " + item?.CarNetId + "\r\n";
        //        debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

        //        debug += $"States: {item?.States?.Count}\r\n";

        //        if (item.States != null)
        //            foreach (var state in item?.States)
        //                debug += "\t" + state.Key + ": " + state.Value + "\r\n";
        //        else
        //            debug += "\r\n";
        //    }

        //    return debug;
        //});

        if (!NetworkedItemManager.Instance.ReceiveSnapshots(packet.Items, null))
            FailWorldSync(new InvalidOperationException("Item update queue exceeded its safety limit."));
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");
        netTrainCar?.Common_ReceivePaintThemeUpdate(packet.TargetArea, paint);
    }

    private void OnClientboundRestorationStateChangePacket(ClientboundRestorationStateChangePacket packet)
    {
        LogDebug(() => $"OnClientboundRestorationStateChangePacket() NetId: {packet.NetId}, NewState: {packet.NewState}, TransportCarNetIds: [{string.Join(", ", packet?.TransportCarNetIds)}]");

        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
        {
            LogWarning($"Received restoration state change for netId: {packet.NetId}, but TrainCar was not found.");
            return;
        }

        Log($"Received restoration state change for {trainCar?.ID}");

        var controller = LocoRestorationController.GetForTrainCar(trainCar);
        if (controller == null)
        {
            LogWarning($"Received restoration state change for {trainCar?.ID}, but LocoRestorationController was not found.");
            return;
        }

        switch (packet.NewState)
        {
            case LocoRestorationController.RestorationState.S5_PartOrdered:
                controller.orderPartsModule.SetUnitsToBuy(0f);
                controller.OnPartsOrdered();
                break;

            case LocoRestorationController.RestorationState.S6_PartPickedUp:
                if (packet.TransportCarNetIds == null || packet.TransportCarNetIds.Length == 0)
                {
                    LogError($"Received restoration state change for {trainCar?.ID}, but transport cars are empty");
                    return;
                }

                List<Car> transportCars = [];
                foreach (var netId in packet.TransportCarNetIds)
                {
                    if (!NetworkedTrainCar.TryGet(netId, out Car transportCar) || transportCar == null)
                    {
                        LogError($"Received restoration state change for {trainCar?.ID}, but failed to find transport car with netId {netId}");
                        continue;
                    }
                    transportCars.Add(transportCar);
                }
                controller.OnOrderedPartLoadedCargo(transportCars);
                break;

            case LocoRestorationController.RestorationState.S7_PartDelivered:
                if (controller.transportingCars != null && controller.transportingCars.Count > 0)
                {
                    foreach (var transportCar in controller.transportingCars)
                    {
                        trainCar.preventFastTravelWithCar = false;
                        trainCar.preventDelete = false;
                    }
                }
                controller.transportingCars = null;
                controller.SetState(packet.NewState);
                controller.installPartsModule.AddThingToCart();
                break;

            case LocoRestorationController.RestorationState.S8_PartInstalled:
                controller.installPartsModule.SetUnitsToBuy(0f);
                controller.OnInstallPartsPaid();
                break;

            case LocoRestorationController.RestorationState.S9_LocoServiced:
                controller.OnServiceDone();
                break;

            case LocoRestorationController.RestorationState.S10_PaintJobDone:
                controller.OnPaintJobChanged();
                break;
        }
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet)
    {
        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");

        netCashRegister.Client_ProcessCashRegisterAction(packet.Action, packet.Amount);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet)
    {
        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Client_ReceiveSwitchState(packet.IsOn);
    }

    #endregion

    #region Senders

    private void SendPacketToServer<T>(T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        SendPacket(serverPeer, packet, deliveryMethod);
    }

    private void SendNetSerializablePacketToServer<T>(T packet, DeliveryMethod deliveryMethod) where T : INetSerializable, new()
    {
        SendNetSerializablePacket(serverPeer, packet, deliveryMethod);
    }


    #region Mod Packets
    public void SendExternalPacketToServer<T>(T packet, bool reliable) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacketToServer(packet, deliveryMethod);
    }

    public void SendExternalSerializablePacketToServer<T>(T packet, bool reliable) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };
        SendNetSerializablePacketToServer(wrapper, deliveryMethod);
    }
    #endregion

    private void SendLoadStateUpdate(PlayerLoadingState newState)
    {
        Log($"Sending Load State {newState}");
        LoadingState = newState;
        SendPacketToServer(new ServerboundLoadStateUpdatePacket { LoadState = newState }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPlayerPosition(PlayerTrackingData trackingData, PlayerPostureFlags posture, bool isOnCar, ushort carId, bool reliable)
    {
        //LogDebug(() => $"SendPlayerPosition({position}, {moveDir}, {rotationY}, {carId}, {isJumping}, {IsOnCar})");

        SendPacketToServer(new ServerboundPlayerPositionPacket
        {
            TrackingData = trackingData,
            Posture = posture,
            IsOnCar = isOnCar,
            CarID = carId
        }, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
    }

    public void SendPlayerPreferences(Dictionary<PlayerPreference, string> preferences)
    {
        if (preferences == null || preferences.Count == 0)
        {
            LogWarning("SendPlayerPreferences() called with null or empty preferences");
            return;
        }
        SendPacketToServer(new ServerboundPlayerPreferenceUpdatePacket
        {
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTimeAdvance(float amountOfTimeToSkipInSeconds)
    {
        SendPacketToServer(new ServerboundTimeAdvancePacket
        {
            amountOfTimeToSkipInSeconds = amountOfTimeToSkipInSeconds
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendJunctionSwitched(ushort netId, byte selectedBranch, Junction.SwitchMode mode)
    {
        SendPacketToServer(new CommonChangeJunctionPacket
        {
            NetId = netId,
            SelectedBranch = selectedBranch,
            Mode = (byte)mode
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTurntableRotation(byte netId, float rotation)
    {
        SendPacketToServer(new CommonRotateTurntablePacket
        {
            NetId = netId,
            rotation = rotation
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendCouplerInteraction(CouplerInteractionType flags, Coupler coupler, Coupler otherCoupler = null)
    {
        ushort couplerNetId = coupler?.train?.GetNetId() ?? 0;
        ushort otherCouplerNetId = otherCoupler?.train?.GetNetId() ?? 0;
        bool couplerIsFront = coupler?.isFrontCoupler ?? false;
        bool otherCouplerIsFront = otherCoupler?.isFrontCoupler ?? false;

        if (couplerNetId == 0)
        {
            LogWarning($"SendCouplerInteraction failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendCouplerInteraction([{flags}], {coupler?.train?.ID}, {otherCoupler?.train?.ID}) coupler isFront: {couplerIsFront}, otherCoupler isFront: {otherCouplerIsFront}");

        if (coupler == null)
            return;

        Log($"Sending coupler interaction [{flags}] for {coupler?.train?.ID}, {(couplerIsFront ? "Front" : "Rear")}");

        SendPacketToServer(new CommonCouplerInteractionPacket
        {
            NetId = couplerNetId,
            IsFrontCoupler = couplerIsFront,
            OtherNetId = otherCouplerNetId,
            IsFrontOtherCoupler = otherCouplerIsFront,
            Flags = (ushort)flags,
        }, DeliveryMethod.ReliableOrdered);
    }


    //public void SendTrainCouple(Coupler coupler, Coupler otherCoupler, bool playAudio, bool viaChainInteraction)
    //{
    //    ushort couplerNetId = coupler.train.GetNetId();
    //    ushort otherCouplerNetId = otherCoupler.train.GetNetId();

    //    if (couplerNetId == 0 || otherCouplerNetId == 0)
    //    {
    //        LogWarning($"SendTrainCouple failed. Coupler: {coupler.name} {couplerNetId}, OtherCoupler: {otherCoupler.name} {otherCouplerNetId}");
    //        return;
    //    }

    //    SendPacketToServer(new CommonTrainCouplePacket
    //    {
    //        NetId = couplerNetId, //coupler.train.GetNetId(),
    //        IsFrontCoupler = coupler.isFrontCoupler,
    //        Value = (byte)coupler.state,
    //        OtherNetId = otherCouplerNetId, //otherCoupler.train.GetNetId(),
    //        OtherState = (byte)otherCoupler.state,
    //        OtherCarIsFrontCoupler = otherCoupler.isFrontCoupler,
    //        PlayAudio = playAudio,
    //        ViaChainInteraction = viaChainInteraction
    //    }, DeliveryMethod.ReliableUnordered);
    //}

    public void SendHoseConnected(Coupler coupler, Coupler otherCoupler, bool playAudio)
    {
        if (coupler == null || otherCoupler == null)
        {
            LogWarning($"Failed to send HoseConnected, {(coupler == null ? "Coupler is null" : coupler?.train?.ID)}, {(otherCoupler == null ? "Other Coupler is null" : otherCoupler?.train?.ID)}");
            return;
        }

        ushort couplerNetId = coupler.train.GetNetId();
        ushort otherCouplerNetId = otherCoupler.train.GetNetId();

        if (couplerNetId == 0 || otherCouplerNetId == 0)
        {
            LogWarning($"SendHoseConnected failed. Coupler: {coupler?.train?.ID} {couplerNetId}, OtherCoupler: {otherCoupler?.train?.ID} {otherCouplerNetId}");
            return;
        }

        SendPacketToServer(new CommonHoseConnectedPacket
        {
            NetId = couplerNetId,
            IsFront = coupler.isFrontCoupler,
            OtherNetId = otherCouplerNetId,
            OtherIsFront = otherCoupler.isFrontCoupler,
            PlayAudio = playAudio
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendHoseDisconnected(Coupler coupler, bool playAudio)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendHoseDisconnected failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendHoseDisconnected({coupler.train.ID}, {coupler.isFrontCoupler}, {playAudio})");

        SendPacketToServer(new CommonHoseDisconnectedPacket
        {
            NetId = couplerNetId,
            IsFront = coupler.isFrontCoupler,
            PlayAudio = playAudio
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendMuConnected(MultipleUnitCable cable, MultipleUnitCable otherCable, bool playAudio)
    {
        ushort cableNetId = cable.muModule.train.GetNetId();
        ushort otherCableNetId = otherCable.muModule.train.GetNetId();

        if (cableNetId == 0 || otherCableNetId == 0)
        {
            LogWarning($"SendMuConnected failed. Cable: {cable.muModule.train.name} {cableNetId}, OtherCable: {otherCable.muModule.train.name} {otherCableNetId}");
            return;
        }

        SendPacketToServer(new CommonMuConnectedPacket
        {
            NetId = cableNetId,
            IsFront = cable.isFront,
            OtherNetId = otherCableNetId,
            OtherIsFront = otherCable.isFront,
            PlayAudio = playAudio
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendMuDisconnected(ushort netId, MultipleUnitCable cable, bool playAudio)
    {

        SendPacketToServer(new CommonMuDisconnectedPacket
        {
            NetId = netId,
            IsFront = cable.isFront,
            PlayAudio = playAudio
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        SendPacketToServer(new CommonCockFiddlePacket
        {
            NetId = netId,
            IsFront = coupler.isFrontCoupler,
            IsOpen = isOpen
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendBrakeCylinderReleased(ushort netId)
    {
        SendPacketToServer(new CommonBrakeCylinderReleasePacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendHandbrakePositionChanged(ushort netId, float position)
    {
        SendPacketToServer(new CommonHandbrakePositionPacket
        {
            NetId = netId,
            Position = position
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendAddCoal(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundAddCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTenderCoalPileInteraction(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundTenderCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendFireboxIgnition(ushort netId)
    {
        SendPacketToServer(new ServerboundFireboxIgnitePacket
        {
            NetId = netId,
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPorts(ushort netId, uint[] portIds, float[] portValues)
    {
        SendPacketToServer(new CommonTrainPortsPacket
        {
            NetId = netId,
            PortIds = portIds,
            PortValues = portValues
        }, DeliveryMethod.ReliableOrdered);

        /*
        string log=$"Sending ports netId: {netId}";
        for (int i = 0; i < portIds.Length; i++) {
            log += $"\r\n\t{portIds[i]}: {portValues[i]}";
        }

        LogDebug(() => log);
        */
    }

    public void SendFuses(ushort netId, uint[] fuseIds, bool[] fuseValues)
    {
        SendPacketToServer(new CommonTrainFusesPacket
        {
            NetId = netId,
            FuseIds = fuseIds,
            FuseValues = fuseValues
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainSyncRequest(ushort netId)
    {
        SendPacketToServer(new ServerboundTrainSyncRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainDeleteRequest(ushort netId)
    {
        SendPacketToServer(new ServerboundTrainDeleteRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainRerailRequest(ushort netId, ushort trackId, Vector3 position, Vector3 forward)
    {
        SendPacketToServer(new ServerboundTrainRerailRequestPacket
        {
            NetId = netId,
            TrackId = trackId,
            Position = position,
            Forward = forward
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainSpawnRequest(string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundTrainSpawnRequestPacket
        {
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendWorkTrainRequest(uint ticketId, string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundWorkTrainRequestPacket
        {
            TicketId = ticketId,
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendLicensePurchaseRequest(uint ticketId, string id, bool isJobLicense)
    {
        SendPacketToServer(new ServerboundLicensePurchaseRequestPacket
        {
            TicketId = ticketId,
            Id = id,
            IsJobLicense = isJobLicense
        }, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>Requests a non-binding server quote. Does not charge funds or spawn items.</summary>
    public void SendShopQuoteRequest(uint ticketId, ushort registerNetId, string[] itemIds, int[] quantities)
    {
        SendPacketToServer(new ServerboundShopQuoteRequestPacket
        {
            TicketId = ticketId,
            RegisterNetId = registerNetId,
            ItemIds = itemIds,
            Quantities = quantities
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendShopPurchaseRequest(uint ticketId, string operationId, ushort registerNetId, string[] itemIds, int[] quantities)
    {
        SendPacketToServer(new ServerboundShopPurchaseRequestPacket
        {
            TicketId = ticketId, OperationId = operationId, RegisterNetId = registerNetId,
            ItemIds = itemIds, Quantities = quantities
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobValidateRequest(NetworkedJob job, NetworkedStationController station)
    {
        SendPacketToServer(new ServerboundJobValidateRequestPacket
        {
            JobNetId = job.NetId,
            StationNetId = station.NetId,
            validationType = job.ValidationType
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendWarehouseRequest(WarehouseAction action, ushort netId)
    {
        SendPacketToServer(new ServerboundWarehouseMachineControllerRequestPacket
        {
            NetId = netId,
            WarehouseAction = action,
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendChat(string message)
    {
        SendPacketToServer(new CommonChatPacket
        {
            message = message
        }, DeliveryMethod.ReliableUnordered);

    }

    public void SendPitStopInteractionPacket(ushort netId, PitStopStationInteractionType interaction, ResourceType? resource, float state)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({netId}, [{interaction}], {resource}, {state})");

        int res = resource == null ? 0 : (int)resource;
        SendPacketToServer(new CommonPitStopInteractionPacket
        {
            Tick = NetworkLifecycle.Instance.Tick,
            NetId = netId,
            InteractionType = interaction,
            ResourceType = res,
            Value = state
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket
    (
        ushort netId,
        PlugInteractionType interaction,
        Vector3? position = null,
        Quaternion? rotation = null,
        ushort trainCarNetId = 0,
        sbyte socketIndex = -1
    )
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({netId}, {interaction}, pos: {position}, rot: {rotation}, trainNetId: {trainCarNetId}, socketIndex: {socketIndex})");

        SendNetSerializablePacketToServer(new CommonPitStopPlugInteractionPacket
        {
            NetId = netId,
            InteractionType = interaction,
            TrainCarNetId = trainCarNetId,
            SocketIndex = socketIndex,
            Position = position,
            Rotation = rotation,

        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemsChangePacket(List<ItemUpdateData> items)
    {
        Log($"Sending CommonItemChangePacket with {items.Count()} items");
        //SendPacketToServer(new CommonItemChangePacket { Items = items },
        //    DeliveryMethod.ReliableUnordered);

        for (int offset = 0; offset < items.Count; offset += NetworkedItemManager.MaxClientBatchItems)
        {
            int count = Math.Min(NetworkedItemManager.MaxClientBatchItems, items.Count - offset);
            SendNetSerializablePacketToServer(new CommonItemChangePacket { Items = items.GetRange(offset, count) },
                    DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeId)
    {
        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        SendPacketToServer(new CommonPaintThemePacket { NetId = netTraincar.NetId, TargetArea = targetArea, PaintThemeId = themeId }, DeliveryMethod.ReliableUnordered);
    }

    public void SendCashRegisterAction(ushort netId, CashRegisterAction action, double amount = 0.0f)
    {
        SendPacketToServer(
            new CommonCashRegisterWithModulesActionPacket
            {
                NetId = netId,
                Action = action,
                Amount = amount
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void SendTrainControlAuthorityRequest(ushort netId, uint portNetId, bool requestAuthority)
    {
        SendPacketToServer
        (
            new ServerboundTrainControlAuthorityPacket
            {
                NetId = netId,
                PortNetId = portNetId,
                RequestAuthority = requestAuthority
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void SendGenericSwitchState(uint netId, bool isOn)
    {
        SendPacketToServer
        (
            new CommonGenericSwitchStatePacket
            {
                NetId = netId,
                IsOn = isOn
            },
            deliveryMethod: DeliveryMethod.ReliableOrdered
        );
    }
    #endregion
}

