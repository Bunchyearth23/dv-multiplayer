using System;
using System.Linq;
using DV;
using DV.Common;
using DV.Scenarios.Common;
using DV.UI;
using DV.UserManagement;
using DV.UserManagement.Data;
using DV.Utils;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.UI.ServerBrowser;
using Multiplayer.Networking.Data;
using UnityEngine;

namespace Multiplayer.Patches.MainMenu;

/// <summary>Starts a configured dedicated world after the menu infrastructure is ready, without using its UI.</summary>
[HarmonyPatch(typeof(DV.UI.MainMenu), "Start")]
public static class DedicatedServerStartupPatch
{
    private static bool started;

    private static void Postfix()
    {
        var options = DedicatedServerLaunchOptions.Current;
        if (!options.Enabled || started)
            return;

        started = true;
        if (!options.IsValid)
        {
            Fail(options.Error);
            return;
        }

        try
        {
            ApplyServerSettings(options);
            var user = ResolveUser(options);
            var session = ResolveSession(user, options);
            var save = ResolveSave(session, options);
            var startData = AStartGameData.Continue(save, useSessionDifficulty: true);

            user.SelectSession(session);
            startData.MakeCurrent();
            NetworkLifecycle.Instance.IsSinglePlayer = false;
            NetworkLifecycle.Instance.serverData = BuildServerData(options, save, startData.DifficultyToUse);

            Multiplayer.Log($"Dedicated startup selected user '{user.Name}', save '{save.Name}' ({save.UID}).");
            SceneSwitcher.SwitchToScene(DVScenes.Game);
        }
        catch (Exception exception)
        {
            Fail("Dedicated startup failed: " + exception.Message, exception);
        }
    }

    private static User ResolveUser(DedicatedServerLaunchOptions options)
    {
        var manager = SingletonBehaviour<UserManager>.Instance;
        if (manager == null || !manager.IsReady)
            throw new InvalidOperationException("User profiles are not ready.");

        var user = options.UserName == null
            ? manager.CurrentUser
            : manager.Users.SingleOrDefault(candidate => string.Equals(candidate.Name, options.UserName, StringComparison.Ordinal));
        if (user == null)
            throw new InvalidOperationException($"User profile '{options.UserName}' was not found.");
        if (user != manager.CurrentUser)
            manager.SwitchUser(user);
        return user;
    }

    private static IGameSession ResolveSession(User user, DedicatedServerLaunchOptions options)
    {
        if (!user.Sessions.TryGetValue(options.GameMode, out var sessions))
            throw new InvalidOperationException($"Game mode '{options.GameMode}' is unavailable for user '{user.Name}'.");

        user.CurrentSessionPerMode.TryGetValue(options.GameMode, out var currentSession);
        var session = options.SessionName == null
            ? currentSession
            : sessions.OfType<GameSession>().SingleOrDefault(candidate => string.Equals(candidate.Name, options.SessionName, StringComparison.Ordinal));
        if (session == null)
            throw new InvalidOperationException($"Session '{options.SessionName ?? "current"}' was not found for game mode '{options.GameMode}'.");
        return session;
    }

    private static ISaveGame ResolveSave(IGameSession session, DedicatedServerLaunchOptions options)
    {
        var save = options.SaveUid.HasValue
            ? session.Saves.SingleOrDefault(candidate => candidate.UID == options.SaveUid.Value)
            : options.SaveName != null
                ? session.Saves.SingleOrDefault(candidate => string.Equals(candidate.Name, options.SaveName, StringComparison.Ordinal))
                : session.LatestSave;
        if (save == null)
            throw new InvalidOperationException($"Save '{options.SaveName ?? options.SaveUid?.ToString() ?? "latest"}' was not found in the selected session.");
        return save;
    }

    private static void ApplyServerSettings(DedicatedServerLaunchOptions options)
    {
        if (options.Port.HasValue) Multiplayer.Settings.Port = options.Port.Value;
        if (options.ServerName != null) Multiplayer.Settings.ServerName = options.ServerName;
        if (options.Password != null) Multiplayer.Settings.Password = options.Password;
        if (options.MaxPlayers.HasValue) Multiplayer.Settings.MaxPlayers = options.MaxPlayers.Value;
        if (options.Details != null) Multiplayer.Settings.Details = options.Details;
        if (options.Visibility != null && Enum.TryParse(options.Visibility, true, out ServerVisibility visibility))
            Multiplayer.Settings.Visibility = visibility;
    }

    private static LobbyServerData BuildServerData(DedicatedServerLaunchOptions options, ISaveGame save, IDifficulty difficulty)
    {
        var requiredMods = ModCompatibilityManager.Instance.GetLocalMods();
        if (requiredMods == null)
            throw new InvalidOperationException("Required-mod inventory could not be built.");

        return new LobbyServerData
        {
            port = Multiplayer.Settings.Port,
            Name = string.IsNullOrWhiteSpace(Multiplayer.Settings.ServerName) ? "Derail Valley dedicated server" : Multiplayer.Settings.ServerName,
            HasPassword = !string.IsNullOrEmpty(Multiplayer.Settings.Password),
            Visibility = Multiplayer.Settings.Visibility,
            GameMode = LobbyServerData.GetGameModeFromString(save.GameMode),
            Difficulty = LobbyServerData.GetDifficultyFromString(difficulty.Name),
            TimePassed = "N/A",
            CurrentPlayers = 0,
            MaxPlayers = Multiplayer.Settings.MaxPlayers,
            RequiredMods = requiredMods,
            GameVersion = Multiplayer.LocalBuildInfo,
            MultiplayerVersion = Multiplayer.Ver,
            ServerDetails = Multiplayer.Settings.Details ?? string.Empty
        };
    }

    private static void Fail(string message, Exception exception = null)
    {
        Multiplayer.LogError(message);
        if (exception != null) Multiplayer.LogException(message, exception);
        Application.Quit();
    }
}
