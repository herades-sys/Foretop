using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Api;
using Foretop.Core.Game;

namespace Foretop.App.ViewModels;

/// <summary>Sidebar pages, in the design's order.</summary>
public enum Page
{
    Dashboard,
    Live,
    PlayerSearch,
    Profiles,
    Ships,
    History,
    Replays,
    Analytics,
    Settings,
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;
    private readonly List<Page> _back = [];
    private readonly List<Page> _forward = [];
    private bool _navigatingHistory;

    public ShellViewModel(AppHost host)
    {
        _host = host;
        Live = new LiveBattleViewModel(host);
        Settings = new SettingsViewModel(host);
        Profiles = new ProfileViewModel(host);
        History = new HistoryViewModel(host);
        Statistics = new StatisticsViewModel(host);
        Personal = new PersonalAnalyticsViewModel(host);
        Analytics = new AnalyticsViewModel(Personal, Statistics);
        Dashboard = new DashboardViewModel(host);
        PlayerSearch = new PlayerSearchViewModel(host);
        Ships = new ShipIntelligenceViewModel(host);
        Replays = new ReplaysViewModel(host);

        IsSidebarCollapsed = host.Settings.Current.SidebarCollapsed;
        UiScale = Services.FontManager.ClampScale(host.Settings.Current.TextScale);
        host.Settings.Changed += s => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            UiScale = Services.FontManager.ClampScale(s.TextScale);
            Services.FontManager.Apply(s.FontFamily);
        });
        Live.ProfileRequested += OpenProfile;
        Statistics.ProfileRequested += OpenProfile;
        PlayerSearch.ProfileRequested += OpenProfile;
        Dashboard.ProfileRequested += OpenProfile;
        Live.HistoryChanged += () => { History.MarkDirty(); Statistics.MarkDirty(); Dashboard.MarkDirty(); };
        host.Ships.Updated += () =>
        {
            HistoryViewModel.ClearRowCache();
            History.MarkDirty();
            Statistics.MarkDirty();
            Dashboard.MarkDirty();
        };
        History.OpenRequested += OpenRecorded;
        Dashboard.BattleRequested += OpenRecorded;
        Personal.BattleRequested += OpenRecorded;
        Personal.PageRequested += p => SelectedPage = p;
        Dashboard.PageRequested += p => SelectedPage = p;
        Ships.SettingsRequested += () => SelectedPage = Page.Settings;
        Profiles.BattleRequested += id =>
        {
            if (host.History.Get(id) is { } record) OpenRecorded(record);
        };

        _currentContent = Dashboard;
        _ = Dashboard.EnsureLoadedAsync();

        Live.BattleShown += live =>
        {
            if (!live || _host.Settings.Current.SwitchToLiveOnBattle) SelectedPage = Page.Live;
        };
        Profiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileViewModel.Title)) UpdateBreadcrumb();
        };
        Ships.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShipIntelligenceViewModel.SectionTitle)) UpdateBreadcrumb();
        };

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(),
            Dispatcher.CurrentDispatcher);
        _statusTimer.Start();
        UpdateStatus();
        UpdateBreadcrumb();
    }

    public LiveBattleViewModel Live { get; }
    public SettingsViewModel Settings { get; }
    public ProfileViewModel Profiles { get; }
    public HistoryViewModel History { get; }
    public StatisticsViewModel Statistics { get; }
    public PersonalAnalyticsViewModel Personal { get; }
    public AnalyticsViewModel Analytics { get; }
    public DashboardViewModel Dashboard { get; }
    public PlayerSearchViewModel PlayerSearch { get; }
    public ShipIntelligenceViewModel Ships { get; }
    public ReplaysViewModel Replays { get; }

    [ObservableProperty] private Page _selectedPage = Page.Dashboard;
    [ObservableProperty] private object _currentContent;

    // Top bar
    [ObservableProperty] private string? _breadcrumbParent;
    [ObservableProperty] private string _breadcrumbTitle = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _apiStatusText = "";
    [ObservableProperty] private string _apiStatusLevel = "Unknown";
    [ObservableProperty] private string _apiStatusTooltip = "";
    [ObservableProperty] private string _regionText = "";

    // Sidebar footer
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _clientText = "";
    [ObservableProperty] private bool _clientRunning;

    /// <summary>Sidebar with icons only; remembered in the settings.</summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    /// <summary>Update notice in the top bar.</summary>
    public Services.UpdateService Updates => _host.Updates;
    public string VersionShort => "Foretop " + Services.AppInfo.Version;

    /// <summary>Interface scale from Settings → Text size.</summary>
    [ObservableProperty] private double _uiScale = 1.0;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        var s = _host.Settings.Clone();
        s.SidebarCollapsed = IsSidebarCollapsed;
        try { _host.Settings.Save(s); } catch (System.IO.IOException) { } // keeps working for this run
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    public void OpenProfile(string name)
    {
        SelectedPage = Page.Profiles;
        _ = Profiles.OpenAsync(name);
    }

    private void OpenRecorded(Core.Data.BattleRecord record)
    {
        Live.ShowStored(record);
        SelectedPage = Page.Live;
    }

    partial void OnSelectedPageChanged(Page oldValue, Page newValue)
    {
        if (!_navigatingHistory)
        {
            _back.Add(oldValue);
            _forward.Clear();
        }
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;

        CurrentContent = PageContent(newValue);
        if (newValue == Page.History) _ = History.EnsureLoadedAsync();
        if (newValue == Page.Replays) _ = Replays.EnsureLoadedAsync();
        if (newValue == Page.Analytics) _ = Analytics.EnsureLoadedAsync();
        if (newValue == Page.Dashboard) _ = Dashboard.EnsureLoadedAsync();
        if (newValue == Page.PlayerSearch) PlayerSearch.OnShown();
        if (newValue == Page.Ships) Ships.OnShown();
        UpdateBreadcrumb();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Step(_back, _forward);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Step(_forward, _back);

    private void Step(List<Page> from, List<Page> to)
    {
        if (from.Count == 0) return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(SelectedPage);
        _navigatingHistory = true;
        SelectedPage = target;
        _navigatingHistory = false;
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    /// <summary>Top-bar search: a ship name opens Ship Intelligence, anything else a player profile.</summary>
    [RelayCommand]
    private void Search()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;
        if (Ships.TryOpenShip(text))
        {
            SelectedPage = Page.Ships;
        }
        else if (text.Length >= 3)
        {
            OpenProfile(text);
        }
        SearchText = "";
    }

    private object PageContent(Page value) => value switch
    {
        Page.Dashboard => Dashboard,
        Page.Live => Live,
        Page.PlayerSearch => PlayerSearch,
        Page.Profiles => Profiles,
        Page.Ships => Ships,
        Page.History => History,
        Page.Replays => Replays,
        Page.Analytics => Analytics,
        Page.Settings => Settings,
        _ => Dashboard,
    };

    private void UpdateBreadcrumb()
    {
        (BreadcrumbParent, BreadcrumbTitle) = SelectedPage switch
        {
            Page.Dashboard => (null, "Dashboard"),
            Page.Live => (null, "Live Battle"),
            Page.PlayerSearch => (null, "Player Search"),
            Page.Profiles => (string.IsNullOrEmpty(Profiles.Title) ? (null, "Player Profiles") : ("Player Profiles", Profiles.Title)),
            Page.Ships => ("Ship Intelligence", Ships.SectionTitle),
            Page.History => (null, "Battle History"),
            Page.Replays => (null, "Replays"),
            Page.Analytics => (null, "Analytics"),
            Page.Settings => (null, "Settings"),
            _ => (null, ""),
        };
    }

    private void UpdateStatus()
    {
        var region = _host.Region;
        RegionText = region switch { Region.Eu => "EU", Region.Na => "NA", Region.Asia => "ASIA", _ => region.ToString() };

        var api = _host.Api;
        (ApiStatusText, ApiStatusLevel) = !api.HasApplicationId
            ? ("No API ID", "Problem")
            : api.Health switch
            {
                ApiHealth.Ok => ("Connected", "Ok"),
                ApiHealth.Offline => ("Offline", "Warning"),
                ApiHealth.Rejected => ("ID rejected", "Problem"),
                _ => ("Not checked yet", "Unknown"),
            };
        var queue = _host.Scheduler.QueueLength;
        ApiStatusTooltip = $"Wargaming API, {Regions.Label(region)} · {api.RequestCount} requests this session" +
                           (queue > 0 ? $" · {queue} queued" : "") +
                           (api.HealthAt is { } at ? $" · last answer {at.LocalDateTime:T}" : "");

        var s = _host.Watcher.Status;
        ClientRunning = s.State is WatcherState.WaitingForBattle or WatcherState.InBattle;
        var name = _host.Settings.Current.PlayerName;
        AccountName = string.IsNullOrEmpty(name) ? "Account not seen yet" : Services.Anonymizer.Name(name);
        var client = !_host.Settings.Current.AutoDetectBattles ? "Detection off"
            : s.State switch
            {
                WatcherState.InBattle => "In battle",
                WatcherState.WaitingForBattle => "Client running",
                WatcherState.GameNotRunning => "Client not running",
                WatcherState.GameNotFound => "Game folder not found",
                _ => "Starting",
            };
        ClientText = $"{client} · {RegionText}";
    }
}
