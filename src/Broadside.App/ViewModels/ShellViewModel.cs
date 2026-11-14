using System.Windows.Threading;
using Broadside.Core.Game;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Broadside.App.ViewModels;

public enum Page
{
    Live,
    Profiles,
    History,
    Statistics,
    Settings,
}

public sealed partial class PlaceholderViewModel(string title, string text) : ObservableObject
{
    public string Title { get; } = title;
    public string Text { get; } = text;
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;

    public ShellViewModel(AppHost host)
    {
        _host = host;
        Live = new LiveBattleViewModel(host);
        Settings = new SettingsViewModel(host);
        Profiles = new ProfileViewModel(host);
        History = new HistoryViewModel(host);
        Statistics = new StatisticsViewModel(host);

        Live.ProfileRequested += name => OpenProfile(name);
        Statistics.ProfileRequested += name => OpenProfile(name);
        Live.HistoryChanged += () => { History.MarkDirty(); Statistics.MarkDirty(); };
        host.Ships.Updated += () =>
        {
            HistoryViewModel.ClearRowCache();
            History.MarkDirty();
            Statistics.MarkDirty();
        };
        History.OpenRequested += record =>
        {
            Live.ShowStored(record);
            SelectedPage = Page.Live;
        };
        Profiles.BattleRequested += id =>
        {
            if (host.History.Get(id) is { } record)
            {
                Live.ShowStored(record);
                SelectedPage = Page.Live;
            }
        };
        _currentContent = Live;

        Live.BattleShown += live =>
        {
            if (!live || _host.Settings.Current.SwitchToLiveOnBattle) SelectedPage = Page.Live;
        };
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(),
            Dispatcher.CurrentDispatcher);
        _statusTimer.Start();
        UpdateStatus();
    }

    public LiveBattleViewModel Live { get; }
    public SettingsViewModel Settings { get; }
    public ProfileViewModel Profiles { get; }
    public HistoryViewModel History { get; }
    public StatisticsViewModel Statistics { get; }

    public void OpenProfile(string name)
    {
        SelectedPage = Page.Profiles;
        _ = Profiles.OpenAsync(name);
    }

    [ObservableProperty] private Page _selectedPage = Page.Live;
    [ObservableProperty] private object _currentContent;
    [ObservableProperty] private string _watcherText = "";
    [ObservableProperty] private string _apiText = "";
    [ObservableProperty] private bool _gameDetected;

    partial void OnSelectedPageChanged(Page value)
    {
        CurrentContent = PageContent(value);
        if (value == Page.History) _ = History.EnsureLoadedAsync();
        if (value == Page.Statistics) _ = Statistics.EnsureLoadedAsync();
    }

    private object PageContent(Page value) => value switch
    {
        Page.Live => Live,
        Page.Profiles => Profiles,
        Page.History => History,
        Page.Statistics => Statistics,
        Page.Settings => Settings,
        _ => Live,
    };

    private void UpdateStatus()
    {
        var s = _host.Watcher.Status;
        WatcherText = _host.Settings.Current.AutoDetectBattles ? s.Message : "Automatic battle detection is off";
        GameDetected = s.State is WatcherState.WaitingForBattle or WatcherState.InBattle;
        var queue = _host.Scheduler.QueueLength;
        ApiText = $"API {Core.Api.Regions.Label(_host.Region)} · {_host.Api.RequestCount} requests" +
                  (queue > 0 ? $" · {queue} queued" : "");
    }
}
