using System.Diagnostics;
using System.IO;
using Foretop.App.Services;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Foretop.App.ViewModels;

public sealed record RegionOption(Region Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of the detection check, ready for display.</summary>
public sealed record CheckRow(string Name, string Detail, string Symbol, string Level);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private AppSettings _draft;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _draft = host.Settings.Clone();
        Load();
    }

    public IReadOnlyList<RegionOption> RegionOptions { get; } =
        [new(Region.Eu, "Europe"), new(Region.Na, "North America"), new(Region.Asia, "Asia")];

    [ObservableProperty] private Region _region;
    [ObservableProperty] private string _applicationId = "";
    [ObservableProperty] private string _gameRoot = "";
    [ObservableProperty] private string _playerName = "";
    [ObservableProperty] private string _detectedRoot = "";
    [ObservableProperty] private bool _autoDetectBattles;
    [ObservableProperty] private bool _switchToLiveOnBattle;
    [ObservableProperty] private bool _compactMode;
    [ObservableProperty] private string _theme = "System";

    public IReadOnlyList<string> ThemeOptions => Services.ThemeManager.Options;

    /// <summary>Installed fonts, read when Settings is first shown.</summary>
    public IReadOnlyList<Services.FontOption> FontOptions => _fontOptions ??= Services.FontManager.InstalledFonts();
    private IReadOnlyList<Services.FontOption>? _fontOptions;
    public IReadOnlyList<Services.ScaleOption> TextScaleOptions => Services.FontManager.TextScales;
    [ObservableProperty] private Services.FontOption? _font;
    [ObservableProperty] private Services.ScaleOption? _textScale;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _showBots;
    [ObservableProperty] private bool _checkForUpdates;
    public Services.UpdateService Updates => _host.Updates;
    [ObservableProperty] private double _apiRatePerSecond;
    [ObservableProperty] private int _playerCacheMinutes;
    [ObservableProperty] private int _clanCacheHours;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _saveResult = "";
    [ObservableProperty] private bool _isTesting;

    /// <summary>
    /// A saved ID is hidden behind a "Saved" line; the input box only appears when there is no ID yet or after "Change".
    /// </summary>
    [ObservableProperty] private bool _isEditingApplicationId;

    [RelayCommand]
    private void EditApplicationId() => IsEditingApplicationId = true;

    public string DataFolder => AppPaths.DataDir;
    public string VersionText => $"Foretop {AppInfo.Version}";

    public System.Collections.ObjectModel.ObservableCollection<CheckRow> Checks { get; } = [];
    [ObservableProperty] private bool _isChecking;

    /// <summary>Runs the detection check (file system and process list) plus an API test, without saving anything.</summary>
    [RelayCommand]
    private async Task RunChecks()
    {
        IsChecking = true;
        Checks.Clear();
        var root = string.IsNullOrWhiteSpace(GameRoot) ? null : GameRoot.Trim();
        var results = await Task.Run(() => GameDiagnostics.Run(root));
        foreach (var c in results) Checks.Add(Row(c.Name, c.Detail, c.Level));

        if (string.IsNullOrWhiteSpace(ApplicationId))
            Checks.Add(Row("Wargaming API", "No application ID entered.", CheckLevel.Problem));
        else
        {
            var (ok, message) = await _host.Api.TestApplicationIdAsync(Region, ApplicationId.Trim(), CancellationToken.None);
            Checks.Add(Row("Wargaming API", $"{RegionOptions.First(r => r.Value == Region).Label}: {message}", ok ? CheckLevel.Ok : CheckLevel.Problem));
        }
        Checks.Add(Row("Watcher", _host.Watcher.Status.Message, CheckLevel.Info));
        IsChecking = false;

        static CheckRow Row(string name, string detail, CheckLevel level) => new(name, detail, level switch
        {
            CheckLevel.Ok => "✓",
            CheckLevel.Warning => "!",
            CheckLevel.Problem => "✗",
            _ => "i",
        }, level.ToString());
    }
    public string CacheInfo => $"{_host.Cache.Count():N0} cached API responses · {_host.Ships.Count:N0} ships in catalogue";

    private void Load()
    {
        Region = _draft.Region;
        ApplicationId = _host.Settings.GetApplicationId() ?? "";
        IsEditingApplicationId = string.IsNullOrWhiteSpace(ApplicationId);
        GameRoot = _draft.GameRoot ?? "";
        PlayerName = _draft.PlayerName ?? "";
        AutoDetectBattles = _draft.AutoDetectBattles;
        SwitchToLiveOnBattle = _draft.SwitchToLiveOnBattle;
        CompactMode = _draft.CompactMode;
        Theme = Services.ThemeManager.Options.Contains(_draft.Theme) ? _draft.Theme : "System";
        Font = FontOptions.FirstOrDefault(f => string.Equals(f.Name, _draft.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? FontOptions[0];
        TextScale = TextScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - _draft.TextScale) < 0.001) ?? TextScaleOptions[1];
        StartWithWindows = _draft.StartWithWindows;
        StartMinimized = _draft.StartMinimized;
        ShowBots = _draft.ShowBots;
        CheckForUpdates = _draft.CheckForUpdates;
        ApiRatePerSecond = _draft.ApiRatePerSecond;
        PlayerCacheMinutes = _draft.PlayerCacheMinutes;
        ClanCacheHours = _draft.ClanCacheHours;
        DetectedRoot = GameLocator.FindGameRoot() ?? "not found";
    }

    [RelayCommand]
    private void Save()
    {
        var s = _host.Settings.Clone();
        s.Region = Region;
        _host.Settings.SetApplicationId(s, ApplicationId);
        s.GameRoot = string.IsNullOrWhiteSpace(GameRoot) ? null : GameRoot.Trim();
        s.PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? null : PlayerName.Trim();
        s.AutoDetectBattles = AutoDetectBattles;
        s.SwitchToLiveOnBattle = SwitchToLiveOnBattle;
        s.CompactMode = CompactMode;
        s.Theme = Theme;
        s.FontFamily = Font?.Name;
        s.TextScale = TextScale?.Value ?? 1.0;
        s.StartWithWindows = StartWithWindows;
        s.StartMinimized = StartMinimized;
        s.ShowBots = ShowBots;
        s.CheckForUpdates = CheckForUpdates;
        s.ApiRatePerSecond = Math.Clamp(ApiRatePerSecond, 1, 10);
        s.PlayerCacheMinutes = Math.Clamp(PlayerCacheMinutes, 0, 7 * 24 * 60);
        s.ClanCacheHours = Math.Clamp(ClanCacheHours, 0, 24 * 30);

        try
        {
            _host.Settings.Save(s);
            StartupRegistration.Apply(s.StartWithWindows, s.StartMinimized);
            _draft = s;
            ApiRatePerSecond = s.ApiRatePerSecond;
            _host.Watcher.Poke();
            SaveResult = "Saved.";
            if (!string.IsNullOrWhiteSpace(ApplicationId)) IsEditingApplicationId = false;
            _ = _host.Ships.EnsureLoadedAsync(s.Region, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveResult = "Could not save: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestApplicationId()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId))
        {
            TestResult = "Enter an application ID first.";
            return;
        }
        IsTesting = true;
        TestResult = "Testing…";
        var (ok, message) = await _host.Api.TestApplicationIdAsync(Region, ApplicationId.Trim(), CancellationToken.None);
        TestResult = (ok ? "✓ " : "✗ ") + message;
        IsTesting = false;
    }

    [RelayCommand]
    private void BrowseGameRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Select the World of Warships folder" };
        if (dlg.ShowDialog() != true) return;
        GameRoot = dlg.FolderName;
        if (!GameLocator.LooksLikeGameRoot(GameRoot))
            SaveResult = "That folder does not look like a World of Warships install (no game_info.xml or bin + replays).";
    }

    [RelayCommand]
    private void OpenDeveloperPortal() =>
        Process.Start(new ProcessStartInfo("https://developers.wargaming.net/applications/") { UseShellExecute = true });

    [RelayCommand]
    private void OpenLogFolder() =>
        Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });

    [RelayCommand]
    private void InvalidateCache()
    {
        _host.Cache.Invalidate();
        SaveResult = "Cached statistics marked stale; they are fetched again on next use.";
        OnPropertyChanged(nameof(CacheInfo));
    }
}
