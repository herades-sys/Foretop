using System.Collections.ObjectModel;
using System.Windows.Media;
using Foretop.App.Charts;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

public sealed record EncounteredPlayer(string Name, int Times, int With, int Against, DateTime LastSeen, string LastStats);

/// <summary>Aggregates over the recorded battles. Battle results are not in tempArenaInfo.json, so there are no own win/loss figures.</summary>
public sealed partial class StatisticsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _dirty = true;

    public StatisticsViewModel(AppHost host) => _host = host;

    public event Action<string>? ProfileRequested;

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<EncounteredPlayer> TopPlayers { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private IReadOnlyList<ChartBar> _byType = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _byMap = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _ownShips = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _enemyClasses = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _byDay = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> _strengthTrend = [];

    public void MarkDirty() => _dirty = true;

    public async Task EnsureLoadedAsync()
    {
        if (!_dirty || IsLoading) return;
        _dirty = false;
        IsLoading = true;
        try
        {
            var rows = await Task.Run(() => HistoryViewModel.LoadRows(_host));
            Build(rows);
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Building statistics failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build(List<HistoryRow> rows)
    {
        HasData = rows.Count > 0;
        Cards.Clear();
        TopPlayers.Clear();
        if (rows.Count == 0) return;

        var accent = RatingBrushConverter.Res("Accent");
        static double? Avg(IEnumerable<double?> v)
        {
            var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return l.Count > 0 ? l.Average() : null;
        }

        var fWr = Avg(rows.Select(r => r.FriendlyWr));
        var eWr = Avg(rows.Select(r => r.EnemyWr));
        var fPr = Avg(rows.Select(r => r.FriendlyPr));
        var ePr = Avg(rows.Select(r => r.EnemyPr));
        var first = rows.Min(r => r.When);
        Cards.Add(new StatCard("Battles recorded", rows.Count.ToString("N0"), $"since {first:d}", "Foretop"));
        Cards.Add(new StatCard("Your teams: avg win rate", fWr is double a ? $"{a:0.0}%" : "—",
            eWr is double b ? $"enemy teams {b:0.0}%" : null, "derived", RatingBrushConverter.For(fWr, "wr")));
        Cards.Add(new StatCard("Your teams: avg PR", fPr?.ToString("0") ?? "—", ePr is double c ? $"enemy teams {c:0}" : null, "third-party",
            RatingBrushConverter.For(fPr, "pr")));
        var favoured = rows.Count(r => r.Share > 0.5);
        var rated = rows.Count(r => r.Share is not null);
        Cards.Add(new StatCard("Stronger team on paper", rated > 0 ? $"{100.0 * favoured / rated:0}%" : "—",
            rated > 0 ? $"of {rated} battles with a PR estimate" : null, "third-party"));

        ByType = rows.GroupBy(r => r.Type).OrderByDescending(g => g.Count())
            .Select(g => new ChartBar(g.Key, g.Count(), g.Count().ToString(), accent)).ToList();
        ByMap = rows.GroupBy(r => r.Map).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new ChartBar(g.Key, g.Count(), g.Count().ToString(), accent)).ToList();
        OwnShips = rows.Where(r => r.Arena.Self is not null).GroupBy(r => r.Arena.Self!.ShipId).OrderByDescending(g => g.Count()).Take(10)
            .Select(g =>
            {
                var ship = _host.Ships.Get(g.Key);
                return new ChartBar(ship is null ? $"Ship {g.Key}" : $"{ship.TierRoman} {ship.Name}", g.Count(), g.Count().ToString(),
                    ship is null ? accent : RatingBrushConverter.ForClass(ship.Class));
            }).ToList();
        EnemyClasses = rows.SelectMany(r => r.Arena.Enemy).Select(v => _host.Ships.Get(v.ShipId)?.Class ?? ShipClass.Unknown)
            .Where(c => c != ShipClass.Unknown).GroupBy(c => c).OrderByDescending(g => g.Count())
            .Select(g => new ChartBar(ProfileViewModel.ClassName(g.Key), g.Count(), $"{g.Count()} ({g.Count() / (double)rows.Count:0.0} per battle)",
                RatingBrushConverter.ForClass(g.Key))).ToList();
        ByDay = rows.GroupBy(r => r.When.Date).OrderByDescending(g => g.Key).Take(14).OrderBy(g => g.Key)
            .Select(g => new ChartBar(g.Key.ToString("ddd d MMM"), g.Count(), g.Count().ToString(), accent)).ToList();

        var ordered = rows.OrderBy(r => r.When).ToList();
        var fSeries = ordered.Select((r, i) => (r, i)).Where(x => x.r.FriendlyPr is not null)
            .Select(x => new ChartPoint(x.i, x.r.FriendlyPr!.Value, x.r.When.ToString("d"))).ToList();
        var eSeries = ordered.Select((r, i) => (r, i)).Where(x => x.r.EnemyPr is not null)
            .Select(x => new ChartPoint(x.i, x.r.EnemyPr!.Value, x.r.When.ToString("d"))).ToList();
        StrengthTrend =
        [
            new ChartSeries("Friendly", RatingBrushConverter.Res("Friendly"), fSeries),
            new ChartSeries("Enemy", RatingBrushConverter.Res("Enemy"), eSeries),
        ];

        var encounters = rows.SelectMany(r => r.Arena.Vehicles.Where(v => !v.IsBot && !v.IsSelf).Select(v => (r, v)))
            .GroupBy(x => x.v.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(x => x.r.When)).Take(20);
        foreach (var g in encounters)
        {
            var last = g.OrderByDescending(x => x.r.When).First();
            var then = last.r.Players.FirstOrDefault(p => string.Equals(p.Name, g.Key, StringComparison.OrdinalIgnoreCase));
            var stats = then?.Overall is { } o ? $"{o.WinRate:0.0}% WR · PR {then.Pr?.ToString("0") ?? "—"}" : "—";
            TopPlayers.Add(new EncounteredPlayer(last.v.Name, g.Count(), g.Count(x => x.v.IsFriendly), g.Count(x => !x.v.IsFriendly),
                last.r.When, stats));
        }
    }

    [RelayCommand]
    private void OpenPlayer(EncounteredPlayer? p)
    {
        if (p is not null) ProfileRequested?.Invoke(p.Name);
    }

    [RelayCommand]
    private async Task Reload()
    {
        _dirty = true;
        await EnsureLoadedAsync();
    }
}
