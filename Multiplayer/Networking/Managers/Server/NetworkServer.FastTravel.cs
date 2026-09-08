using System;
using System.Collections;
using System.Linq;
using DV.InventorySystem;
using DV.Teleporters;
using DV.Utils;
using LiteNetLib;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Patches.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Networking.Managers.Server;

public partial class NetworkServer
{
    private GuardedRoutine fastTravelRoutine;
    private readonly System.Collections.Generic.HashSet<ushort> travellingCars = new();
    public bool IsFastTravelCar(ushort id) => travellingCars.Contains(id);

    private void ReplyFastTravel(ServerPlayer player, uint operation, ushort car, byte status)
        => SendPacket(player.Peer, new ClientboundFastTravelPacket { OperationId = operation, CarId = car, Status = status }, DeliveryMethod.ReliableOrdered);

    private void OnFastTravelRequest(ServerboundFastTravelPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player)) return;
        var operation = player.FastTravel;
        if (operation.Matches(packet.OperationId, packet.CarId, packet.Destination))
        {
            ReplyFastTravel(player, operation.Id, operation.CarId, operation.Status);
            return;
        }
        if (!operation.Begin(packet.OperationId, packet.CarId, packet.Destination))
        {
            ReplyFastTravel(player, packet.OperationId, packet.CarId, 0);
            return;
        }
        operation.Status = 0;
        if (player.LoadingState != PlayerLoadingState.Complete || fastTravelRoutine != null ||
            (bool)HarmonyLib.AccessTools.Field(typeof(TrainCarTeleporter), "isTeleportingTrain").GetValue(null) ||
            player.CarId != packet.CarId || !NetworkedTrainCar.TryGet(packet.CarId, out TrainCar car) ||
            !car.IsLoco || car.derailed || car.IsTeleporting || !car.isStationary ||
            !ServerActionPolicy.InRange((player.WorldPosition - car.transform.position).sqrMagnitude, 30f))
        {
            ReplyFastTravel(player, operation.Id, operation.CarId, 0);
            return;
        }
        var destinations = UnityEngine.Object.FindObjectsOfType<FastTravelDestination>()
            .Where(d => d.MarkerName == packet.Destination && d.playerTeleportAnchor != null).ToArray();
        if (destinations.Length != 1)
        {
            ReplyFastTravel(player, operation.Id, operation.CarId, 0);
            return;
        }
        var destination = destinations[0];
        DV.UI.FastTravelData quote;
        try
        {
            CoordinatedFastTravelPatch.QuoteOrigin = player.WorldPosition;
            quote = FastTravelController.ExtractFastTravelData(destination, car);
        }
        finally { CoordinatedFastTravelPatch.QuoteOrigin = null; }
        var cars = TrainCarTeleporter.GetConnectedLocoMultipleUnitCars(car) ?? new System.Collections.Generic.List<TrainCar> { car };
        if (!quote.CanTravelWithLoco || quote.isTutorialInProgress || quote.fastTravelWithLocoPrice < 0 ||
            cars.Any(c => c == null || c.derailed || c.IsTeleporting || !c.isStationary) ||
            !TrainCompositionPolicy.IsValid(cars.Select(c => c.GetNetId()).ToArray()) ||
            !Inventory.Instance.RemoveMoney(quote.fastTravelWithLocoPrice))
        {
            ReplyFastTravel(player, operation.Id, operation.CarId, 0);
            return;
        }
        operation.Status = 2;
        foreach (var member in cars) travellingCars.Add(member.GetNetId());
        var riders = serverPlayers.Values.Where(p => cars.Any(c => c.GetNetId() == p.CarId))
            .Select(p => (Player: p, CarId: p.CarId)).ToArray();
        var target = destination.playerTeleportAnchor.position - WorldMover.currentMove;
        fastTravelRoutine = new GuardedRoutine(Travel(), ex => LogError("Fast travel recovery required: " + ex));
        CoroutineManager.Instance.StartCoroutine(fastTravelRoutine);

        IEnumerator Travel()
        {
            bool moved = false;
            var before = cars.Select(c => c.transform.position - WorldMover.currentMove).ToArray();
            try
            {
                foreach (var member in cars)
                {
                    var controls = member.SimController?.controlsOverrider;
                    controls?.Throttle?.Set(0f);
                    controls?.DynamicBrake?.Set(0f);
                    controls?.Reverser?.Set(0.5f);
                    controls?.Brake?.Set(0f);
                    controls?.IndependentBrake?.Set(1f);
                    controls?.Handbrake?.Set(1f);
                }
                // Drive the native iterator ourselves so exceptions and disposal are owned here.
                var inner = TrainCarTeleporter.TeleportTrainset(cars, target + WorldMover.currentMove);
                try { while (inner.MoveNext()) yield return inner.Current; }
                finally { (inner as IDisposable)?.Dispose(); }
                moved = cars.Where((c, i) => (c.transform.position - WorldMover.currentMove - before[i]).sqrMagnitude > 1f).Any();
                if (!moved) throw new InvalidOperationException("Native teleport did not move the train.");
                foreach (var set in cars.Select(c => c.trainset).Distinct())
                {
                    SendPacketToAll(new ClientboundTrainRepairPacket { Parts = TrainsetSpawnPart.FromTrainSet(set.cars), Relocate = true, Tick = Components.Networking.NetworkLifecycle.Instance.Tick },
                        DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
                    foreach (var member in set.cars)
                        if (member.TryNetworked(out NetworkedTrainCar net)) net.Server_DirtyAllState();
                }
                operation.Status = 1;
                foreach (var rider in riders)
                    if (TryGetServerPlayer(rider.Player.Peer, out var current) && ReferenceEquals(current, rider.Player))
                        ReplyFastTravel(current, current == player ? operation.Id : 0, rider.CarId, 1);
                if (quote.fastTravelDuration > 0 && fastTravelAdvancesTime)
                    DV.TimeAdvance.AdvanceTime(quote.fastTravelDuration);
            }
            finally
            {
                try
                {
                if (operation.Status == 2)
                {
                    // Native code can detach links before failing. Never replay an uncertain operation.
                    operation.Status = 3;
                    if (!moved && cars.All(c => c != null) && cars.Select((c, i) => (c.transform.position - WorldMover.currentMove - before[i]).sqrMagnitude < 1f).All(v => v))
                        Inventory.Instance.AddMoney(quote.fastTravelWithLocoPrice);
                    ReplyFastTravel(player, operation.Id, operation.CarId, 3);
                }
                }
                finally
                {
                    HarmonyLib.AccessTools.Field(typeof(TrainCarTeleporter), "isTeleportingTrain").SetValue(null, false);
                    travellingCars.Clear();
                    fastTravelRoutine = null;
                }
            }
        }
    }
}
