using System;
using System.Collections.Generic;
using System.Globalization;

namespace Multiplayer.Networking.Data;

/// <summary>
/// Immutable command-line contract between the dedicated-server launcher and the mod.
/// It deliberately contains no Unity types so the contract can be tested outside the game.
/// </summary>
public sealed class DedicatedServerLaunchOptions
{
    public const string DedicatedArgument = "-dvmp-dedicated";

    public static DedicatedServerLaunchOptions Current { get; } = Parse(Environment.GetCommandLineArgs());

    public bool Enabled { get; private set; }
    public string UserName { get; private set; }
    public string GameMode { get; private set; }
    public string SessionName { get; private set; }
    public string SaveName { get; private set; }
    public int? SaveUid { get; private set; }
    public int? Port { get; private set; }
    public string ServerName { get; private set; }
    public string Password { get; private set; }
    public int? MaxPlayers { get; private set; }
    public string Details { get; private set; }
    public string Visibility { get; private set; }
    public string Error { get; private set; }

    public bool IsValid => Enabled && string.IsNullOrEmpty(Error);

    public static DedicatedServerLaunchOptions Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool enabled = false;

        foreach (var argument in arguments ?? Array.Empty<string>())
        {
            if (string.Equals(argument, DedicatedArgument, StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                continue;
            }

            if (!argument.StartsWith("-dvmp-", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = argument.IndexOf('=');
            if (separator < 0)
                separator = argument.IndexOf(':');
            if (separator <= 6)
                continue;

            values[argument.Substring(6, separator - 6)] = argument.Substring(separator + 1);
        }

        if (!enabled)
            return new DedicatedServerLaunchOptions { Enabled = false };

        var options = new DedicatedServerLaunchOptions
        {
            Enabled = true,
            UserName = Value(values, "user"),
            GameMode = CanonicalGameMode(Value(values, "game-mode")),
            SessionName = Value(values, "session"),
            SaveName = Value(values, "save"),
            ServerName = Value(values, "server-name"),
            Password = Value(values, "password"),
            Details = Value(values, "details"),
            Visibility = Value(values, "visibility"),
            SaveUid = ParseInt(values, "save-uid"),
            Port = ParseInt(values, "port"),
            MaxPlayers = ParseInt(values, "max-players")
        };

        options.Error = Validate(options);
        return options;
    }

    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? ParseInt(Dictionary<string, string> values, string key)
    {
        var value = Value(values, key);
        if (value == null)
            return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : -1;
    }

    private static string CanonicalGameMode(string gameMode)
    {
        if (string.Equals(gameMode, "career", StringComparison.OrdinalIgnoreCase)) return "Career";
        if (string.Equals(gameMode, "freeroam", StringComparison.OrdinalIgnoreCase)) return "FreeRoam";
        return gameMode;
    }

    private static string Validate(DedicatedServerLaunchOptions options)
    {
        if (options.GameMode is not ("Career" or "FreeRoam"))
            return "A dedicated server requires -dvmp-game-mode=Career or FreeRoam.";
        if (options.Port is < 1024 or > 65535)
            return "The dedicated server port must be between 1024 and 65535.";
        if (options.MaxPlayers is < 1 or > 255)
            return "The dedicated server max-player count must be between 1 and 255.";
        if (options.SaveUid is < 0)
            return "The dedicated server save UID must be a positive integer.";
        if (options.SaveUid.HasValue && options.SaveName != null)
            return "Select a dedicated save by UID or name, not both.";
        return null;
    }
}
