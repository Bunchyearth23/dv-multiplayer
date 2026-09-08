using System;

namespace Multiplayer.Networking.Data.RPCs;

/// <summary>
/// Runs optional post-commit work without allowing it to suppress an RPC's
/// terminal response. The response delegate is invoked exactly once.
/// </summary>
public static class RpcTerminalResponse
{
    public static void Send(Action postCommit, Action<Exception> reportFailure, Action respond)
    {
        if (respond == null) throw new ArgumentNullException(nameof(respond));

        try
        {
            postCommit?.Invoke();
        }
        catch (Exception ex)
        {
            try { reportFailure?.Invoke(ex); }
            catch { }
        }
        finally
        {
            respond();
        }
    }
}
