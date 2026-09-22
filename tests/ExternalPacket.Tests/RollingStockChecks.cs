using System;
using System.Collections.Generic;
using System.Linq;
using MPAPI;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Components.Networking.Train;

internal static class RollingStockChecks
{
    public static void Run()
    {
        var server = new NetworkServer(); var peer = new Peer();
        var member = new ServerPlayer { PlayerId = 1, PersistentId = Guid.NewGuid() };
        server.Peers[peer] = member;
        var own = new TrainCar { CarGUID = "own" }; var foreign = new TrainCar { CarGUID = "foreign" };
        NetworkedTrainCar.Cars[1] = new NetworkedTrainCar { TrainCar = own };
        NetworkedTrainCar.Cars[2] = new NetworkedTrainCar { TrainCar = foreign };
        int checks = 0;
        Action<bool,string> check = (ok, name) => { if (!ok) throw new Exception(name); checks++; };
        try
        {
            check(server.CanUseRollingStock(member, foreign), "No provider must retain native behavior");
            RollingStockAccess.SetValidator((actor, guid) => ReferenceEquals(actor, server.GetWrapper(member)) && guid == "own");
            check(server.CanUseRollingStock(member, own), "Member refused");
            check(!server.CanUseRollingStock(member, foreign), "Foreign vehicle allowed");
            check(server.CheckAccess(peer, 1), "Authenticated member packet refused");
            check(!server.CheckAccess(peer, 1, 2), "Foreign second endpoint allowed");
            check(!server.CheckAccess(peer, 99), "Missing car allowed");
            check(!server.CheckAccess(new Peer(), 1), "Unknown peer allowed");
            server.Ready = false;
            check(!server.CheckAccess(peer, 1), "Unready session allowed"); server.Ready = true;
            own.trainset = new Trainset(); own.trainset.cars.AddRange(new[] { own, foreign });
            check(!server.CanUseRollingStock(member, own), "Allowed loco can move foreign consist"); own.trainset = null;
            own.frontCoupler = new Coupler { connected = new Coupler { train = foreign } };
            check(!server.CanUseRollingStock(member, own), "Foreign hose-only connection allowed"); own.frontCoupler = null;
            own.muModule = new MuModule { frontCable = new MuCable { connectedTo = new MuCable { muModule = new MuModule { train = foreign } } } };
            check(!server.CanUseRollingStock(member, own), "Foreign MU-only connection allowed"); own.muModule = null;
            var stale = new ServerPlayer { PlayerId = member.PlayerId, PersistentId = member.PersistentId };
            check(!server.CanUseRollingStock(stale, own), "Stale session allowed");
            RollingStockAccess.SetValidator((actor, guid) => { throw new InvalidOperationException(); });
            check(!server.CanUseRollingStock(member, own), "Provider failure must deny");
            check(!RollingStockAccess.Allows(null, "own"), "Missing actor allowed");
            var actor1 = server.GetWrapper(member);
            using (RollingStockAccess.ForActor(actor1))
            {
                check(ReferenceEquals(RollingStockAccess.CurrentActor, actor1), "Actor scope missing");
                using (RollingStockAccess.ForActor(null)) check(RollingStockAccess.CurrentActor == null, "Nested scope failed");
                check(ReferenceEquals(RollingStockAccess.CurrentActor, actor1), "Nested scope did not restore");
                bool isolated = false;
                var thread = new System.Threading.Thread(() => isolated = RollingStockAccess.CurrentActor == null);
                thread.Start(); thread.Join(); check(isolated, "Actor leaked to other thread");
            }
            check(RollingStockAccess.CurrentActor == null, "Actor scope leaked");
            Console.WriteLine($"{checks} rolling stock authority checks passed (production API and server gate).");
        }
        finally { RollingStockAccess.SetValidator(null); }
    }
}
public class TrainCar { public string CarGUID; public Trainset trainset; public Coupler frontCoupler, rearCoupler; public MuModule muModule; }
public class Coupler { public TrainCar train; public Coupler connected; public Coupler GetAirHoseConnectedTo() => connected; }
public class MuModule { public TrainCar train; public MuCable frontCable, rearCable; }
public class MuCable { public MuModule muModule; public MuCable connectedTo; }
public class Trainset { public readonly List<TrainCar> cars = new List<TrainCar>(); }
namespace Multiplayer.Components.Networking.Train
{
    public class NetworkedTrainCar
    {
        public TrainCar TrainCar;
        public static readonly Dictionary<ushort,NetworkedTrainCar> Cars = new Dictionary<ushort,NetworkedTrainCar>();
        public static bool TryGet(ushort id, out NetworkedTrainCar car) => Cars.TryGetValue(id, out car);
    }
}
namespace Multiplayer.Networking.Managers.Server
{
    public partial class NetworkServer
    {
        public bool Ready = true;
        public bool TryGetServerPlayer(byte id, out ServerPlayer player) { player = Peers.Values.FirstOrDefault(p => p.PlayerId == id); return player != null; }
        private bool AllowsAction(ServerPlayer player, bool required) => Ready;
        private void RejectAction(ITransportPeer peer, string action, string reason) { }
        public bool CheckAccess(ITransportPeer peer, ushort car, ushort other = 0) => CanUseRollingStock(peer, car, other);
    }
}
