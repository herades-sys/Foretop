using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.App.Charts;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Personal;
using Foretop.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

public sealed record PeriodOption(string Label, int? Last, bool Custom = false)
{
    public override string ToString() => Label;
}

public sealed record FilterOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>A labelled value with where it comes from (API, replay, calculated) and an optional note.</summary>
public sealed record PersonalFact(string Label, string Value, string Origin, Brush? Accent = null, string? Tip = null);

public sealed record PersonalRow(PersonalBattle Battle, string WhenText, string Map, string Ship, string TierText, string Class, string Result,
    string ResultLevel, long Damage, int Kills, int Deaths, string BaseXpText, long Xp, string RatingText, string LifeText, string TypeText)
{
    public DateTimeOffset When => Battle.When;
    public int Tier => Battle.Tier ?? 0;
    public double Rating => Battle.Rating ?? -1;
    public int BaseXp => Battle.BaseXp ?? -1;
}

public sealed record PeriodCompareRow(string Metric, string A, string B, string Delta, string Level);

public sealed record TierRow(string Band, string Battles, string WinRate, string Damage, string Xp, string Kills, string Survival, string Pr,
    Brush? WrBrush, Brush? PrBrush);

public sealed record ShipRowP(string Ship, string TierText, int Tier, int Battles, string WinRate, string Damage, string Kills, string Survival, string Pr, Brush? PrBrush);

/// <summary>Analytics → My performance: your own battles as recorded by Foretop (API results + replay facts).</summary>
public sealed partial class PersonalAnalyticsViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.CurrentCulture;
    private readonly AppHost _host;
    private readonly DispatcherTimer _reload;
    private List<PersonalBattle> _all = [];
    private List<PersonalBattle> _selected = [];
    private PlayerSummary? _account;
    private bool _loaded;

    public PersonalAnalyticsViewModel(AppHost host)
    {
        _host = host;
        _period = Periods[3];
        _mode = Modes[0];
        _class = Classes[0];
        _tier = Tiers[0];
        _compareA = CompareOptions[1];
        _compareB = CompareOptions[3];
        _metric = Metrics[1];
        _reload = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reload.Tick += async (_, _) => { _reload.Stop(); await ReloadAsync(); };
        host.Results.Changed += ScheduleReload;
        host.Personal.Changed += ScheduleReload;
    }

    /// <summary>Opens a recorded battle (double-click in the history table).</summary>
    public event Action<Core.Data.BattleRecord>? BattleRequested;
    /// <summary>Asks the shell for another page (Ship Statistics).</summary>
    public event Action<Page>? PageRequested;

    private void ScheduleReload() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (!_loaded) return;
        _reload.Stop();
        _reload.Start();
    });

    // ------------------------------------------------------------------ options

    public IReadOnlyList<PeriodOption> Periods { get; } =
    [
        new("Last battle", 1), new("Last 5", 5), new("Last 10", 10), new("Last 20", 20), new("Last 50", 50), new("Last 100", 100),
        new("All", null), new("Date range", null, Custom: true),
    ];

    public IReadOnlyList<FilterOption<string?>> Modes { get; } =
        [new("All battle types", null), new("Random", ResultModes.Random), new("Ranked", ResultModes.Ranked), new("Co-op", ResultModes.Coop)];

    public IReadOnlyList<FilterOption<string?>> Classes { get; } =
    [
        new("All classes", null), new("Destroyers", "Destroyer"), new("Cruisers", "Cruiser"), new("Battleships", "Battleship"),
        new("Aircraft carriers", "AirCarrier"), new("Submarines", "Submarine"),
    ];

    public IReadOnlyList<FilterOption<int>> Tiers { get; } =
        [new("All tiers", 0), .. TierFilter.Levels.Select(t => new FilterOption<int>(TierFilter.Label(t), t))];

    public IReadOnlyList<FilterOption<SeriesMode>> SeriesModes { get; } =
        [new("Per battle", SeriesMode.PerBattle), new("Rolling (10)", SeriesMode.Rolling), new("Overall average", SeriesMode.Cumulative)];

    public IReadOnlyList<string> Metrics { get; } = ["Win rate", "Damage", "Battle rating", "Kills", "Deaths", "Kills/deaths", "Survival", "Base XP"];

    public IReadOnlyList<FilterOption<Func<List<PersonalBattle>, List<PersonalBattle>>>> CompareOptions { get; } =
    [
        new("Last 5", l => l.Take(5).ToList()),
        new("Last 10", l => l.Take(10).ToList()),
        new("Last 20", l => l.Take(20).ToList()),
        new("Last 50", l => l.Take(50).ToList()),
        new("Last 100", l => l.Take(100).ToList()),
        new("All recorded", l => l),
        new("This month", l => l.Where(b => b.When.LocalDateTime.Year == DateTime.Now.Year && b.When.LocalDateTime.Month == DateTime.Now.Month).ToList()),
        new("Last month", l => l.Where(b => b.When.LocalDateTime.Year == DateTime.Now.AddMonths(-1).Year && b.When.LocalDateTime.Month == DateTime.Now.AddMonths(-1).Month).ToList()),
        new("Tier VI+", l => l.Where(b => b.Tier >= 6).ToList()),
        new("Tier VIII+", l => l.Where(b => b.Tier >= 8).ToList()),
        new("Tier X+", l => l.Where(b => b.Tier >= 10).ToList()),
        new("Random only", l => l.Where(b => b.Mode == ResultModes.Random).ToList()),
        new("Ranked only", l => l.Where(b => b.Mode == ResultModes.Ranked).ToList()),
    ];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomPeriod))] private PeriodOption _period;
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private FilterOption<string?> _mode;
    [ObservableProperty] private FilterOption<string?> _class;
    [ObservableProperty] private FilterOption<int> _tier;
    [ObservableProperty] private string _metric;
    [ObservableProperty] private SeriesMode _seriesMode = SeriesMode.Rolling;
    [ObservableProperty] private FilterOption<Func<List<PersonalBattle>, List<PersonalBattle>>> _compareA;
    [ObservableProperty] private FilterOption<Func<List<PersonalBattle>, List<PersonalBattle>>> _compareB;

    // table filters
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _tableShip = "All ships";
    [ObservableProperty] private string _tableResult = "All results";
    public ObservableCollection<string> ShipNames { get; } = ["All ships"];
    public IReadOnlyList<string> ResultOptions { get; } = ["All results", "Victory", "Defeat", "Draw"];

    public bool IsCustomPeriod => Period.Custom;

    /// <summary>Date range as text (the current culture's short date), for the two boxes of "Date range".</summary>
    public string CustomFromText
    {
        get => CustomFrom?.ToString("d", Inv) ?? "";
        set { if (DateTime.TryParse(value, Inv, DateTimeStyles.None, out var d)) CustomFrom = d; }
    }

    public string CustomToText
    {
        get => CustomTo?.ToString("d", Inv) ?? "";
        set { if (DateTime.TryParse(value, Inv, DateTimeStyles.None, out var d)) CustomTo = d; }
    }

    [RelayCommand]
    private void SetPeriod(PeriodOption? p) { if (p is not null) Period = p; }

    [RelayCommand]
    private void SetMetric(string? m) { if (m is not null) Metric = m; }

    partial void OnPeriodChanged(PeriodOption value) => Rebuild();
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustomPeriod) Rebuild(); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustomPeriod) Rebuild(); }
    partial void OnModeChanged(FilterOption<string?> value) => Rebuild();
    partial void OnClassChanged(FilterOption<string?> value) => Rebuild();
    partial void OnTierChanged(FilterOption<int> value) => Rebuild();
    partial void OnMetricChanged(string value) => BuildTrend();
    partial void OnSeriesModeChanged(SeriesMode value) => BuildTrend();
    partial void OnCompareAChanged(FilterOption<Func<List<PersonalBattle>, List<PersonalBattle>>> value) => BuildCompare();
    partial void OnCompareBChanged(FilterOption<Func<List<PersonalBattle>, List<PersonalBattle>>> value) => BuildCompare();
    partial void OnSearchChanged(string value) => BuildTable();
    partial void OnTableShipChanged(string value) => BuildTable();
    partial void OnTableResultChanged(string value) => BuildTable();

    // ------------------------------------------------------------------ state

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasBattles;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _lastTitle = "";
    [ObservableProperty] private string _lastSubtitle = "";
    [ObservableProperty] private string? _lastNotes;
    [ObservableProperty] private bool _hasLast;
    [ObservableProperty] private IReadOnlyList<ChartSeries> _trend = [];
    [ObservableProperty] private string _trendFormat = "0";
    [ObservableProperty] private string _trendNote = "";
    [ObservableProperty] private IReadOnlyList<ChartBar> _resultBars = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _damageBars = [];
    [ObservableProperty] private string _tableText = "";
    [ObservableProperty] private string _accountTierNote = "";
    [ObservableProperty] private string _compareNote = "";

    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<PersonalFact> LastRows { get; } = [];
    public ObservableCollection<PersonalRow> Rows { get; } = [];
    public ObservableCollection<PeriodCompareRow> CompareRows { get; } = [];
    public ObservableCollection<TierRow> RecordedTiers { get; } = [];
    public ObservableCollection<TierRow> AccountTiers { get; } = [];
    public ObservableCollection<PeriodCompareRow> AccountTierCompare { get; } = [];
    public ObservableCollection<ShipRowP> ShipRows { get; } = [];

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var region = _host.Region;
            var name = _host.Settings.Current.PlayerName;
            long? account = string.IsNullOrWhiteSpace(name) ? null
                : _host.Accounts.Get(region, [name]).TryGetValue(name, out var a) ? a.AccountId : null;
            var rows = await Task.Run(() => account is long id ? _host.Results.Store.All(region, id) : _host.Results.Store.Since(DateTimeOffset.MinValue, 10_000));
            rows = await Task.Run(() => BackfillAsync(rows));
            _all = rows.Select(PersonalBattle.From).Where(b => b is not null).Select(b => b!).OrderByDescending(b => b.When).ToList();
            _loaded = true;
            var combined = rows.Count(r => r.Status == ResultStatus.Combined);
            var pending = rows.Count(r => r.Status == ResultStatus.Pending);
            SummaryText = _all.Count == 0 ? "" :
                $"{_all.Count:N0} battle{(_all.Count == 1 ? "" : "s")} with a result recorded since {_all[^1].When.LocalDateTime:d}"
                + (pending > 0 ? $" · {pending} waiting for the API" : "")
                + (combined > 0 ? $" · {combined} result(s) covering several battles at once left out" : "");
            EmptyText = _all.Count == 0
                ? "No results recorded yet. Foretop records the result of each Random, Ranked and Co-op battle you play while it runs "
                  + "(from your account totals before and after the battle, plus the replay). Wargaming offers no battle history, so analytics start with your first recorded battle."
                : "";
            ShipNames.Clear();
            ShipNames.Add("All ships");
            foreach (var s in _all.Select(b => b.ShipName ?? "").Where(s => s.Length > 0).Distinct().OrderBy(s => s)) ShipNames.Add(s);
            Rebuild();
            _ = LoadAccountAsync(name, region);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            EmptyText = "Your results could not be read: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Battles recorded before personal analytics existed: map, battle type and ship (from the recorded roster and the
    /// ship catalogue) and the battle rating (from today's expected values) are added once and then kept.
    /// </summary>
    private async Task<List<BattleResult>> BackfillAsync(List<BattleResult> rows)
    {
        IReadOnlyDictionary<long, ExpectedValues>? expected = null;
        var result = new List<BattleResult>(rows.Count);
        foreach (var r in rows)
        {
            if (r.Status != ResultStatus.Ok || r.Result is not { Battles: 1 } one) { result.Add(r); continue; }
            var x = r.Extras ?? new PersonalExtras();
            var changed = false;
            if (x.Map is null || x.MatchGroup is null)
            {
                if (_host.History.GetByKey(r.BattleKey) is { } rec && ArenaInfoParser.TryParse(rec.ArenaJson, out var info, out _))
                {
                    x = x with { Map = info!.MapName, MatchGroup = info.MatchGroup };
                    changed = true;
                }
            }
            if (x.ShipTier is null && _host.Results.ShipLookup?.Invoke(r.ShipId) is { } s)
            {
                x = x with { ShipName = s.Name, ShipTier = s.Tier, ShipClass = s.Class };
                changed = true;
            }
            if (x.Rating is null && x.RatingExpected is null)
            {
                try { expected ??= await _host.Expected.GetAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { expected = new Dictionary<long, ExpectedValues>(); }
                if (expected.TryGetValue(r.ShipId, out var e))
                {
                    x = x with { Rating = PersonalRating.Compute([(r.ShipId, one)], expected), RatingExpected = [e.AvgDamage, e.AvgFrags, e.WinRate] };
                    changed = true;
                }
            }
            if (changed)
            {
                var updated = r with { Extras = x };
                _host.Results.Store.Upsert(updated);
                result.Add(updated);
            }
            else result.Add(r);
        }
        return result;
    }

    private PersonalQuery Query() => new()
    {
        Last = Period.Custom ? null : Period.Last,
        From = Period.Custom && CustomFrom is DateTime f ? new DateTimeOffset(f.Date) : null,
        To = Period.Custom && CustomTo is DateTime t ? new DateTimeOffset(t.Date.AddDays(1)) : null,
        Mode = Mode.Value,
        ShipClass = Class.Value,
        MinTier = Tier.Value,
    };

    private void Rebuild()
    {
        if (!_loaded) return;
        _selected = Query().Apply(_all);
        HasBattles = _selected.Count > 0;
        BuildLast();
        BuildCards();
        BuildTrend();
        BuildTable();
        BuildCompare();
        BuildRecordedTiers();
        BuildShips();
    }

    // ------------------------------------------------------------------ last battle

    private void BuildLast()
    {
        LastRows.Clear();
        var b = _selected.FirstOrDefault();
        HasLast = b is not null;
        if (b is null) { LastTitle = ""; LastSubtitle = ""; LastNotes = null; return; }
        var x = b.Extras;
        LastTitle = $"{b.ResultText} · {b.Map ?? "Unknown map"}";
        LastSubtitle = string.Join("  ·  ", new[]
        {
            b.When.LocalDateTime.ToString("g", Inv), TypeLabel(b), $"{b.ShipName ?? "Ship " + b.ShipId} ({TierText(b.Tier)}, {ClassLabel(b.ShipClass)})",
        });
        var period = PeriodStats.Of(_selected);
        void Add(string label, string value, string origin, Brush? accent = null, string? tip = null) => LastRows.Add(new PersonalFact(label, value, origin, accent, tip));
        Add("Result", b.ResultText, "API", b.Result == 'W' ? RatingBrushConverter.Res("StatusGreen") : b.Result == 'L' ? RatingBrushConverter.Res("StatusRed") : null);
        Add("Win rate (selected period)", Pct(period.WinRate) + $" over {period.Battles:N0}", "calculated", null,
            "A single battle has no win rate: this is the win rate over the battles selected above.");
        Add("Damage", b.Damage.ToString("N0", Inv), "API");
        Add("Kills", b.Kills.ToString(Inv), "API");
        Add("Deaths", b.Deaths.ToString(Inv), "API");
        Add("Survived", b.Survived ? "Yes" : "No", "API");
        Add("Time survived", b.LifeTime is double lt ? Clock(lt) + (b.Extras.Replay?.Duration is double d ? $" of {Clock(d)}" : "") : "—", "replay",
            tip: b.LifeTime is null ? x.ReplayNote ?? "No replay facts yet" : "From the replay: death time minus the start of the battle timer.");
        Add("XP", b.Xp.ToString("N0", Inv), "API", tip: "As the API counts it: with Premium Account, flags and other bonuses.");
        Add("Base XP", b.BaseXp?.ToString("N0", Inv) ?? "—", "replay", tip: b.BaseXp is null ? x.ReplayNote ?? "No replay facts yet" : "Before Premium Account and bonuses, from the replay's result data.");
        Add("Spotting damage", b.Spotting?.ToString("N0", Inv) ?? "—", "API", tip: x.ShipDelta is null ? x.ShipDeltaNote ?? Before16 : "Difference of your ship's totals before and after the battle.");
        Add("Potential damage", b.Potential?.ToString("N0", Inv) ?? "—", "API", tip: x.ShipDelta is null ? x.ShipDeltaNote ?? Before16 : "Shell and torpedo damage aimed at you (artillery + torpedo potential).");
        Add("Aircraft destroyed", b.Planes.ToString(Inv), "API");
        Add("Main battery hits", x.ShipDelta is { } d1 ? $"{d1.MbHits:N0} / {d1.MbShots:N0}" + (b.MbAccuracy is double ma ? $"  ({ma:0.0} %)" : "") : "—", "API",
            tip: x.ShipDelta is null ? x.ShipDeltaNote ?? Before16 : null);
        Add("Torpedo hits", x.ShipDelta is { } d2 ? $"{d2.TorpHits:N0} / {d2.TorpShots:N0}" + (b.TorpAccuracy is double ta ? $"  ({ta:0.0} %)" : "") : "—", "API",
            tip: x.ShipDelta is null ? x.ShipDeltaNote ?? Before16 : null);
        Add("Team position", b.TeamPosition is int p ? $"{p} of {b.TeamSize} by damage" : "—", "replay",
            tip: b.TeamPosition is null ? x.ReplayNote ?? "No replay facts yet" : "Your place in your team by damage dealt, from the replay's result data.");
        Add("Battle rating", b.Rating is double r ? r.ToString("0", Inv) : "—", "calculated", RatingBrushConverter.For(b.Rating, "pr"),
            "The wows-numbers PR formula applied to this one battle (damage, kills, result against the ship's expected values). Calculated by Foretop, not an official rating.");
        var notes = new List<string>();
        if (x.ReplayNote is { } rn) notes.Add("Replay: " + rn + ".");
        if (x.ShipDelta is null) notes.Add("Ship details: " + (x.ShipDeltaNote ?? Before16) + ".");
        LastNotes = notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private const string Before16 = "not recorded for battles before Foretop 1.6.0";

    // ------------------------------------------------------------------ cards

    private void BuildCards()
    {
        Cards.Clear();
        if (_selected.Count == 0) return;
        var s = PeriodStats.Of(_selected);
        var n = s.Battles;
        string Of(Metric m) => m.Count == n ? $"all {n} battles" : $"{m.Count} of {n} battles";
        Cards.Add(new StatCard("Battles", n.ToString("N0", Inv), $"{s.Wins} victories · {s.Losses} defeats · {s.Draws} draws", "API"));
        Cards.Add(new StatCard("Win rate", Pct(s.WinRate), "victories ÷ battles", "calculated", RatingBrushConverter.For(n >= 10 ? s.WinRate : null, "wr")));
        Cards.Add(new StatCard("PR", s.Pr?.ToString("0", Inv) ?? "—",
            s.Pr is null ? "no expected values stored" : $"over {s.PrBattles} battles, wows-numbers formula", "calculated", RatingBrushConverter.For(s.Pr, "pr")));
        Cards.Add(new StatCard("Average damage", Num(s.AvgDamage), $"{s.Damage:N0} in total", "API"));
        Cards.Add(new StatCard("Kills", s.Kills.ToString("N0", Inv), $"{Dec(s.AvgKills)} per battle", "API"));
        Cards.Add(new StatCard("Deaths", s.Deaths.ToString("N0", Inv), $"{Dec(s.AvgDeaths)} per battle", "API"));
        Cards.Add(new StatCard("Kills/deaths", s.KillDeath?.ToString("0.00", Inv) ?? "—", s.Deaths == 0 ? "no deaths in this period" : "kills ÷ deaths", "calculated"));
        Cards.Add(new StatCard("Survival rate", Pct(s.SurvivalRate), $"{n - s.Deaths} of {n} survived", "calculated"));
        Cards.Add(new StatCard("Average XP", Num(s.AvgXp), "with Premium and bonuses", "API"));
        Cards.Add(new StatCard("Average base XP", Num(s.BaseXp.Value), Of(s.BaseXp), "replay"));
        Cards.Add(new StatCard("Average spotting damage", Num(s.Spotting.Value), Of(s.Spotting), "API"));
        Cards.Add(new StatCard("Average potential damage", Num(s.Potential.Value), Of(s.Potential), "API"));
        Cards.Add(new StatCard("Main battery accuracy", s.MbAccuracy.Value is double mb ? mb.ToString("0.0", Inv) + " %" : "—", "hits ÷ shots, " + Of(s.MbAccuracy), "API"));
        Cards.Add(new StatCard("Torpedo accuracy", s.TorpAccuracy.Value is double tb ? tb.ToString("0.0", Inv) + " %" : "—", "hits ÷ launched, " + Of(s.TorpAccuracy), "API"));
        Cards.Add(new StatCard("Average time survived", s.LifeTime.Value is double lt ? Clock(lt) : "—", Of(s.LifeTime), "replay"));
        Cards.Add(new StatCard("Average aircraft destroyed", Dec(s.Planes.Value), Of(s.Planes), "API"));
        Cards.Add(new StatCard("Average team position", s.TeamPosition.Value is double tp ? tp.ToString("0.0", Inv) : "—", "by damage, " + Of(s.TeamPosition), "replay"));
    }

    // ------------------------------------------------------------------ trends

    private void BuildTrend()
    {
        if (!_loaded) return;
        var ordered = _selected.AsEnumerable().Reverse().ToList();
        var mode = SeriesMode;
        List<(PersonalBattle Battle, double Value)> pts = Metric switch
        {
            "Win rate" => PersonalSeries.BuildRatio(ordered, b => (b.Result == 'W' ? 1 : 0, 1), mode, scale: 100),
            "Damage" => PersonalSeries.Build(ordered, b => b.Damage, mode),
            "Battle rating" => PersonalSeries.Build(ordered, b => b.Rating, mode),
            "Kills" => PersonalSeries.Build(ordered, b => b.Kills, mode),
            "Deaths" => PersonalSeries.Build(ordered, b => b.Deaths, mode),
            "Kills/deaths" => PersonalSeries.BuildRatio(ordered, b => (b.Kills, b.Deaths), mode),
            "Survival" => PersonalSeries.BuildRatio(ordered, b => (b.Survived ? 1 : 0, 1), mode, scale: 100),
            _ => PersonalSeries.Build(ordered, b => b.BaseXp, mode),
        };
        TrendFormat = Metric is "Win rate" or "Survival" ? "0'%'" : Metric is "Kills" or "Deaths" or "Kills/deaths" ? "0.##" : "N0";
        var i = 0;
        var points = pts.Select(p => new ChartPoint(i++, p.Value, p.Battle.When.LocalDateTime.ToString("d", Inv), Tip(p.Battle, p.Value))).ToList();
        Trend = points.Count == 0 ? [] : [new ChartSeries(Metric, RatingBrushConverter.Res("Accent"), points)];
        TrendNote = mode switch
        {
            SeriesMode.PerBattle => $"{Metric} in each battle, oldest left. Hover a point for the battle.",
            SeriesMode.Rolling => $"{Metric} over the last 10 battles at each point (totals ÷ battles).",
            _ => $"{Metric} over all battles up to each point (totals ÷ battles).",
        } + (Metric == "Battle rating" ? " Battle rating is calculated by Foretop with the PR formula." : "")
          + (Metric == "Kills/deaths" ? " Points need at least one death." : "");

        ResultBars =
        [
            new ChartBar("Victories", _selected.Count(b => b.Result == 'W'), _selected.Count(b => b.Result == 'W').ToString(Inv), RatingBrushConverter.Res("StatusGreen")),
            new ChartBar("Defeats", _selected.Count(b => b.Result == 'L'), _selected.Count(b => b.Result == 'L').ToString(Inv), RatingBrushConverter.Res("StatusRed")),
            new ChartBar("Draws", _selected.Count(b => b.Result == 'D'), _selected.Count(b => b.Result == 'D').ToString(Inv), RatingBrushConverter.Res("TextDim")),
        ];
        (string Label, long Min, long Max)[] buckets = [("< 25k", 0, 25_000), ("25–50k", 25_000, 50_000), ("50–75k", 50_000, 75_000),
            ("75–100k", 75_000, 100_000), ("100–150k", 100_000, 150_000), ("≥ 150k", 150_000, long.MaxValue)];
        DamageBars = buckets.Select(k =>
        {
            var c = _selected.Count(b => b.Damage >= k.Min && b.Damage < k.Max);
            return new ChartBar(k.Label, c, c.ToString(Inv), RatingBrushConverter.Res("Accent"));
        }).ToList();
    }

    private string Tip(PersonalBattle b, double value) =>
        $"{b.When.LocalDateTime:g} · {b.Map} · {b.ShipName} ({TierText(b.Tier)})\n{b.ResultText}, {b.Damage:N0} damage, {b.Kills} kills, "
        + $"{(b.Survived ? "survived" : "sunk")}{(b.Rating is double r ? $", rating {r:0}" : "")}\n{Metric}: {value.ToString(TrendFormat.Replace("'%'", "") , Inv)}{(TrendFormat.Contains('%') ? " %" : "")}";

    // ------------------------------------------------------------------ table

    private void BuildTable()
    {
        Rows.Clear();
        var q = _selected.Where(b =>
            (TableShip == "All ships" || b.ShipName == TableShip)
            && (TableResult == "All results" || b.ResultText == TableResult)
            && (Search.Length == 0 || (b.ShipName ?? "").Contains(Search, StringComparison.CurrentCultureIgnoreCase)
                || (b.Map ?? "").Contains(Search, StringComparison.CurrentCultureIgnoreCase)));
        foreach (var b in q)
            Rows.Add(new PersonalRow(b, b.When.LocalDateTime.ToString("g", Inv), b.Map ?? "—", b.ShipName ?? "Ship " + b.ShipId, TierText(b.Tier),
                ClassLabel(b.ShipClass), b.ResultText, b.Result == 'W' ? "good" : b.Result == 'L' ? "bad" : "", b.Damage, b.Kills, b.Deaths,
                b.BaseXp?.ToString("N0", Inv) ?? "—", b.Xp, b.Rating?.ToString("0", Inv) ?? "—", b.LifeTime is double lt ? Clock(lt) : "—", TypeLabel(b)));
        TableText = $"{Rows.Count:N0} of {_selected.Count:N0} battles in the selected period";
    }

    [RelayCommand]
    private void Open(PersonalRow? row)
    {
        if (row is null) return;
        if (_host.History.GetByKey(row.Battle.Key) is { } record) BattleRequested?.Invoke(record);
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"foretop-battles-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV file (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, Csv(Rows.Select(r => r.Battle)), new UTF8Encoding(true));
    }

    /// <summary>One line per battle, invariant number format; empty cells for values that are not available.</summary>
    public static string Csv(IEnumerable<PersonalBattle> battles)
    {
        var ci = CultureInfo.InvariantCulture;
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        string N(double? v, string f = "0.##") => v?.ToString(f, ci) ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("date,battle_type,map,ship,tier,class,result,damage,kills,deaths,survived,xp_with_bonuses,base_xp,spotting_damage,potential_damage,"
                      + "aircraft_destroyed,main_battery_hits,main_battery_shots,torpedo_hits,torpedo_shots,time_survived_s,team_position,team_size,battle_rating_calculated");
        foreach (var b in battles)
        {
            var d = b.Extras.ShipDelta;
            sb.AppendLine(string.Join(",",
                b.When.ToString("yyyy-MM-dd HH:mm", ci), Q(b.Mode), Q(b.Map), Q(b.ShipName), N(b.Tier), Q(b.ShipClass), b.ResultText,
                b.Damage.ToString(ci), b.Kills.ToString(ci), b.Deaths.ToString(ci), b.Survived ? "1" : "0", b.Xp.ToString(ci), N(b.BaseXp),
                N(b.Spotting), N(b.Potential), b.Planes.ToString(ci), N(d?.MbHits), N(d?.MbShots), N(d?.TorpHits), N(d?.TorpShots),
                N(b.LifeTime, "0"), N(b.TeamPosition), N(b.TeamSize), N(b.Rating, "0")));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ compare

    private void BuildCompare()
    {
        CompareRows.Clear();
        if (!_loaded) return;
        var a = CompareA.Value(_all);
        var b = CompareB.Value(_all);
        CompareNote = $"{CompareA.Label}: {a.Count} battles · {CompareB.Label}: {b.Count} battles. Difference = first − second; green is better.";
        if (a.Count == 0 || b.Count == 0) return;
        var sa = PeriodStats.Of(a);
        var sb = PeriodStats.Of(b);
        void Row(string metric, double? x, double? y, string format, bool higherIsBetter = true, string unit = "", bool neutral = false)
        {
            string F(double? v) => v is double d ? d.ToString(format, Inv) + unit : "—";
            var delta = x is double dx && y is double dy ? dx - dy : (double?)null;
            var level = !neutral && delta is double dd && Math.Abs(dd) > 1e-9 ? (dd > 0 == higherIsBetter ? "good" : "bad") : "";
            CompareRows.Add(new PeriodCompareRow(metric, F(x), F(y), delta is double z ? (z > 0 ? "+" : "") + z.ToString(format, Inv) + unit : "—", level));
        }
        Row("Battles", sa.Battles, sb.Battles, "N0", neutral: true);
        Row("Win rate", sa.WinRate, sb.WinRate, "0.0", unit: " %");
        Row("PR (calculated)", sa.Pr, sb.Pr, "0");
        Row("Average damage", sa.AvgDamage, sb.AvgDamage, "N0");
        Row("Kills per battle", sa.AvgKills, sb.AvgKills, "0.00");
        Row("Deaths per battle", sa.AvgDeaths, sb.AvgDeaths, "0.00", higherIsBetter: false);
        Row("Kills/deaths", sa.KillDeath, sb.KillDeath, "0.00");
        Row("Survival rate", sa.SurvivalRate, sb.SurvivalRate, "0.0", unit: " %");
        Row("Average base XP", sa.BaseXp.Value, sb.BaseXp.Value, "N0");
    }

    // ------------------------------------------------------------------ tiers and ships

    private void BuildRecordedTiers()
    {
        RecordedTiers.Clear();
        foreach (var (label, min, max) in TierBands.All)
        {
            var list = _selected.Where(b => b.Tier >= min && b.Tier <= max).ToList();
            if (list.Count == 0) { RecordedTiers.Add(new TierRow(label, "0", "—", "—", "—", "—", "—", "—", null, null)); continue; }
            var s = PeriodStats.Of(list);
            RecordedTiers.Add(new TierRow(label, s.Battles.ToString("N0", Inv), Pct(s.WinRate), Num(s.AvgDamage), Num(s.BaseXp.Value), Dec(s.AvgKills),
                Pct(s.SurvivalRate), s.Pr?.ToString("0", Inv) ?? "—", RatingBrushConverter.For(s.Battles >= 10 ? s.WinRate : null, "wr"), RatingBrushConverter.For(s.Pr, "pr")));
        }
    }

    private async Task LoadAccountAsync(string? name, Region region)
    {
        AccountTiers.Clear();
        AccountTierCompare.Clear();
        if (string.IsNullOrWhiteSpace(name) || !_host.Api.HasApplicationId)
        {
            AccountTierNote = "Needs your account name (Settings → Your account) and a Wargaming application ID.";
            return;
        }
        AccountTierNote = "Loading your per-ship statistics…";
        try
        {
            var result = await _host.Stats.LoadAsync("personal:" + name, region, [new PlayerRequest(name, 1, 0, false)], RequestPriority.Normal, CancellationToken.None);
            _account = result[0];
            if (_account.Ships is not { } ships || _host.Stats.ShipTier is not { } tierOf)
            {
                AccountTierNote = _account.Hidden ? "Your profile is hidden, so the API gives no per-ship statistics." : "Per-ship statistics are not available.";
                return;
            }
            var expected = await _host.Expected.GetAsync(CancellationToken.None);
            foreach (var (label, t, pr) in TierBands.Account(ships, tierOf, expected))
                AccountTiers.Add(new TierRow(label, t.Battles.ToString("N0", Inv), Pct(t.WinRate), Num(t.AvgDamage), Num(t.AvgXp), Dec(t.AvgFrags),
                    Pct(t.SurvivalRate), pr?.ToString("0", Inv) ?? "—", RatingBrushConverter.For(t.Battles >= 50 ? t.WinRate : null, "wr"), RatingBrushConverter.For(pr, "pr")));
            var all = TierFilter.Sum(ships.Where(s => tierOf(s.ShipId) is not null).Select(s => s.Totals));
            var allPr = PersonalRating.Compute(ships.Where(s => tierOf(s.ShipId) is not null).Select(s => (s.ShipId, s.Totals)), expected);
            var levels = TierFilter.ComputeAll(ships, tierOf, expected).ToDictionary(t => t.MinTier);
            void Pair(string label, StatTotals x, double? xPr, StatTotals y, double? yPr)
            {
                AccountTierCompare.Add(new PeriodCompareRow(label + " — battles", x.Battles.ToString("N0", Inv), y.Battles.ToString("N0", Inv), "", ""));
                var dwr = x.WinRate is double a && y.WinRate is double b ? b - a : (double?)null;
                AccountTierCompare.Add(new PeriodCompareRow(label + " — win rate", Pct(x.WinRate), Pct(y.WinRate), Signed(dwr, "0.0", " %"), Level(dwr)));
                var ddm = x.AvgDamage is double c && y.AvgDamage is double d ? d - c : (double?)null;
                AccountTierCompare.Add(new PeriodCompareRow(label + " — average damage", Num(x.AvgDamage), Num(y.AvgDamage), Signed(ddm, "N0"), Level(ddm)));
                var dpr = xPr is double e && yPr is double f ? f - e : (double?)null;
                AccountTierCompare.Add(new PeriodCompareRow(label + " — PR", xPr?.ToString("0", Inv) ?? "—", yPr?.ToString("0", Inv) ?? "—", Signed(dpr, "0"), Level(dpr)));
            }
            Pair("All tiers → VI+", all, allPr, levels[6].Totals, levels[6].Pr);
            Pair("VI+ → VIII+", levels[6].Totals, levels[6].Pr, levels[8].Totals, levels[8].Pr);
            Pair("VIII+ → X+", levels[8].Totals, levels[8].Pr, levels[10].Totals, levels[10].Pr);
            AccountTierNote = $"All {all.Battles:N0} Random battles of your account, from the official per-ship totals (not only those Foretop recorded). "
                              + "XP here is as the API counts it, with bonuses. Differences: second − first.";
        }
        catch (Exception ex) when (ex is WgApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _host.Log.LogInformation("Personal tier analysis: {Error}", ex.Message);
            AccountTierNote = "Your per-ship statistics could not be loaded: " + ex.Message;
        }
    }

    private void BuildShips()
    {
        ShipRows.Clear();
        foreach (var g in _selected.GroupBy(b => b.ShipId).OrderByDescending(g => g.Count()))
        {
            var s = PeriodStats.Of(g.ToList());
            var f = g.First();
            ShipRows.Add(new ShipRowP(f.ShipName ?? "Ship " + f.ShipId, TierText(f.Tier), f.Tier ?? 0, s.Battles, Pct(s.WinRate), Num(s.AvgDamage), Dec(s.AvgKills),
                Pct(s.SurvivalRate), s.Pr?.ToString("0", Inv) ?? "—", RatingBrushConverter.For(s.Pr, "pr")));
        }
    }

    [RelayCommand]
    private void OpenShipStatistics() => PageRequested?.Invoke(Page.Ships);

    // ------------------------------------------------------------------ formatting

    private static string Pct(double? v) => v is double d ? d.ToString("0.0", Inv) + " %" : "—";
    private static string Num(double? v) => v is double d ? d.ToString("N0", Inv) : "—";
    private static string Dec(double? v) => v is double d ? d.ToString("0.00", Inv) : "—";
    private static string Signed(double? v, string f, string unit = "") => v is double d ? (d > 0 ? "+" : "") + d.ToString(f, Inv) + unit : "—";
    private static string Level(double? v) => v is double d && Math.Abs(d) > 1e-9 ? (d > 0 ? "good" : "bad") : "";
    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    private static string TierText(int? t) => t is int x and > 0 ? TierFilter.Roman(x) : "?";

    private static string ClassLabel(string? c) => c switch
    {
        "Destroyer" => "Destroyer", "Cruiser" => "Cruiser", "Battleship" => "Battleship", "AirCarrier" => "Aircraft carrier", "Submarine" => "Submarine",
        null or "" => "?", _ => c,
    };

    private static string TypeLabel(PersonalBattle b) => b.Mode switch
    {
        ResultModes.Random => "Random battle", ResultModes.Ranked => "Ranked battle", ResultModes.Coop => "Co-op battle", _ => b.Mode,
    };
}
