namespace Multiplayer.Networking.Data.Train;

// Retain the most recent result; older operation IDs can never execute again.
public sealed class FastTravelOperation
{
    public uint Id { get; private set; }
    public ushort CarId { get; private set; }
    public string Destination { get; private set; }
    public byte Status { get; set; }
    public bool Begin(uint id, ushort carId, string destination)
    {
        if (id == 0 || id <= Id || carId == 0 || string.IsNullOrWhiteSpace(destination) || destination.Length > 256) return false;
        if (Status == 2 || Status == 3) return false;
        Id = id; CarId = carId; Destination = destination; Status = 2;
        return true;
    }
    public bool Matches(uint id, ushort carId, string destination) => Id == id && CarId == carId && Destination == destination;
}
