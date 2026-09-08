using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Components.Networking;

public abstract class TickedQueue<T> : MonoBehaviour
{
    private readonly TickSnapshotBuffer<T> snapshots = new(256);
    public int PendingSnapshots => snapshots.Count;
    public int SnapshotHighWater => snapshots.HighWater;
    public long StaleSnapshots => snapshots.Stale;
    public long SnapshotOverflows => snapshots.Overflows;
    public long AppliedSnapshots { get; private set; }
    protected string identifier;
    private float nextReport;

    protected virtual void OnEnable()
    {
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    protected virtual void OnDisable()
    {
        if (UnloadWatcher.isQuitting)
            return;
        NetworkLifecycle.Instance.OnTick -= OnTick;
        snapshots.Reset();
        AppliedSnapshots = 0;
        nextReport = 0;
        identifier = string.Empty;
    }

    public void ReceiveSnapshot(T snapshot, uint tick)
    {
        if (!snapshots.TryEnqueue(snapshot, tick))
            NetworkLifecycle.Instance.Client.FailWorldSync(new System.InvalidOperationException(
                $"[{GetID()}] Train snapshot queue exceeded 256 entries; refusing silent event loss."));
    }
    private void OnTick(uint tick)
    {
        if (snapshots.Count == 0 || UnloadWatcher.isUnloading)
            return;
        if (Time.realtimeSinceStartup >= nextReport)
        {
            nextReport = Time.realtimeSinceStartup + 10f;
            Multiplayer.Log($"train_queue id={GetID()} pending={PendingSnapshots} high_water={SnapshotHighWater} stale={StaleSnapshots} overflows={SnapshotOverflows} applied={AppliedSnapshots}");
        }
        try
        {
            snapshots.Drain(32, (snapshot, snapshotTick) =>
            {
                Process(snapshot, snapshotTick);
                AppliedSnapshots++;
            });
        }
        catch (System.Exception error)
        {
            NetworkLifecycle.Instance.Client.FailWorldSync(error);
        }
    }
    public void Clear()
    {
        snapshots.Clear();
    }

    protected abstract void Process(T snapshot, uint snapshotTick);

    private string GetID()
    {
        if (!string.IsNullOrEmpty(identifier))
            return identifier;

        if (this.gameObject == null)
            return "Bad GO";

        TrainCar car = TrainCar.Resolve(this.gameObject);
        int bogie = 0;

        if (car != null && car.Bogies != null && car.Bogies.Length > 0)
            if (this is NetworkedBogie netBogie)
                bogie = (car.Bogies[0] == netBogie.Bogie) ? 1 : 2;

        if (car?.logicCar != null)
            identifier = $"{car?.ID ?? gameObject.GetPath()}{(bogie > 0 ? $" Bogie {bogie}" : "")}";

        return identifier ?? "Unknown";
    }
}



