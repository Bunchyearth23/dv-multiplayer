using System;
using System.Collections;
using Multiplayer.Utils;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using LiteNetLib.Utils;

internal static class LoadingTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Loading invariant failed."); }

    public static void CoroutineDisposalFailureIsReportedOnce()
    {
        int reports = 0;
        var inner = new FaultingEnumerator(false);
        var guarded = new GuardedRoutine(inner, error =>
        {
            Check(inner.Disposals == 1 && error is InvalidOperationException);
            reports++;
        });
        Check(guarded.MoveNext()); guarded.Dispose(); guarded.Dispose();
        Check(!guarded.MoveNext() && reports == 1 && inner.Disposals == 1);
    }

    public static void CoroutineAndDisposalFailuresArePreserved()
    {
        Exception reported = null;
        var inner = new FaultingEnumerator(true);
        var guarded = new GuardedRoutine(inner, error => reported = error);
        Check(!guarded.MoveNext());
        Check(reported is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
            aggregate.InnerExceptions[0] is TimeoutException && aggregate.InnerExceptions[1] is InvalidOperationException);
        guarded.Dispose(); Check(inner.Disposals == 1);
    }

    public static void FailureNotificationRunsAfterResourceRelease()
    {
        bool released = false; int reports = 0;
        IEnumerator Work()
        {
            try { yield return null; throw new TimeoutException(); }
            finally { released = true; }
        }
        GuardedRoutine guarded = null;
        guarded = new GuardedRoutine(Work(), _ =>
        {
            Check(released); reports++; guarded.Dispose();
            throw new Exception("Caller failure");
        });
        Check(guarded.MoveNext());
        try { guarded.MoveNext(); } catch (Exception ex) { Check(ex.Message == "Caller failure"); }
        Check(reports == 1 && !guarded.MoveNext());
    }

    private sealed class FaultingEnumerator : IEnumerator, IDisposable
    {
        private readonly bool failMove;
        public int Disposals;
        public FaultingEnumerator(bool failMove) { this.failMove = failMove; }
        public object Current => null;
        public bool MoveNext() { if (failMove) throw new TimeoutException("Loading failed"); return true; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { Disposals++; throw new InvalidOperationException("Cleanup failed"); }
    }

    public static void ManifestRequiresAppliedObjects()
    {
        var manifest = new LoadingManifest();
        manifest.MarkApplied(2); manifest.MarkApplied(2); manifest.MarkApplied(99);
        Check(!manifest.IsComplete);
        var ids = new ushort[] { 1, 2 };
        manifest.SetExpected(ids); ids[0] = 99;
        Check(!manifest.IsComplete && manifest.Missing.Length == 1 && manifest.Missing[0] == 1);
        manifest.MarkApplied(1); Check(manifest.IsComplete);
        manifest.SetExpected(new ushort[] { 2, 1 }); Check(manifest.IsComplete);
    }

    public static void InvalidManifestCannotReplaceExpectedObjects()
    {
        var manifest = new LoadingManifest();
        manifest.SetExpected(new ushort[] { 1 });
        foreach (var ids in new[] { new ushort[] { 0 }, new ushort[] { 1, 1 }, new ushort[] { 2 }, null })
        {
            try { manifest.SetExpected(ids); throw new Exception("Invalid manifest accepted"); }
            catch (ArgumentException) { }
        }
        Check(!manifest.IsComplete && manifest.Missing[0] == 1);
        var empty = new LoadingManifest(); empty.SetExpected(Array.Empty<ushort>()); Check(empty.IsComplete);
    }

    public static void JobManifestWireRoundTrip()
    {
        var processor = new NetPacketProcessor();
        ushort[] received = null;
        processor.SubscribeReusable<ClientboundJobManifestPacket>(packet => received = packet.JobIds);
        var writer = new NetDataWriter();
        processor.Write(writer, new ClientboundJobManifestPacket { JobIds = new ushort[] { 1, 400, 65535 } });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Check(received.Length == 3 && received[0] == 1 && received[1] == 400 && received[2] == 65535);
    }

    public static void StalledLoadingReportsStageAndProgress()
    {
        double now = 0;
        var deadline = new LoadingDeadline("trainsets", 10, 30, () => now);
        deadline.Check("2/5"); now = 9; deadline.Check("2/5"); now = 10;
        try { deadline.Check("2/5"); throw new Exception("Timeout not raised"); }
        catch (TimeoutException ex) { Check(ex.Message.Contains("trainsets") && ex.Message.Contains("2/5")); }
    }

    public static void ProgressExtendsIdleButNotTotalDeadline()
    {
        double now = 0;
        var deadline = new LoadingDeadline("test", 10, 25, () => now);
        deadline.Check("0"); now = 9; deadline.Check("1"); now = 18; deadline.Check("2");
        now = 24; deadline.Check("3"); now = 25;
        try { deadline.Check("4"); throw new Exception("Total timeout not raised"); }
        catch (TimeoutException) { }
    }

    public static void CancelledRoutineCannotResume()
    {
        int steps = 0, disposed = 0;
        IEnumerator Work()
        {
            try { steps++; yield return null; steps++; }
            finally { disposed++; }
        }
        var routine = new GuardedRoutine(Work(), _ => throw new Exception("Unexpected failure"));
        Check(routine.MoveNext()); routine.Dispose(); routine.Dispose();
        Check(!routine.MoveNext() && steps == 1 && disposed == 1);
    }

    public static void RoutineFailureRunsCleanupAndReportsOnce()
    {
        int disposed = 0, failures = 0;
        IEnumerator Work()
        {
            try { yield return null; throw new TimeoutException("world state"); }
            finally { disposed++; }
        }
        GuardedRoutine routine = null;
        routine = new GuardedRoutine(Work(), ex => { Check(ex is TimeoutException); failures++; routine.Dispose(); });
        Check(routine.MoveNext()); Check(!routine.MoveNext()); routine.Dispose(); Check(!routine.MoveNext());
        Check(failures == 1 && disposed == 1);
    }

    public static void ReentrantCancellationStopsAtYield()
    {
        int disposed = 0, laterSteps = 0;
        GuardedRoutine routine = null;
        IEnumerator Work()
        {
            try { routine.Dispose(); yield return null; laterSteps++; }
            finally { disposed++; }
        }
        routine = new GuardedRoutine(Work(), _ => throw new Exception("Unexpected failure"));
        Check(!routine.MoveNext() && disposed == 1 && laterSteps == 0);
    }
}
