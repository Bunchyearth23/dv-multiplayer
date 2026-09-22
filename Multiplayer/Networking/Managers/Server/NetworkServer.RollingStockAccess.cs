using MPAPI;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.TransportLayers;

namespace Multiplayer.Networking.Managers.Server;

public partial class NetworkServer
{
    public bool CanUseRollingStock(ServerPlayer player, TrainCar car)
    {
        if (!RollingStockAccess.IsRestricted) return true;
        if (player == null || car == null || !TryGetServerPlayer(player.PlayerId, out var current) || current != player) return false;
        var actor = GetWrapper(player);
        if (!AllowsVehicleAndConnections(actor, car)) return false;
        // Driving an allowed locomotive must not move a foreign company's coupled stock.
        if (car.trainset?.cars != null)
            foreach (var coupled in car.trainset.cars)
                if (coupled != null && coupled != car && !AllowsVehicleAndConnections(actor, coupled)) return false;
        return true;
    }

    private static bool AllowsVehicleAndConnections(MPAPI.Interfaces.IPlayer actor, TrainCar car)
    {
        if (!RollingStockAccess.Allows(actor, car.CarGUID)) return false;
        // Hoses/MU can remain connected without a mechanical coupling.
        var frontHose = car.frontCoupler?.GetAirHoseConnectedTo()?.train;
        var rearHose = car.rearCoupler?.GetAirHoseConnectedTo()?.train;
        var frontMu = car.muModule?.frontCable?.connectedTo?.muModule?.train;
        var rearMu = car.muModule?.rearCable?.connectedTo?.muModule?.train;
        return (frontHose == null || RollingStockAccess.Allows(actor, frontHose.CarGUID)) &&
            (rearHose == null || RollingStockAccess.Allows(actor, rearHose.CarGUID)) &&
            (frontMu == null || RollingStockAccess.Allows(actor, frontMu.CarGUID)) &&
            (rearMu == null || RollingStockAccess.Allows(actor, rearMu.CarGUID));
    }

    private bool CanUseRollingStock(ITransportPeer peer, ushort netId, ushort otherNetId = 0)
    {
        if (!TryGetServerPlayer(peer, out var player) || !AllowsAction(player, true) ||
            !NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car) || !CanUseRollingStock(player, car.TrainCar))
        { RejectAction(peer, "Rolling stock", "Company membership required."); return false; }
        if (otherNetId != 0 && (!NetworkedTrainCar.TryGet(otherNetId, out NetworkedTrainCar other) || !CanUseRollingStock(player, other.TrainCar)))
        { RejectAction(peer, "Rolling stock", "Company membership required for both vehicles."); return false; }
        return true;
    }
}
