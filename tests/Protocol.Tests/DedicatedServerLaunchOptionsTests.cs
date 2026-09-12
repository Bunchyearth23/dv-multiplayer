using System;
using Multiplayer.Networking.Data;

internal static class DedicatedServerLaunchOptionsTests
{
    public static void DedicatedCommandLineIsParsedWithoutUnity()
    {
        var options = DedicatedServerLaunchOptions.Parse(new[]
        {
            "DerailValley.exe", "-dvmp-dedicated", "-dvmp-game-mode=career", "-dvmp-user=Dedicated",
            "-dvmp-session=Persistent", "-dvmp-save-uid=7", "-dvmp-port=25000", "-dvmp-max-players=8"
        });

        Check(options.Enabled && options.IsValid, "valid dedicated command line is accepted");
        Check(options.GameMode == "Career" && options.SaveUid == 7 && options.Port == 25000, "dedicated values are canonicalized");
    }

    public static void UnsafeDedicatedCommandLineIsRejected()
    {
        var options = DedicatedServerLaunchOptions.Parse(new[]
        {
            "-dvmp-dedicated", "-dvmp-game-mode=Career", "-dvmp-port=80", "-dvmp-save=one", "-dvmp-save-uid=1"
        });

        Check(!options.IsValid && !string.IsNullOrEmpty(options.Error), "invalid dedicated command line is rejected before Unity starts a world");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
