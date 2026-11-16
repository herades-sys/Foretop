using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.App.Charts;
using Foretop.Core.Personal;
using Foretop.Core.Stats;

namespace Foretop.App.ViewModels;

/// <summary>Shared number formats for the analytics panels: "—" for missing values, never 0.</summary>
internal static class Fmt
{
    private static CultureInfo C => CultureInfo.CurrentCulture;
    public static string Pct(double? v) => v is double d ? d.ToString("0.0", C) + " %" : "—";
    public static string Num(double? v) => v is double d ? d.ToString("N0", C) : "—";
    public static string Dec(double? v) => v is double d ? d.ToString("0.00", C) : "—";
    public static string Int(long v) => v.ToString("N0", C);
    /// <summary>PR as everywhere in Foretop: whole number, no thousands separator.</summary>
    public static string Pr(double? v) => v is double d ? d.ToString("0", C) : "—";
    public static string Tier(int? t) => t is int x and > 0 ? ShipRanking.Roman(x) : "?";
    public static string Signed(double? v, string f, string unit = "") => v is double d ? (d > 0 ? "+" : "") + d.ToString(f, C) + unit : "—";

    /// <summary>Relative difference b vs a in percent; null when a is missing or zero.</summary>
    public static string Relative(double? a, double? b) =>
        a is double x && b is double y && Math.Abs(x) > 1e-9 ? Signed(100 * (y - x) / Math.Abs(x), "0.0", " %") : "—";
}

/// <summary>One row of an analytics table: a ship, a tier, a class or a group, with every metric.</summary>
public sealed record AnalyticsRow(string Label, string TierText, int Tier, AggregateStats S, bool Highlight = false, string? Key = null)
{
    public int Battles => S.Battles;
    public int Wins => S.Wins;
    public int Losses => S.Losses;
    public int Draws => S.Draws;
    public double WinRateSort => S.WinRate ?? -1;
    public double AvgDamageSort => S.AvgDamage ?? -1;
    public double PrSort => S.Pr ?? -1;
    public double KillsSort => S.AvgKills ?? -1;
    public double DeathsSort => S.AvgDeaths ?? -1;
    public double KdSort => S.KillDeath ?? -1;
    public double SurvivalSort => S.SurvivalRate ?? -1;
    public double BaseXpSort => S.BaseXp.Value ?? -1;
    public long TotalDamage => S.Damage;
    public string WinRateText => Fmt.Pct(S.WinRate);
    public string AvgDamageText => Fmt.Num(S.AvgDamage);
    public string TotalDamageText => Fmt.Int(S.Damage);
    public string PrText => Fmt.Pr(S.Pr);
    public string PrTip => S.Pr is null ? "No expected values for these ships, so no PR." : $"PR over {S.PrBattles:N0} of {S.Battles:N0} battles (ships with expected values).";
    public string KillsText => Fmt.Dec(S.AvgKills);
    public string DeathsText => Fmt.Dec(S.AvgDeaths);
    public string KdText => Fmt.Dec(S.KillDeath);
    public string SurvivalText => Fmt.Pct(S.SurvivalRate);
    public string BaseXpText => S.BaseXp.Value is double b ? b.ToString("N0", CultureInfo.CurrentCulture) + (S.BaseXp.Count < S.Records ? $" ({S.BaseXp.Count})" : "") : "—";
    public Brush? PrBrush => RatingBrushConverter.For(S.Pr, "pr");
    public Brush? WrBrush => RatingBrushConverter.For(S.Battles >= 10 ? S.WinRate : null, "wr");

    public static AnalyticsRow From(AnalyticsGroups.Group g, bool highlight = false) =>
        new(g.Ship is { } s ? s.Name : g.Label, g.Tier is int t ? Fmt.Tier(t) : "", g.Tier ?? 0, g.Stats, highlight, g.Key);
}

/// <summary>Where a panel gets its data: the records of the current source and the global filter.</summary>
public interface IAnalyticsData
{
    IReadOnlyList<AnalyticsRecord> Records { get; }
    AnalyticsFilter Global { get; }
    AnalyticsSource Source { get; }
}

/// <summary>Base for panels: a chart filter on top of the global filter, refreshed on either change.</summary>
public abstract partial class AnalyticsPanel : ObservableObject
{
    protected AnalyticsPanel(IAnalyticsData data, bool localTiers = false)
    {
        Data = data;
        Local = new ChartFilter(Refresh, localTiers);
    }

    protected IAnalyticsData Data { get; }
    public ChartFilter Local { get; }

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _emptyText;

    /// <summary>Global ∩ chart filter applied to the records.</summary>
    protected List<AnalyticsRecord> Selected(AnalyticsFilter? globalOverride = null) =>
        (globalOverride ?? Data.Global).Intersect(Local.Filter).Apply(Data.Records);

    public abstract void Refresh();
}

/// <summary>Best or worst performing ships: ranked by PR, win rate, average damage or battles, with a battle minimum.</summary>
public sealed partial class ShipRankingPanel : AnalyticsPanel
{
    public ShipRankingPanel(IAnalyticsData data, bool best) : base(data)
    {
        Best = best;
        Metrics = best
            ? [new("PR", ShipMetric.Pr), new("Win rate", ShipMetric.WinRate), new("Average damage", ShipMetric.AvgDamage), new("Battles", ShipMetric.Battles)]
            : [new("Lowest PR", ShipMetric.Pr), new("Lowest win rate", ShipMetric.WinRate), new("Lowest average damage", ShipMetric.AvgDamage)];
        _metric = Metrics[0];
        _rankMin = RankMins[3];
    }

    public bool Best { get; }
    public string Title => Best ? "Best performing ships" : "Worst performing ships";
    public IReadOnlyList<FilterOption<ShipMetric>> Metrics { get; }
    public IReadOnlyList<MinBattlesOption> RankMins { get; } = [new(1), new(5), new(10), new(20), new(50), new(100)];
    [ObservableProperty] private FilterOption<ShipMetric> _metric;
    [ObservableProperty] private MinBattlesOption _rankMin;
    [ObservableProperty] private IReadOnlyList<ChartBar> _bars = [];
    public ObservableCollection<AnalyticsRow> Rows { get; } = [];

    partial void OnMetricChanged(FilterOption<ShipMetric> value) => Refresh();
    partial void OnRankMinChanged(MinBattlesOption value) => Refresh();

    public override void Refresh()
    {
        var records = Selected();
        var min = Math.Max(RankMin.Value ?? 1, Data.Global.MinBattles);
        var (top, qualified, without) = AnalyticsRanking.Rank(AnalyticsGroups.ByShip(records), Metric.Value, Best, min, 10);
        Rows.Clear();
        foreach (var g in top) Rows.Add(AnalyticsRow.From(g));
        var inv = CultureInfo.CurrentCulture;
        Bars = top.Select(g =>
        {
            var v = AnalyticsRanking.Value(g.Stats, Metric.Value)!.Value;
            var text = Metric.Value switch
            {
                ShipMetric.Pr => $"PR {v:0}",
                ShipMetric.WinRate => $"{v.ToString("0.0", inv)} %",
                ShipMetric.AvgDamage => $"{v.ToString("N0", inv)} dmg",
                _ => $"{v:N0} battles",
            };
            return new ChartBar($"{Fmt.Tier(g.Tier)} {g.Ship!.Name}", v,
                $"{text} · {g.Stats.Battles:N0} b · {Fmt.Pct(g.Stats.WinRate)} · {Fmt.Num(g.Stats.AvgDamage)} dmg",
                Metric.Value == ShipMetric.Pr ? RatingBrushConverter.For(g.Stats.Pr, "pr") : RatingBrushConverter.Res(Best ? "Accent" : "StatusRed"));
        }).ToList();
        Summary = $"Ships with {min:N0}+ battles: {qualified:N0}" + (qualified > top.Count ? $", top {top.Count} shown" : "")
                  + (without > 0 ? $" · {without:N0} without a {Metric.Label.Replace("Lowest ", "").ToLowerInvariant()} (not ranked)" : "")
                  + (min > 1 && !Best ? " · ships with fewer battles are not ranked, so a few unlucky battles do not count as poor" : "");
        EmptyText = top.Count > 0 ? null : $"No ships with {min:N0}+ battles match the filters.";
    }
}

/// <summary>Average damage or win rate per ship, tier or class: bars, a table, sorting and a minimum.</summary>
public sealed partial class ShipMetricPanel : AnalyticsPanel
{
    public ShipMetricPanel(IAnalyticsData data, ShipMetric metric) : base(data)
    {
        MetricKind = metric;
        _groupBy = GroupOptions[0];
        _order = Orders[0];
        // A win rate over one or two battles is 0 % or 100 % and says little: the win-rate list starts at 5 battles.
        _min = metric == ShipMetric.WinRate ? Mins[1] : Mins[0];
    }

    public ShipMetric MetricKind { get; }
    public bool IsDamage => MetricKind == ShipMetric.AvgDamage;
    public string Title => IsDamage ? "Average damage per ship" : "Win rate per ship";
    public IReadOnlyList<string> GroupOptions { get; } = ["Ship", "Tier", "Class"];
    public IReadOnlyList<string> Orders { get; } = ["Highest first", "Lowest first"];
    public IReadOnlyList<MinBattlesOption> Mins { get; } = [new(1), new(5), new(10), new(20), new(50), new(100)];
    [ObservableProperty] private string _groupBy;
    [ObservableProperty] private string _order;
    [ObservableProperty] private MinBattlesOption _min;
    [ObservableProperty] private IReadOnlyList<ChartBar> _bars = [];
    public ObservableCollection<AnalyticsRow> Rows { get; } = [];

    partial void OnGroupByChanged(string value) => Refresh();
    partial void OnOrderChanged(string value) => Refresh();
    partial void OnMinChanged(MinBattlesOption value) => Refresh();

    public override void Refresh()
    {
        var records = Selected();
        var groups = GroupBy switch
        {
            "Tier" => AnalyticsGroups.ByTier(records),
            "Class" => AnalyticsGroups.ByClass(records),
            _ => AnalyticsGroups.ByShip(records),
        };
        var min = Math.Max(Min.Value ?? 1, GroupBy == "Ship" ? Data.Global.MinBattles : 1);
        var (top, qualified, _) = AnalyticsRanking.Rank(groups, MetricKind, Order == "Highest first", min, GroupBy == "Ship" ? 15 : 20);
        Rows.Clear();
        foreach (var g in top) Rows.Add(AnalyticsRow.From(g));
        var inv = CultureInfo.CurrentCulture;
        Bars = top.Select(g => new ChartBar(
            g.Ship is { } s ? $"{Fmt.Tier(g.Tier)} {s.Name}" : g.Label,
            AnalyticsRanking.Value(g.Stats, MetricKind)!.Value,
            IsDamage
                ? $"{Fmt.Num(g.Stats.AvgDamage)} · {g.Stats.Battles:N0} b · {Fmt.Int(g.Stats.Damage)} total"
                : $"{Fmt.Pct(g.Stats.WinRate)} · {g.Stats.Wins:N0}–{g.Stats.Losses:N0}–{g.Stats.Draws:N0} of {g.Stats.Battles:N0}",
            IsDamage ? RatingBrushConverter.Res("Accent") : RatingBrushConverter.For(g.Stats.Battles >= 10 ? g.Stats.WinRate : null, "wr") ?? RatingBrushConverter.Res("Accent")))
            .ToList();
        var unit = GroupBy == "Ship" ? "ships" : GroupBy == "Tier" ? "tiers" : "classes";
        Summary = $"{qualified:N0} {unit} with {min:N0}+ battles" + (qualified > top.Count ? $", {top.Count} shown" : "")
                  + (IsDamage ? " · total damage ÷ battles" : " · victories ÷ battles (draws count as battles, not victories); groups add up their battles, they do not average win rates");
        EmptyText = top.Count > 0 ? null : "Nothing matches the filters.";
    }
}

/// <summary>Your ships side by side: pick any ships (all of a tier with "Select all shown"); a totals row adds them up.</summary>
public sealed partial class ShipComparePanel : AnalyticsPanel
{
    public ShipComparePanel(IAnalyticsData data) : base(data)
    {
        _metric = Metrics[0];
    }

    public ObservableCollection<ShipPick> Picks { get; } = [];
    public ObservableCollection<AnalyticsRow> Rows { get; } = [];
    public IReadOnlyList<FilterOption<ShipMetric>> Metrics { get; } =
        [new("PR", ShipMetric.Pr), new("Win rate", ShipMetric.WinRate), new("Average damage", ShipMetric.AvgDamage), new("Kills per battle", ShipMetric.Kills), new("Survival", ShipMetric.Survival), new("Battles", ShipMetric.Battles)];
    [ObservableProperty] private FilterOption<ShipMetric> _metric;
    [ObservableProperty] private IReadOnlyList<ChartBar> _bars = [];
    partial void OnMetricChanged(FilterOption<ShipMetric> value) => Refresh();
    private bool _quiet;

    [RelayCommand]
    private void SelectAll()
    {
        _quiet = true;
        foreach (var p in Picks) p.IsChecked = true;
        _quiet = false;
        Refresh();
    }

    [RelayCommand]
    private void SelectNone()
    {
        _quiet = true;
        foreach (var p in Picks) p.IsChecked = false;
        _quiet = false;
        Refresh();
    }

    public override void Refresh()
    {
        if (_quiet) return;
        var records = Selected();
        var groups = AnalyticsGroups.ByShip(records).OrderBy(g => g.Tier ?? 0).ThenBy(g => g.Label).ToList();
        // Keep the picks in step with the ships in the selection.
        var chosen = Picks.Where(p => p.IsChecked).Select(p => p.Ship.Id).ToHashSet();
        if (!Picks.Select(p => p.Ship.Id).SequenceEqual(groups.Select(g => g.Ship!.Id)))
        {
            _quiet = true;
            Picks.Clear();
            foreach (var g in groups) Picks.Add(new ShipPick(g.Ship!, g.Stats.Battles, Refresh) { IsChecked = chosen.Contains(g.Ship!.Id) });
            _quiet = false;
        }
        var picked = groups.Where(g => Picks.Any(p => p.IsChecked && p.Ship.Id == g.Ship!.Id)).ToList();
        Rows.Clear();
        foreach (var g in picked) Rows.Add(AnalyticsRow.From(g));
        if (picked.Count > 1)
        {
            var ids = picked.Select(g => g.Ship!.Id).ToHashSet();
            Rows.Add(new AnalyticsRow($"All {picked.Count} ships together", "", 0, AggregateStats.Of(records.Where(r => ids.Contains(r.Ship.Id))), true));
        }
        var inv = CultureInfo.CurrentCulture;
        Bars = picked.Where(g => AnalyticsRanking.Value(g.Stats, Metric.Value) is not null)
            .Select(g =>
            {
                var v = AnalyticsRanking.Value(g.Stats, Metric.Value)!.Value;
                var text = Metric.Value switch
                {
                    ShipMetric.Pr => v.ToString("0", inv), ShipMetric.WinRate or ShipMetric.Survival => v.ToString("0.0", inv) + " %",
                    ShipMetric.AvgDamage => v.ToString("N0", inv), ShipMetric.Kills => v.ToString("0.00", inv), _ => v.ToString("N0", inv),
                };
                return new ChartBar($"{Fmt.Tier(g.Tier)} {g.Ship!.Name}", v, $"{text} · {g.Stats.Battles:N0} b", RatingBrushConverter.Res("Accent"));
            }).ToList();
        Summary = picked.Count == 0
            ? $"{groups.Count:N0} ships in the selection. Tick ships to compare them; with the toolbar on one tier, \"Select all shown\" compares that tier's ships."
            : $"{picked.Count} of {groups.Count:N0} ships · every value shows the battles behind it; the totals row adds up the battles of the ticked ships";
        EmptyText = groups.Count > 0 ? null : "No ships match the filters.";
    }
}

/// <summary>Every tier in one table (all toolbar filters except the tier), rows clickable to filter by that tier.</summary>
public sealed partial class TierOverviewPanel : AnalyticsPanel
{
    private readonly Action<int> _pickTier;

    public TierOverviewPanel(IAnalyticsData data, Action<int> pickTier) : base(data)
    {
        _pickTier = pickTier;
        _metric = Metrics[0];
    }

    public ObservableCollection<AnalyticsRow> Rows { get; } = [];
    public IReadOnlyList<FilterOption<ShipMetric>> Metrics { get; } =
        [new("Win rate", ShipMetric.WinRate), new("PR", ShipMetric.Pr), new("Average damage", ShipMetric.AvgDamage), new("Kills per battle", ShipMetric.Kills), new("Survival", ShipMetric.Survival), new("Battles", ShipMetric.Battles)];
    [ObservableProperty] private FilterOption<ShipMetric> _metric;
    [ObservableProperty] private IReadOnlyList<ChartBar> _bars = [];
    partial void OnMetricChanged(FilterOption<ShipMetric> value) => Refresh();

    [RelayCommand]
    private void Pick(AnalyticsRow? row)
    {
        if (row is { Tier: > 0 }) _pickTier(row.Tier);
    }

    public override void Refresh()
    {
        var global = Data.Global with { Tiers = new HashSet<int>() };
        var records = Selected(global);
        var tiers = AnalyticsGroups.ByTier(records);
        Rows.Clear();
        foreach (var g in tiers) Rows.Add(AnalyticsRow.From(g, Data.Global.Tiers.Contains(g.Tier!.Value)) with { Label = g.Label });
        var total = AggregateStats.Of(records.Where(r => r.Ship.Tier is not null));
        if (tiers.Count > 1) Rows.Add(new AnalyticsRow("All tiers", "", 0, total, false));
        var inv = CultureInfo.CurrentCulture;
        Bars = tiers.Where(g => AnalyticsRanking.Value(g.Stats, Metric.Value) is not null).Select(g =>
        {
            var v = AnalyticsRanking.Value(g.Stats, Metric.Value)!.Value;
            var text = Metric.Value switch
            {
                ShipMetric.Pr => v.ToString("0", inv), ShipMetric.WinRate or ShipMetric.Survival => v.ToString("0.0", inv) + " %",
                ShipMetric.AvgDamage => v.ToString("N0", inv), ShipMetric.Kills => v.ToString("0.00", inv), _ => v.ToString("N0", inv),
            };
            return new ChartBar(g.Label, v, $"{text} · {g.Stats.Battles:N0} b",
                Data.Global.Tiers.Contains(g.Tier!.Value) ? RatingBrushConverter.Res("StatusAmber") : RatingBrushConverter.Res("Accent"));
        }).ToList();
        var unknown = records.Count(r => r.Ship.Tier is null);
        Summary = "Every tier, with the other toolbar filters (the toolbar's tier choice is highlighted, not applied). Click a row to filter by that tier."
                  + (unknown > 0 ? $" {unknown:N0} record(s) of ships with an unknown tier are left out." : "");
        EmptyText = tiers.Count > 0 ? null : "No battles match the filters.";
    }
}

/// <summary>A tier group for the comparison: chips I–XI.</summary>
public sealed partial class TierGroup : ObservableObject
{
    public TierGroup(string name, Action changed)
    {
        Name = name;
        Chips = Enumerable.Range(1, 11).Select(t => new FilterChip(ShipRanking.Roman(t), t, changed)).ToList();
    }

    public string Name { get; }
    public IReadOnlyList<FilterChip> Chips { get; }
    public HashSet<int> Tiers => Chips.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet();
    public string Label => Tiers.Count == 0 ? "All tiers" : ShipRanking.TierLabel(Tiers);
}

public sealed record TierCompareRow(string Metric, string A, string B, string C, string Diff, string DiffPct, string Level, string Basis);

/// <summary>Two or three tier groups compared on the same records (toolbar filters except the tier, plus a class filter).</summary>
public sealed partial class TierComparePanel : AnalyticsPanel
{
    private bool _quiet;

    public TierComparePanel(IAnalyticsData data) : base(data)
    {
        A = new TierGroup("A", Refresh);
        B = new TierGroup("B", Refresh);
        C = new TierGroup("C", Refresh);
        Preset("VIII vs X");
    }

    public TierGroup A { get; }
    public TierGroup B { get; }
    public TierGroup C { get; }
    public IReadOnlyList<string> Presets { get; } = ["VIII vs X", "VI–VIII vs IX–XI", "VIII+ vs all", "VII vs VIII vs IX", "All vs VI+", "VI+ vs VIII+"];
    public ObservableCollection<TierCompareRow> Rows { get; } = [];
    [ObservableProperty] private bool _useC;
    [ObservableProperty] private string _headerA = "";
    [ObservableProperty] private string _headerB = "";
    [ObservableProperty] private string _headerC = "";
    partial void OnUseCChanged(bool value) => Refresh();

    [RelayCommand]
    private void Preset(string? name)
    {
        (int[] a, int[] b, int[]? c) = name switch
        {
            "VI–VIII vs IX–XI" => (new[] { 6, 7, 8 }, new[] { 9, 10, 11 }, (int[]?)null),
            "VIII+ vs all" => (new[] { 8, 9, 10, 11 }, Array.Empty<int>(), null),
            "VII vs VIII vs IX" => (new[] { 7 }, new[] { 8 }, new[] { 9 }),
            "All vs VI+" => (Array.Empty<int>(), new[] { 6, 7, 8, 9, 10, 11 }, null),
            "VI+ vs VIII+" => (new[] { 6, 7, 8, 9, 10, 11 }, new[] { 8, 9, 10, 11 }, null),
            _ => (new[] { 8 }, new[] { 10 }, null),
        };
        _quiet = true;
        foreach (var (g, tiers) in new[] { (A, a), (B, b), (C, c ?? []) })
            foreach (var chip in g.Chips) chip.IsChecked = tiers.Contains(chip.Value);
        UseC = c is not null;
        _quiet = false;
        Refresh();
    }

    public override void Refresh()
    {
        if (_quiet) return;
        var global = Data.Global with { Tiers = new HashSet<int>() };
        AggregateStats Of(TierGroup g) => AggregateStats.Of(Selected(global with { Tiers = g.Tiers }));
        var sa = Of(A);
        var sb = Of(B);
        var sc = UseC ? Of(C) : null;
        HeaderA = A.Label;
        HeaderB = B.Label;
        HeaderC = UseC ? C.Label : "";
        Rows.Clear();
        void Row(string metric, Func<AggregateStats, double?> v, string f, string unit = "", bool higherIsBetter = true, bool neutral = false, bool relative = true)
        {
            string F(AggregateStats? s) => s is null ? "" : v(s) is double d ? d.ToString(f, CultureInfo.CurrentCulture) + unit : "—";
            var (x, y) = (v(sa), v(sb));
            var diff = x is double dx && y is double dy ? dy - dx : (double?)null;
            var level = !neutral && diff is double dd && Math.Abs(dd) > 1e-9 ? (dd > 0 == higherIsBetter ? "good" : "bad") : "";
            Rows.Add(new TierCompareRow(metric, F(sa), F(sb), F(sc), Fmt.Signed(diff, f, unit), relative ? Fmt.Relative(x, y) : "", level,
                $"A: {sa.Battles:N0} battles, B: {sb.Battles:N0}" + (sc is null ? "" : $", C: {sc.Battles:N0}")));
        }
        Row("Battles", s => s.Battles, "N0", neutral: true, relative: false);
        Row("Win rate", s => s.WinRate, "0.0", " %", relative: false);
        Row("PR", s => s.Pr, "0");
        Row("Average damage", s => s.AvgDamage, "N0");
        Row("Average base XP", s => s.BaseXp.Value, "N0");
        Row("Kills per battle", s => s.AvgKills, "0.00");
        Row("Deaths per battle", s => s.AvgDeaths, "0.00", higherIsBetter: false);
        Row("Kills/deaths", s => s.KillDeath, "0.00");
        Row("Survival rate", s => s.SurvivalRate, "0.0", " %", relative: false);
        Summary = "Same toolbar filters for every group except the tier (the groups choose the tiers)"
                  + (Local.IsActive ? ", plus the chart filter" : "") + ". Difference = B − A; % = relative to A. Rates differ in points, not percent."
                  + (Data.Source == AnalyticsSource.Account ? " Base XP is not in the account totals." : "");
        EmptyText = sa.Battles + sb.Battles == 0 ? "No battles in either group." : null;
    }
}

/// <summary>A PR line for the progression chart.</summary>
public sealed record PrLine(string Name, Brush Stroke, string Latest, string Battles, string Range);

/// <summary>PR over time from recorded battles, for several tier selections at once (calculated by Foretop).</summary>
public sealed partial class PrProgressionPanel : AnalyticsPanel
{
    private static readonly string[] Palette = ["Accent", "StatusAmber", "StatusGreen", "RatePurple", "Enemy", "Friendly"];
    private bool _quiet;

    public PrProgressionPanel(IAnalyticsData data) : base(data)
    {
        LineChips = new[] { "Selection", "All tiers", "VI+", "VIII+", "X+" }.Concat(Enumerable.Range(1, 11).Select(ShipRanking.Roman))
            .Select((l, i) => new FilterChip(l, i, Refresh) { IsChecked = i == 0 }).ToList();
        _window = Windows[0];
    }

    public IReadOnlyList<FilterChip> LineChips { get; }
    public IReadOnlyList<FilterOption<int?>> Windows { get; } = [new("Overall (all battles so far)", null), new("Rolling 10 battles", 10), new("Rolling 20 battles", 20)];
    [ObservableProperty] private FilterOption<int?> _window;
    [ObservableProperty] private IReadOnlyList<ChartSeries> _series = [];
    [ObservableProperty] private double? _reference;
    [ObservableProperty] private string _referenceText = "";
    public ObservableCollection<PrLine> Lines { get; } = [];
    partial void OnWindowChanged(FilterOption<int?> value) => Refresh();

    /// <summary>The account PR for the same ship filters (official per-ship totals), drawn as a dashed line.</summary>
    public Func<AnalyticsFilter, (double? Pr, int Battles)>? AccountReference { get; set; }

    [RelayCommand]
    private void Compare(string? which)
    {
        _quiet = true;
        foreach (var c in LineChips) c.IsChecked = which switch
        {
            "VIII vs X" => c.Label is "VIII" or "X",
            "Bands" => c.Label is "All tiers" or "VI+" or "VIII+",
            _ => c.Value == 0,
        };
        _quiet = false;
        Refresh();
    }

    public override void Refresh()
    {
        if (_quiet) return;
        Lines.Clear();
        if (Data.Source != AnalyticsSource.Recorded)
        {
            Series = [];
            Reference = null;
            EmptyText = "PR over time needs dates, so it uses recorded battles: switch the toolbar's source to \"Recorded battles\".";
            Summary = "";
            return;
        }
        var global = Data.Global;
        var series = new List<ChartSeries>();
        var colour = 0;
        foreach (var chip in LineChips.Where(c => c.IsChecked))
        {
            var filter = chip.Value switch
            {
                0 => global,
                1 => global with { Tiers = new HashSet<int>() },
                2 => global with { Tiers = Enumerable.Range(6, 6).ToHashSet() },
                3 => global with { Tiers = Enumerable.Range(8, 4).ToHashSet() },
                4 => global with { Tiers = new HashSet<int> { 10, 11 } },
                _ => global with { Tiers = new HashSet<int> { chip.Value - 4 } },
            };
            var name = chip.Value == 0 ? (global.Tiers.Count == 0 ? "Selection (all tiers)" : "Selection: " + ShipRanking.TierLabel(global.Tiers))
                : chip.Value <= 4 ? chip.Label : "Tier " + chip.Label;
            var records = Selected(filter).Where(r => r.When is not null).OrderBy(r => r.When).ToList();
            var points = PrProgression.Build(records, Window.Value);
            var brush = RatingBrushConverter.Res(Palette[colour++ % Palette.Length]);
            if (points.Count == 0)
            {
                Lines.Add(new PrLine(name, brush, "—", "no battles with expected values", ""));
                continue;
            }
            var inv = CultureInfo.CurrentCulture;
            series.Add(new ChartSeries(name, brush, points.Select(p => new ChartPoint(
                p.Record.When!.Value.ToUnixTimeSeconds() / 86400.0, p.Pr, p.Record.When.Value.LocalDateTime.ToString("d", inv),
                $"{name}\n{p.Record.When.Value.LocalDateTime:g} · {p.Record.Ship.Name}\nPR {p.Pr:0} over {p.Battles:N0} battle(s)")).ToList()));
            var last = points[^1];
            Lines.Add(new PrLine(name, brush, last.Pr.ToString("0", inv), $"{records.Count(r => r.Pr.Battles > 0):N0} battles",
                $"{points[0].Record.When!.Value.LocalDateTime:d} – {last.Record.When!.Value.LocalDateTime:d}"));
        }
        Series = series;
        if (AccountReference?.Invoke(global with { From = null, To = null, LastBattles = null, Mode = null }) is { Pr: double refPr } r)
        {
            Reference = refPr;
            ReferenceText = $"Dashed line: your account PR for the same ships and tiers, {refPr:0} over {r.Battles:N0} Random battles (official per-ship totals, current expected values).";
        }
        else
        {
            Reference = null;
            ReferenceText = "";
        }
        Summary = (Window.Value is int w ? $"PR of the last {w} recorded battles at each point" : "PR of all recorded battles up to each point")
                  + " — calculated by Foretop with the wows-numbers formula and the expected values stored with each battle; battles without them are left out. Not an official rating.";
        EmptyText = series.Count > 0 ? null : "No recorded battles with expected values match the filters.";
    }
}
