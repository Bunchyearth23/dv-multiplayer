using System;
using Multiplayer.Networking.Data.RPCs;

internal static class RpcTerminalResponseTests
{
    public static void PostCommitFailureStillRespondsExactlyOnce()
    {
        int reports = 0;
        int responses = 0;

        RpcTerminalResponse.Send(
            () => throw new InvalidOperationException("notification failed"),
            ex =>
            {
                Check(ex is InvalidOperationException, "Wrong post-commit exception reported");
                reports++;
            },
            () => responses++);

        Check(reports == 1, "Post-commit failure was not reported exactly once");
        Check(responses == 1, "Terminal response was not sent exactly once");
    }

    public static void ReporterFailureStillRespondsExactlyOnce()
    {
        int responses = 0;
        RpcTerminalResponse.Send(
            () => throw new InvalidOperationException("notification failed"),
            _ => throw new InvalidOperationException("logger failed"),
            () => responses++);

        Check(responses == 1, "Reporter failure suppressed or duplicated the terminal response");
    }

    public static void SuccessStillRespondsExactlyOnce()
    {
        int postCommits = 0;
        int responses = 0;
        RpcTerminalResponse.Send(() => postCommits++, null, () => responses++);
        Check(postCommits == 1, "Post-commit callback did not run once");
        Check(responses == 1, "Terminal response did not run once");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
