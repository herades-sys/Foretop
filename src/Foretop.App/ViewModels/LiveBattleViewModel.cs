using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Foretop.App.ViewModels;

/// <summary>One line of the team comparison. FriendlyAhead is null when higher is not simply better.</summary>
public sealed record ComparisonRow(string Label, string Origin, double? Friendly, double? Enemy, string FriendlyText, string EnemyText,
    string DiffText, bool? FriendlyAhead);

public sealed record SortOption(string Label, string? Property, bool Descending)
{
    public override string ToString() => Label;
}

public sealed partial class LiveBattleViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, PlayerRowViewModel> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _loadCts;
    private string? _sessionKey;
    private ArenaInfo? _arena;
    private bool _isLive;
    private bool _dirty;

    public LiveBattleViewModel(AppHost host)
    {
        _host = host;
        _ui = Dispatcher.CurrentDispatcher;

        FriendlyView = MakeView(FriendlyRows);
        EnemyView = MakeView(EnemyRows);
        _selectedSort = SortOptions[0];
        var st = host.Settings.Current;
        _rememberStatsFilter = st.RememberStatsFilter;
        _selectedStatsFilter = StatsFilterOptions.FirstOrDefault(o => st.RememberStatsFilter && o.MinTier == st.StatsMinTier) ?? StatsFilterOptions[0];
        _statsViewMode = Enum.TryParse<StatsView>(st.StatsView, out var sv) ? sv : StatsView.Filtered;
        ApplySort();

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => FlushUpdates(), _ui);

        host.Stats.PlayerUpdated += OnPlayerUpdated;
        host.Watcher.BattleStarted += info => _ui.BeginInvoke(() => ShowBattle(info, live: true));
        host.Watcher.BattleEnded += () => _ui.BeginInvoke(() => { if (_isLive) IsInBattle = false; });
        host.Ships.Updated += () => _ui.BeginInvoke(RefreshShipInfo);
        host.Settings.Changed += _ => _ui.BeginInvoke(() => { UpdateNotice(); OnPropertyChanged(nameof(CompactMode)); FriendlyView.Refresh(); EnemyView.Refresh(); });
        UpdateNotice();
    }

    /// <summary>Raised when a new battle is shown, so the shell can switch to this page.</summary>
    public event Action<bool>? BattleShown;

    /// <summary>Raised after a battle was written to the history.</summary>
    public event Action? HistoryChanged;

    /// <summary>Raised when the user wants a player's in-app profile.</summary>
    public event Action<string>? ProfileRequested;

    public ObservableCollection<ComparisonRow> ComparisonRows { get; } = [];
    public ObservableCollection<ComparisonRow> CompositionRows { get; } = [];

    [ObservableProperty] private bool _showComparison;

    public ObservableCollection<PlayerRowViewModel> FriendlyRows { get; } = [];
    public ObservableCollection<PlayerRowViewModel> EnemyRows { get; } = [];
    public ListCollectionView FriendlyView { get; }
    public ListCollectionView EnemyView { get; }

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Class & tier", null, false),
        new("Win rate", nameof(PlayerRowViewModel.WinRate), true),
        new("Personal Rating", nameof(PlayerRowViewModel.Pr), true),
        new("Avg damage", nameof(PlayerRowViewModel.AvgDamage), true),
        new("Battles", nameof(PlayerRowViewModel.Battles), true),
        new("Ship win rate", nameof(PlayerRowViewModel.ShipWinRate), true),
        new("Recent win rate", nameof(PlayerRowViewModel.RecentWinRate), true),
    ];

    public IReadOnlyList<string> TierOptions { get; } = ["All tiers", "XI", "X", "IX", "VIII", "VII", "VI", "V", "IV", "III", "II", "I"];

    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private string _selectedTier = "All tiers";
    [ObservableProperty] private bool _showDD = true;
    [ObservableProperty] private bool _showCA = true;
    [ObservableProperty] private bool _showBB = true;
    [ObservableProperty] private bool _showCV = true;
    [ObservableProperty] private bool _showSS = true;

    [ObservableProperty] private bool _hasBattle;
    [ObservableProperty] private bool _isInBattle;
    [ObservableProperty] private string _title = "Waiting for a battle";
    [ObservableProperty] private string _subtitle = "Battles are detected automatically while World of Warships runs. You can also open a replay.";
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private TeamSummary? _friendlySummary;
    [ObservableProperty] private TeamSummary? _enemySummary;
    [ObservableProperty] private double? _friendlyShare;
    /// <summary>Any player with 7-day results (from earlier local snapshots); the column is hidden otherwise.</summary>
    [ObservableProperty] private bool _hasRecentData;
    [ObservableProperty] private string _comparisonText = "";

    public bool CompactMode => _host.Settings.Current.CompactMode;

    // ------------------------------------------------------------------ statistics filter

    /// <summary>All tiers, Tier V+ … X+, Tier XI only.</summary>
    public IReadOnlyList<StatsFilterOption> StatsFilterOptions { get; } =
        [new(0, TierFilter.Label(0)), .. TierFilter.Levels.Select(t => new StatsFilterOption(t, TierFilter.Label(t)))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsMinTier), nameof(IsStatsFilterActive), nameof(TableBadge), nameof(TableBadgeTip), nameof(ShowBothLines))]
    private StatsFilterOption _selectedStatsFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TableBadge), nameof(TableBadgeTip), nameof(ShowBothLines))]
    private StatsView _statsViewMode;

    public bool ShowBothLines => IsStatsFilterActive && StatsViewMode == StatsView.Both;

    [ObservableProperty] private bool _rememberStatsFilter;

    public int StatsMinTier => SelectedStatsFilter.MinTier;
    public bool IsStatsFilterActive => StatsMinTier > 0;

    /// <summary>What the stat columns show, on each team table: "ALL TIERS", "TIER VI+", "ALL TIERS / TIER VI+".</summary>
    public string TableBadge => !IsStatsFilterActive || StatsViewMode == StatsView.Overall ? TierFilter.Badge(0)
        : StatsViewMode == StatsView.Filtered ? TierFilter.Badge(StatsMinTier)
        : TierFilter.Badge(0) + " / " + TierFilter.Badge(StatsMinTier);

    public string TableBadgeTip => !IsStatsFilterActive
        ? "Battles, win rate, damage, XP and PR over all of the player's Random battles (official account totals)."
        : StatsViewMode switch
        {
            StatsView.Overall => $"Overall statistics shown; the {TierFilter.Label(StatsMinTier)} filter is set but switched to Overall. Hover a player for both.",
            StatsView.Filtered => $"Battles, win rate, damage, XP and PR over the player's {TierFilter.Label(StatsMinTier)} ships only: the official per-ship totals added up and recalculated by Foretop. Team averages and the strength estimate use them too. 7-day values are not available per tier.",
            _ => $"First line: overall. Second line: {TierFilter.Label(StatsMinTier)} only (calculated by Foretop from the official per-ship totals).",
        };

    partial void OnSelectedStatsFilterChanged(StatsFilterOption value) => ApplyStatsFilter();
    partial void OnStatsViewModeChanged(StatsView value) => ApplyStatsFilter();
    partial void OnRememberStatsFilterChanged(bool value) => SaveStatsFilter();

    private void ApplyStatsFilter()
    {
        foreach (var r in FriendlyRows.Concat(EnemyRows)) r.SetStatsFilter(StatsMinTier, StatsViewMode);
        UpdateTeamSummaries();
        ApplySort();
        SaveStatsFilter();
    }

    /// <summary>False in development runs (--stats-filter), which must not change the saved choice.</summary>
    public bool PersistStatsFilter { get; set; } = true;

    private void SaveStatsFilter()
    {
        if (!PersistStatsFilter) return;
        var cur = _host.Settings.Current;
        var tier = RememberStatsFilter ? StatsMinTier : 0;
        var view = StatsViewMode.ToString();
        if (cur.RememberStatsFilter == RememberStatsFilter && cur.StatsMinTier == tier && cur.StatsView == view) return;
        var s = _host.Settings.Clone();
        s.RememberStatsFilter = RememberStatsFilter;
        s.StatsMinTier = tier;
        s.StatsView = view;
        try { _host.Settings.Save(s); } catch (System.IO.IOException) { } // the choice still applies for this run
    }

    partial void OnSelectedSortChanged(SortOption value) => ApplySort();
    partial void OnSelectedTierChanged(string value) => RefreshFilter();
    partial void OnShowDDChanged(bool value) => RefreshFilter();
    partial void OnShowCAChanged(bool value) => RefreshFilter();
    partial void OnShowBBChanged(bool value) => RefreshFilter();
    partial void OnShowCVChanged(bool value) => RefreshFilter();
    partial void OnShowSSChanged(bool value) => RefreshFilter();

    // ------------------------------------------------------------------ battle lifecycle

    public void ShowBattle(ArenaInfo info, bool live) => ShowBattle(info, live, stored: null);

    /// <summary>Re-opens a recorded battle with the statistics saved at the time (no API calls).</summary>
    public void ShowStored(Core.Data.BattleRecord record)
    {
        if (!ArenaInfoParser.TryParse(record.ArenaJson, out var info, out _)) return;
        ShowBattle(info!, live: false, stored: record);
    }

    private void ShowBattle(ArenaInfo info, bool live, Core.Data.BattleRecord? stored)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _arena = info;
        _isLive = live;
        _stored = stored;
        _sessionKey = info.BattleKey + (live ? "" : stored is null ? ":file" : ":history");
        var region = stored?.Region ?? _host.Region;
        var saved = BattleSummaryCodec.Deserialize(stored?.SummaryJson)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _rowsByName.Clear();
        FriendlyRows.Clear();
        EnemyRows.Clear();

        var showBots = _host.Settings.Current.ShowBots;
        foreach (var v in info.Vehicles)
        {
            if (v.IsBot && !showBots) continue;
            var summary = saved.TryGetValue(v.Name, out var old)
                ? old
                : new PlayerSummary { Name = v.Name, Relation = v.Relation, ShipId = v.ShipId, IsBot = v.IsBot };
            var row = new PlayerRowViewModel(summary, _host.Ships.Get(v.ShipId), region, _host.ShipData.FindById(v.ShipId));
            row.SetStatsFilter(StatsMinTier, StatsViewMode);
            _rowsByName[v.Name] = row;
            (v.IsFriendly ? FriendlyRows : EnemyRows).Add(row);
        }

        ApplySort();
        HasBattle = true;
        IsInBattle = live;
        Title = $"{BattleTypes.Label(info.BattleType, info.MatchGroup)} · {info.MapName}";
        Subtitle = string.Join("  ·  ", new[]
        {
            live ? "Live" : stored is null ? "From file" : $"From history · stats as of {stored.DetectedAt.LocalDateTime:g}",
            info.StartedAt?.ToString("g"),
            info.PlayersPerTeam > 0 ? $"{info.PlayersPerTeam} vs {info.PlayersPerTeam}" : null,
            Regions.Label(region),
            info.ClientVersion is null ? null : "client " + info.ClientVersion.Replace(", ", "."),
        }.Where(s => !string.IsNullOrEmpty(s)));

        UpdateTeamSummaries();
        UpdateNotice();
        BattleShown?.Invoke(live);

        if (stored is null)
        {
            _ = LoadAsync(info, region, live, forceRefresh: false, ct);
            if (live && !string.IsNullOrEmpty(info.PlayerName) && info.PlayerName != _host.Settings.Current.PlayerName)
            {
                var s = _host.Settings.Clone();
                s.PlayerName = info.PlayerName;
                try { _host.Settings.Save(s); } catch (System.IO.IOException) { }
            }
        }
        else
        {
            ProgressText = "Recorded battle with the statistics from that time. Refresh loads current statistics.";
            if (info.Vehicles.Any(v => _host.Ships.Get(v.ShipId) is null)) _ = _host.Ships.EnsureLoadedAsync(region, ct);
        }
    }

    private Core.Data.BattleRecord? _stored;

    private async Task LoadAsync(ArenaInfo info, Region region, bool live, bool forceRefresh, CancellationToken ct)
    {
        var key = _sessionKey!;
        try
        {
            if (info.Vehicles.Any(v => _host.Ships.Get(v.ShipId) is null))
                _ = _host.Ships.EnsureLoadedAsync(region, ct);

            if (!_host.Api.HasApplicationId) return;

            ProgressText = "Loading statistics…";
            var sw = Stopwatch.StartNew();
            var requests = info.Vehicles.Select(v => new PlayerRequest(v.Name, v.Relation, v.ShipId, v.IsBot)).ToList();
            var result = await _host.Stats.LoadAsync(key, region, requests, RequestPriority.High, ct, forceRefresh);
            if (ct.IsCancellationRequested) return;

            ProgressText = $"Loaded in {sw.Elapsed.TotalSeconds:0.0} s";
            if (_stored is null) SaveHistory(info, region, result, live ? "live" : "file");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Loading battle {Key} failed", key);
            ProgressText = "Loading failed: " + ex.Message;
        }
    }

    private void OnPlayerUpdated(string sessionKey, PlayerSummary summary)
    {
        _ui.BeginInvoke(() =>
        {
            if (sessionKey != _sessionKey || !_rowsByName.TryGetValue(summary.Name, out var row)) return;
            row.Update(summary);
            _dirty = true;
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        });
    }

    private void FlushUpdates()
    {
        _refreshTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        FriendlyView.Refresh();
        EnemyView.Refresh();
        UpdateTeamSummaries();
        var loading = _rowsByName.Values.Count(r => r.IsLoading);
        if (loading > 0) ProgressText = $"Loading statistics… {_rowsByName.Count - loading}/{_rowsByName.Count}";
    }

    private void RefreshShipInfo()
    {
        foreach (var row in _rowsByName.Values)
            if (row.Ship is null) row.SetShip(_host.Ships.Get(row.Summary.ShipId));
        FriendlyView.Refresh();
        EnemyView.Refresh();
    }

    private void UpdateTeamSummaries()
    {
        // Team averages and the strength estimate follow the statistics the columns show.
        var project = StatsMinTier > 0 && StatsViewMode != StatsView.Overall;
        FriendlySummary = TeamSummary.From(FriendlyRows.Select(r => project ? TierFilter.Project(r.Summary, StatsMinTier) : r.Summary).ToList());
        EnemySummary = TeamSummary.From(EnemyRows.Select(r => project ? TierFilter.Project(r.Summary, StatsMinTier) : r.Summary).ToList());
        HasRecentData = FriendlyRows.Concat(EnemyRows).Any(r => r.Summary.Recent7 is { Totals.Battles: > 0 });
        FriendlyShare = TeamComparison.FriendlyShare(FriendlySummary, EnemySummary);
        ComparisonText = FriendlyShare is double s
            ? $"Estimated strength (average PR): {s:P0} vs {1 - s:P0} — an indication from past stats, not a prediction"
            : "Strength estimate needs PR for at least 3 players per team";
        BuildComparison(FriendlySummary, EnemySummary);
    }

    private void BuildComparison(TeamSummary f, TeamSummary e)
    {
        ComparisonRows.Clear();
        void Add(string label, string origin, double? a, double? b, Func<double, string> fmt, bool higherBetter = true,
            Func<double, string>? diffFmt = null)
        {
            var diff = "";
            bool? ahead = null;
            if (a is double x && b is double y)
            {
                var d = x - y;
                diff = (d > 0 ? "+" : d < 0 ? "−" : "±") + (diffFmt ?? fmt)(Math.Abs(d));
                if (Math.Abs(d) > 1e-9 && higherBetter) ahead = d > 0;
            }
            ComparisonRows.Add(new ComparisonRow(label, origin, a, b, a is double av ? fmt(av) : "—", b is double bv ? fmt(bv) : "—",
                diff, ahead));
        }

        static string Pct(double v) => v.ToString("0.0") + "%";
        static string Pts(double v) => v.ToString("0.0") + " pts";
        static string N0(double v) => v.ToString("N0");
        Add("Average win rate", "derived", f.AvgWinRate, e.AvgWinRate, Pct, diffFmt: Pts);
        Add("Average Personal Rating", "third-party", f.AvgPr, e.AvgPr, N0);
        Add("Average damage", "derived", f.AvgDamage, e.AvgDamage, N0);
        Add("Damage vs. expected", "third-party", f.AvgDamageRatio * 100, e.AvgDamageRatio * 100, v => v.ToString("0") + "%",
            diffFmt: v => v.ToString("0") + " pts");
        Add("Average experience", "derived", f.AvgXp, e.AvgXp, N0);
        Add("Average frags", "derived", f.AvgFrags, e.AvgFrags, v => v.ToString("0.00"));
        Add("Average survival", "derived", f.AvgSurvival, e.AvgSurvival, Pct, diffFmt: Pts);
        Add("Win rate in current ship", "derived", f.AvgShipWinRate, e.AvgShipWinRate, Pct, diffFmt: Pts);
        Add("Average battles per player", "official", f.AvgBattles, e.AvgBattles, N0);
        Add("Total battles", "official", f.TotalBattles, e.TotalBattles, N0);
        Add("Average account age", "official", f.AvgAccountAgeYears, e.AvgAccountAgeYears, v => v.ToString("0.0") + " y");
        Add("Players with public stats", "official", f.WithStats, e.WithStats, N0, higherBetter: false);
        Add("Hidden profiles", "official", f.Hidden, e.Hidden, N0, higherBetter: false);

        CompositionRows.Clear();
        foreach (var (cls, label) in new[]
                 {
                     (ShipClass.AirCarrier, "Aircraft carriers"), (ShipClass.Battleship, "Battleships"),
                     (ShipClass.Cruiser, "Cruisers"), (ShipClass.Destroyer, "Destroyers"), (ShipClass.Submarine, "Submarines"),
                 })
        {
            double a = FriendlyRows.Count(r => r.ShipClass == cls), b = EnemyRows.Count(r => r.ShipClass == cls);
            if (a == 0 && b == 0) continue;
            CompositionRows.Add(new ComparisonRow(label, "", a, b, a.ToString("0"), b.ToString("0"),
                a == b ? "" : (a > b ? "+" : "−") + Math.Abs(a - b), null));
        }

        static double? AvgTier(IEnumerable<PlayerRowViewModel> rows)
        {
            var t = rows.Where(r => r.Tier > 0).Select(r => (double)r.Tier).ToList();
            return t.Count > 0 ? t.Average() : null;
        }

        var ft = AvgTier(FriendlyRows);
        var et = AvgTier(EnemyRows);
        CompositionRows.Add(new ComparisonRow("Average tier", "", ft, et, ft?.ToString("0.0") ?? "—", et?.ToString("0.0") ?? "—",
            ft is double tx && et is double ty && Math.Abs(tx - ty) > 0.01 ? (tx > ty ? "+" : "−") + Math.Abs(tx - ty).ToString("0.0") : "",
            null));
    }

    private void UpdateNotice()
    {
        Notice = !_host.Api.HasApplicationId
            ? "No Wargaming application ID set — statistics cannot load. Add one in Settings (free at developers.wargaming.net)."
            : _host.DatabaseArchivedTo is { } dir
                ? $"The local database could not be read, so it was moved to {dir} and a new one started. Cached statistics and battle history start fresh."
                : null;
    }

    private void SaveHistory(ArenaInfo info, Region region, IReadOnlyList<PlayerSummary> players, string source)
    {
        try
        {
            _host.History.Upsert(info.BattleKey, region, info.StartedAt, info.MatchGroup, info.MapName, info.PlayerName,
                info.RawJson, BattleSummaryCodec.Serialize(players), source);
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Saving battle history failed");
        }
    }

    // ------------------------------------------------------------------ sorting & filtering

    private ListCollectionView MakeView(ObservableCollection<PlayerRowViewModel> rows)
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
        view.Filter = o => o is PlayerRowViewModel r && PassesFilter(r);
        return view;
    }

    private bool PassesFilter(PlayerRowViewModel r)
    {
        if (r.Ship is null) return true;
        var classOk = r.ShipClass switch
        {
            ShipClass.Destroyer => ShowDD,
            ShipClass.Cruiser => ShowCA,
            ShipClass.Battleship => ShowBB,
            ShipClass.AirCarrier => ShowCV,
            ShipClass.Submarine => ShowSS,
            _ => true,
        };
        return classOk && (SelectedTier == "All tiers" || r.TierText == SelectedTier || (SelectedTier == "XI" && r.Tier == 11));
    }

    private void RefreshFilter()
    {
        FriendlyView.Refresh();
        EnemyView.Refresh();
    }

    /// <summary>
    /// Re-applies the chosen sort. WPF's DataGrid clears a view's sort descriptions when it attaches to it,
    /// so the table calls this once it has loaded.
    /// </summary>
    public void ReapplySort() => ApplySort();

    private void ApplySort()
    {
        foreach (var view in new[] { FriendlyView, EnemyView })
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (SelectedSort.Property is null)
                {
                    view.SortDescriptions.Add(new SortDescription(nameof(PlayerRowViewModel.ClassOrder), ListSortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription(nameof(PlayerRowViewModel.Tier), ListSortDirection.Descending));
                    view.SortDescriptions.Add(new SortDescription(nameof(PlayerRowViewModel.ShipName), ListSortDirection.Ascending));
                }
                else
                {
                    view.SortDescriptions.Add(new SortDescription(SelectedSort.Property,
                        SelectedSort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
                }
            }
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a replay or tempArenaInfo.json",
            Filter = "Replays and arena files|*.wowsreplay;*.json|All files|*.*",
        };
        var root = _host.Settings.Current.GameRoot ?? GameLocator.FindGameRoot();
        if (root is not null && Directory.Exists(Path.Combine(root, "replays"))) dlg.InitialDirectory = Path.Combine(root, "replays");
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = dlg.FileName.EndsWith(".wowsreplay", StringComparison.OrdinalIgnoreCase)
                ? ReplayHeaderReader.ReadArenaJson(dlg.FileName)
                : File.ReadAllText(dlg.FileName);
            if (!ArenaInfoParser.TryParse(json, out var info, out var error))
            {
                MessageBox.Show("This file has no usable roster: " + error, "Foretop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ShowBattle(info!, live: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not read the file: " + ex.Message, "Foretop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_arena is null) return;
        if (_stored is not null)
        {
            // Keep the recorded snapshot as it is; show the same roster again with current statistics.
            ShowBattle(_arena, live: false, stored: null);
            return;
        }
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        _ = LoadAsync(_arena, _host.Region, _isLive, forceRefresh: true, _loadCts.Token);
    }

    [RelayCommand]
    private void OpenProfile(PlayerRowViewModel? row)
    {
        if (row is { IsBot: false }) ProfileRequested?.Invoke(row.Name);
    }

    [RelayCommand]
    private void OpenOfficialProfile(PlayerRowViewModel? row)
    {
        if (row?.AccountId is long id) OpenUrl(Regions.ProfileUrl(row.Region, id, row.Name));
    }

    [RelayCommand]
    private void OpenWowsNumbers(PlayerRowViewModel? row)
    {
        if (row?.AccountId is long id) OpenUrl(Regions.WowsNumbersUrl(row.Region, id, row.Name));
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
