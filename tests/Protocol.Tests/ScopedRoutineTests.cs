using System;
using System.Collections;
using Multiplayer.Utils;

internal static class ScopedRoutineTests
{
    private sealed class Scope : IDisposable
    {
        private readonly Action exit;
        public Scope(Action enter, Action exit) { this.exit = exit; enter(); }
        public void Dispose() => exit();
    }

    private static void Check(bool value) { if (!value) throw new Exception("Iterator scope leaked or was missing."); }

    public static void ScopeDoesNotLeakAcrossYields()
    {
        int depth = 0;
        IEnumerator Inner()
        {
            Check(depth == 1);
            yield return "first";
            Check(depth == 1);
            yield return "second";
        }
        using (var routine = new ScopedRoutine(Inner(), () => new Scope(() => depth++, () => depth--)))
        {
            Check(routine.MoveNext() && depth == 0 && (string)routine.Current == "first");
            Check(routine.MoveNext() && depth == 0 && (string)routine.Current == "second");
            Check(!routine.MoveNext() && depth == 0);
        }
        Check(depth == 0);
    }

    public static void FailureAndCancellationReleaseScope()
    {
        int depth = 0, cleaned = 0;
        IEnumerator Inner()
        {
            try { yield return null; throw new InvalidOperationException("injected"); }
            finally { Check(depth == 1); cleaned++; }
        }
        var failing = new ScopedRoutine(Inner(), () => new Scope(() => depth++, () => depth--));
        Check(failing.MoveNext());
        try { failing.MoveNext(); throw new Exception("Failure missing."); }
        catch (InvalidOperationException) { Check(depth == 0 && cleaned == 1); }
        failing.Dispose();
        var cancelled = new ScopedRoutine(Inner(), () => new Scope(() => depth++, () => depth--));
        Check(cancelled.MoveNext());
        cancelled.Dispose(); cancelled.Dispose();
        Check(depth == 0 && cleaned == 2 && !cancelled.MoveNext());
    }

    public static void NestedScopesRestoreOuterContext()
    {
        int depth = 0;
        IEnumerator Child() { Check(depth == 2); yield return null; }
        IEnumerator Parent()
        {
            using (var child = new ScopedRoutine(Child(), () => new Scope(() => depth++, () => depth--)))
            {
                Check(child.MoveNext() && depth == 1);
            }
            Check(depth == 1);
            yield return null;
        }
        using (var parent = new ScopedRoutine(Parent(), () => new Scope(() => depth++, () => depth--)))
            Check(parent.MoveNext() && depth == 0);
        Check(depth == 0);
    }
}
