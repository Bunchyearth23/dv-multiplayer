using System;
using System.Collections;
using DV.Teleporters;
using DV.TerrainSystem;
using DV.Utils;
using LiteNetLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Networking.Managers.Client;

public partial class NetworkClient
{
    private uint nextTravelOperation;
    private ServerboundFastTravelPacket pendingTravel;
    private GuardedRoutine arrivalRoutine;

    public void RequestFastTravel(FastTravelDestination destination)
    {
        if (arrivalRoutine != null || destination == null || PlayerManager.Car == null) return;
        if (pendingTravel == null)
        {
            if (nextTravelOperation == uint.MaxValue) return;
            pendingTravel = new ServerboundFastTravelPacket
            {
                OperationId = ++nextTravelOperation,
                CarId = PlayerManager.Car.GetNetId(),
                Destination = destination.MarkerName
            };
        }
        SendPacketToServer(pendingTravel, DeliveryMethod.ReliableOrdered);
    }

    private void OnFastTravelResponse(ClientboundFastTravelPacket packet)
    {
        if (packet.Status > 3 || packet.CarId == 0) return;
        if (packet.OperationId != 0 && (pendingTravel == null || packet.OperationId != pendingTravel.OperationId || packet.CarId != pendingTravel.CarId)) return;
        if (packet.Status == 2) return;
        if (packet.Status == 3)
        {
            LogError("Fast travel requires recovery; reconnect before retrying.");
            return;
        }
        if (packet.OperationId != 0) pendingTravel = null;
        if (packet.Status == 0) { LogWarning("Fast travel refused by the server."); return; }
        if (arrivalRoutine != null) return;
        ushort carId = packet.CarId; // reusable packet cannot be captured across yields
        arrivalRoutine = new GuardedRoutine(Arrive(), FailWorldSync);
        CoroutineManager.Instance.StartCoroutine(arrivalRoutine);

        IEnumerator Arrive()
        {
            try
            {
                long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 60 * System.Diagnostics.Stopwatch.Frequency;
                NetworkedTrainCar netCar;
                while (!NetworkedTrainCar.TryGet(carId, out netCar) || !netCar.Client_Initialized)
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                        throw new TimeoutException("Arrival car initialization timed out.");
                    yield return null;
                }
                TrainCar car = netCar.TrainCar;
                PlayerManager.TeleportPlayerToCar(car);
                while (TerrainGrid.Instance != null && !TerrainGrid.Instance.IsInLoadedRegion(PlayerManager.PlayerTransform.position))
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Fast travel terrain streaming timed out.");
                    yield return null;
                }
                PlayerManager.TeleportPlayerToCar(car);
                if (!NetworkLifecycle.Instance.IsHost()) SendTrainSyncRequest(carId);
            }
            finally { arrivalRoutine = null; }
        }
    }
}
