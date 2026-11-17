using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.GameData;
using Foretop.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

public enum StartupState { Running, Ok, Notice, Warning, Problem, Off }

/// <summary>One line of the startup screen.</summary>
public sealed partial class StartupCheck(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] private StartupState _state = StartupState.Running;
    [ObservableProperty] private string _detail = "Checking…";

    public void Set(StartupState state, string detail) => (State, Detail) = (state, detail);
}

/// <summary>
/// The startup screen: while the main window is built, checks what Foretop depends on — its own data, the game
/// installation, the Wargaming API (one light request with the saved application ID) and GitHub for a newer release.
/// Each check has a time limit; a check still running continues in the background and its result shows in the
/// main window (top bar) as usual.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    public static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(6);
    private readonly AppHost _host;

    public StartupViewModel(AppHost host)
    {
        _host = host;
        Checks = [Data, Game, Api, Updates];
    }

    public string VersionText { get; } = "Version " + Services.AppInfo.Version;
    public StartupCheck Data { get; } = new("Data");
    public StartupCheck Game { get; } = new("Game");
    public StartupCheck Api { get; } = new("Wargaming API");
    public StartupCheck Updates { get; } = new("Updates");
    public ObservableCollection<StartupCheck> Checks { get; }

    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private bool _isDone;

    /// <summary>True when something needs the user's attention (then the screen stays a little longer).</summary>
    public bool HasNews => Checks.Any(c => c.State is StartupState.Notice or StartupState.Warning or StartupState.Problem);

    public async Task RunAsync()
    {
        CheckData();
        await Task.WhenAll(CheckGameAsync(), CheckApiAsync(), CheckUpdatesAsync());
        Status = Checks.Any(c => c.State == StartupState.Problem) ? "Ready, with a problem to look at"
            : HasNews ? "Ready" : "Ready — everything is fine";
        IsDone = true;
    }

    private void CheckData()
    {
        // The settings and the database were opened before this screen appeared; reaching here means they work.
        var battles = 0;
        try { battles = _host.History.Count(); } catch (Exception ex) { _host.Log.LogWarning(ex, "Startup: history count failed"); }
        Data.Set(StartupState.Ok, battles switch
        {
            0 => "Settings and database ready",
            1 => "Settings and database ready · 1 recorded battle",
            _ => $"Settings and database ready · {battles:N0} recorded battles",
        });
    }

    private async Task CheckGameAsync()
    {
        var configured = _host.Settings.Current.GameRoot;
        var (root, build) = await Task.Run(() =>
        {
            var r = string.IsNullOrWhiteSpace(configured) ? GameLocator.FindGameRoot() : configured;
            return (r, r is null ? null : GameFileSystem.NewestBuild(r));
        });
        if (root is null)
            Game.Set(StartupState.Warning, "World of Warships not found — set the game folder in Settings (needed for Live Battle)");
        else if (build is null)
            Game.Set(StartupState.Warning, $"{root}: no game files found — check the game folder in Settings");
        else
            Game.Set(StartupState.Ok, $"{root} · build {build}");
    }

    private async Task CheckApiAsync()
    {
        var api = _host.Api;
        if (!api.HasApplicationId)
        {
            Api.Set(StartupState.Problem, "No application ID — add yours in Settings → Wargaming API");
            return;
        }
        var region = _host.Region;
        using var cts = new CancellationTokenSource(CheckLimit);
        try
        {
            var info = await api.GetEncyclopediaInfoAsync(region, RequestPriority.High, cts.Token);
            Api.Set(StartupState.Ok, $"Connected · {Regions.Label(region)}"
                                      + (string.IsNullOrWhiteSpace(info.GameVersion) ? "" : $" · game version {info.GameVersion}"));
        }
        catch (OperationCanceledException)
        {
            Api.Set(StartupState.Warning, $"No answer within {CheckLimit.TotalSeconds:0} s — Foretop keeps trying and uses its cache meanwhile");
        }
        catch (WgApiException ex) when (api.Health == ApiHealth.Rejected)
        {
            Api.Set(StartupState.Problem, "Application ID rejected: " + ex.Message + " — check it in Settings");
        }
        catch (Exception ex) when (ex is WgApiException or HttpRequestException or IOException)
        {
            Api.Set(StartupState.Warning, "Not reachable (" + ex.Message + ") — Foretop works from its cache until it is back");
        }
        _host.Log.LogInformation("Startup: API {State} ({Detail})", Api.State, Api.Detail);
    }

    private async Task CheckUpdatesAsync()
    {
        var updates = _host.Updates;
        if (!_host.Settings.Current.CheckForUpdates)
        {
            Updates.Set(StartupState.Off, "Not checked — turned off in Settings");
            return;
        }
        var check = updates.CheckAsync();
        if (await Task.WhenAny(check, Task.Delay(CheckLimit)) != check)
        {
            Updates.Set(StartupState.Warning, "GitHub did not answer yet — the check goes on in the background");
            return;
        }
        await check;
        var current = Services.AppInfo.Version;
        switch (updates.LastOutcome)
        {
            case UpdateCheckOutcome.Available when updates.Available is { } release:
                Updates.Set(StartupState.Notice, $"Foretop {release.Version.ToString(3)} is available (you have {current}) — install it from the top bar");
                break;
            case UpdateCheckOutcome.UpToDate:
                Updates.Set(StartupState.Ok, $"Up to date · {current} is the latest release");
                break;
            default:
                Updates.Set(StartupState.Warning, string.IsNullOrWhiteSpace(updates.StatusText) ? "Could not check" : updates.StatusText);
                break;
        }
    }
}
