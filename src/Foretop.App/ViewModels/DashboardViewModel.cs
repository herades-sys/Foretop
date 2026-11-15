using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

/// <summary>Overview: client and API status, the last battle, recent battles and frequent players. Only data the app already has.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _dirty = true;

    /// <summary>Battles further apart than this start a new session.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromHours(1);

    private Dictionary<string, HistoryRow> _historyByKey = new();

    public DashboardViewModel(AppHost host)
    {
        _host = host;
        var ui = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => UpdateStatus(), ui);
        timer.Start();
        UpdateStatus();
        host.Results.Changed += () => ui.BeginInvoke(() =>
        {
            _dirty = true; // a new battle also means a new history row; this reloads both
            _ = EnsureLoadedAsync();
        });
    }

    public ObservableCollection<OwnBattleRow> OwnBattles { get; } = [];
    [ObservableProperty] private ResultSummaryView? _sessionResults;
    [ObservableProperty] private ResultSummaryView? _todayResults;
    [ObservableProperty] private bool _hasResults;

    /// <summary>Your results this session and today, from the stored battle results.</summary>
    private void LoadResults()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var rows = _host.Results.Store.Since(now.AddDays(-2), 200);
            HasResults = rows.Count > 0;
            var session = ResultMath.Session(rows, SessionGap);
            if (session.Count > 0 && now - session[0].StartedAt > SessionGap) session = []; // last session is over
            var midnight = new DateTimeOffset(now.Date, now.Offset);
            var today = rows.Where(r => r.StartedAt >= midnight).ToList();
            bool Test(BattleResult r) => _host.ShipData.FindById(r.ShipId)?.IsTestShip == true;

            // Battles in ships in testing show no values, so they stay out of the totals too.
            SessionResults = new ResultSummaryView("THIS SESSION",
                session.Count == 0 ? "No battle in the last hour" : $"Since {session[^1].StartedAt.LocalDateTime:t}",
                ResultTotals.Of(session.Where(r => !Test(r)).ToList()));
            TodayResults = new ResultSummaryView("TODAY", now.ToString("D", System.Globalization.CultureInfo.CurrentCulture),
                ResultTotals.Of(today.Where(r => !Test(r)).ToList()));

            OwnBattles.Clear();
            foreach (var r in rows.Take(12))
                OwnBattles.Add(new OwnBattleRow(r, _host.Ships.Get(r.ShipId), _historyByKey.GetValueOrDefault(r.BattleKey), _host.ShipData.FindById(r.ShipId)));
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Loading battle results failed");
        }
    }

    public event Action<string>? ProfileRequested;
    public event Action<Core.Data.BattleRecord>? BattleRequested;
    public event Action<Page>? PageRequested;

    public ObservableCollection<HistoryRow> RecentBattles { get; } = [];
    public ObservableCollection<EncounteredPlayer> FrequentPlayers { get; } = [];

    [ObservableProperty] private string _clientTitle = "";
    [ObservableProperty] private string _clientDetail = "";
    [ObservableProperty] private string _clientLevel = "Unknown";
    [ObservableProperty] private string _apiTitle = "";
    [ObservableProperty] private string _apiDetail = "";
    [ObservableProperty] private string _apiLevel = "Unknown";
    [ObservableProperty] private HistoryRow? _lastBattle;
    [ObservableProperty] private string _historySummary = "";
    [ObservableProperty] private bool _hasHistory;

    public void MarkDirty() => _dirty = true;

    public async Task EnsureLoadedAsync()
    {
        UpdateStatus();
        if (!_dirty) return;
        _dirty = false;
        try
        {
            var rows = await Task.Run(() => HistoryViewModel.LoadRows(_host));
            _historyByKey = rows.GroupBy(r => r.Record.BattleKey).ToDictionary(g => g.Key, g => g.First());
            LoadResults();
            RecentBattles.Clear();
            foreach (var r in rows.Take(6)) RecentBattles.Add(r);
            LastBattle = rows.FirstOrDefault();
            HasHistory = rows.Count > 0;
            HistorySummary = rows.Count == 0
                ? "No battles recorded yet. They appear here as soon as Foretop sees one."
                : $"{rows.Count} battles recorded since {rows.Min(r => r.When):d}";

            FrequentPlayers.Clear();
            var frequent = rows.SelectMany(r => r.Arena.Vehicles.Where(v => !v.IsBot && !v.IsSelf).Select(v => (r, v)))
                .GroupBy(x => x.v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(x => x.r.When)).Take(5);
            foreach (var g in frequent)
            {
                var last = g.OrderByDescending(x => x.r.When).First();
                FrequentPlayers.Add(new EncounteredPlayer(last.v.Name, g.Count(), g.Count(x => x.v.IsFriendly), g.Count(x => !x.v.IsFriendly),
                    last.r.When, ""));
            }
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Dashboard load failed");
        }
    }

    private void UpdateStatus()
    {
        var s = _host.Watcher.Status;
        (ClientTitle, ClientLevel) = s.State switch
        {
            WatcherState.InBattle => ("In battle", "Ok"),
            WatcherState.WaitingForBattle => ("Client running", "Ok"),
            WatcherState.GameNotRunning => ("Client not running", "Unknown"),
            WatcherState.GameNotFound => ("Game folder not found", "Problem"),
            _ => ("Starting", "Unknown"),
        };
        ClientDetail = s.GameRoot is null ? s.Message : $"{s.Message} {s.GameRoot}";
        if (!_host.Settings.Current.AutoDetectBattles) (ClientTitle, ClientLevel, ClientDetail) = ("Detection off", "Warning", "Turn it on in Settings.");

        var api = _host.Api;
        (ApiTitle, ApiLevel) = !api.HasApplicationId
            ? ("No application ID", "Problem")
            : api.Health switch
            {
                ApiHealth.Ok => ("Connected", "Ok"),
                ApiHealth.Offline => ("Offline — showing cached data", "Warning"),
                ApiHealth.Rejected => ("Application ID rejected", "Problem"),
                _ => ("Not checked yet", "Unknown"),
            };
        ApiDetail = $"{Regions.Label(_host.Region)} · {api.RequestCount} requests this session · {_host.Cache.Count():N0} cached responses";
    }

    [RelayCommand]
    private void OpenBattle(HistoryRow? row)
    {
        row ??= LastBattle;
        if (row is not null) BattleRequested?.Invoke(row.Record);
    }

    [RelayCommand]
    private void OpenOwnBattle(OwnBattleRow? row)
    {
        if (row?.History is { } h) BattleRequested?.Invoke(h.Record);
    }

    [RelayCommand]
    private void OpenPlayer(EncounteredPlayer? p)
    {
        if (p is not null) ProfileRequested?.Invoke(p.Name);
    }

    [RelayCommand]
    private void Go(Page page) => PageRequested?.Invoke(page);
}
