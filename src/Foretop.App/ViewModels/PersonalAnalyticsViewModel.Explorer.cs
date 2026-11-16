using CommunityToolkit.Mvvm.ComponentModel;
using Foretop.Core.Personal;
using Foretop.Core.Stats;

namespace Foretop.App.ViewModels;

/// <summary>
/// The Analytics filter toolbar and the panels it drives. All panels read the same records (recorded battles or the
/// account's per-ship totals) through <see cref="IAnalyticsData"/>; filtering and sums happen in memory on the data
/// loaded once per reload, so changing a filter makes no API call and no database query.
/// </summary>
public sealed partial class PersonalAnalyticsViewModel
{
    private List<AnalyticsRecord> _recorded = [];
    private List<AnalyticsRecord> _accountRecords = [];
    private List<ShipPerformance> _accountShips = [];
    private IReadOnlyDictionary<long, ExpectedValues> _expected = new Dictionary<long, ExpectedValues>();
    private AnalyticsFilter _global = AnalyticsFilter.All;
    private List<AnalyticsRecord> _filtered = [];
    private bool _rebuildQueued;

    public AnalyticsFilterViewModel Filters { get; }
    public ShipRankingPanel Best { get; }
    public ShipRankingPanel Worst { get; }
    public ShipMetricPanel DamagePerShip { get; }
    public ShipMetricPanel WinRatePerShip { get; }
    public ShipComparePanel ShipCompare { get; }
    public TierOverviewPanel TierOverview { get; }
    public TierComparePanel TierCompare { get; }
    public PrProgressionPanel PrTrend { get; }

    private IEnumerable<AnalyticsPanel> Panels => [Best, Worst, DamagePerShip, WinRatePerShip, ShipCompare, TierOverview, TierCompare, PrTrend];

    // IAnalyticsData
    public IReadOnlyList<AnalyticsRecord> Records => IsRecordedSource ? _recorded : _accountRecords;
    public AnalyticsFilter Global => _global;
    public AnalyticsSource Source => Filters.Source;

    public bool IsRecordedSource => Filters.Source == AnalyticsSource.Recorded;
    public bool IsAccountSource => !IsRecordedSource;

    /// <summary>"No results recorded yet": only relevant while looking at recorded battles.</summary>
    public string? RecordedEmptyText => IsRecordedSource && EmptyText.Length > 0 ? EmptyText : null;

    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _showLast;
    [ObservableProperty] private string _sourceNote = "";

    /// <summary>Filter changes arrive in bursts (a preset sets several chips): rebuild once, after them.</summary>
    private void ScheduleRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Rebuild, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BuildRecorded() =>
        _recorded = _all.Select(b => AnalyticsRecord.FromBattle(b, Meta(b.ShipId, b.ShipName, b.Tier, b.ShipClass))).ToList();

    private void BuildAccountRecords() =>
        _accountRecords = _accountShips.Select(sh => AnalyticsRecord.FromShip(sh, Meta(sh.ShipId, null, null, null), _expected.GetValueOrDefault(sh.ShipId))).ToList();

    private void ScheduleMetaRefresh() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (!_loaded) return;
        BuildRecorded();
        BuildAccountRecords();
        ScheduleRebuild();
    });

    /// <summary>Tier, class and nation of a ship: the battle's own record first, then the ship list (API, else game files).</summary>
    private ShipMeta Meta(long id, string? name, int? tier, string? cls)
    {
        var api = _host.Ships.Get(id);
        var game = api is null ? _host.ShipData.FindById(id) : null;
        return new ShipMeta(id,
            name ?? api?.Name ?? game?.DisplayName ?? $"Ship {id}",
            tier is > 0 ? tier : api?.Tier is int t and > 0 ? t : game?.Tier is int g and > 0 ? g : null,
            cls ?? (api is not null && api.Class != ShipClass.Unknown ? api.Class.ToString() : game?.Species is { Length: > 0 } sp ? sp : null),
            (api?.Nation ?? game?.Nation)?.ToLowerInvariant());
    }

    private void UpdateSelectionText()
    {
        var s = AggregateStats.Of(_filtered);
        var total = Records.Sum(r => r.Totals.Battles);
        SelectionText = Records.Count == 0
            ? (IsRecordedSource ? "No recorded battles yet." : "Your account totals are not loaded (see below).")
            : _global.NoMatch ? "These filters exclude each other: nothing matches."
            : $"{s.Battles:N0} of {total:N0} battles match" + (IsAccountSource ? $" · {s.Ships:N0} ship{(s.Ships == 1 ? "" : "s")}" : "")
              + (s.UnknownTier > 0 ? $" · {s.UnknownTier:N0} with an unknown tier" : "");
        SourceNote = IsAccountSource
            ? "Whole account: the official per-ship totals of all your Random battles. They have no dates or battle types, so the period and battle type apply to recorded battles only. "
              + "Battle-by-battle sections (last battle, trends, history, compare periods, PR over time) need recorded battles."
            : "Recorded battles: the battles Foretop recorded while it ran, with dates, battle types and replay details.";
    }

    /// <summary>Statistics cards for the account totals (fewer than for recorded battles: no replay or per-battle data).</summary>
    private void BuildAccountCards()
    {
        if (_filtered.Count == 0) return;
        var s = AggregateStats.Of(_filtered);
        var n = s.Battles;
        Cards.Add(new StatCard("Battles", n.ToString("N0", Inv), $"{s.Wins:N0} victories · {s.Losses:N0} defeats · {s.Draws:N0} draws · {s.Ships:N0} ships", "official"));
        Cards.Add(new StatCard("Win rate", Fmt.Pct(s.WinRate), "victories ÷ battles", "calculated", RatingBrushConverter.For(n >= 10 ? s.WinRate : null, "wr")));
        Cards.Add(new StatCard("PR", Fmt.Pr(s.Pr), s.Pr is null ? "no expected values" : $"over {s.PrBattles:N0} battles, wows-numbers formula", "calculated",
            RatingBrushConverter.For(s.Pr, "pr")));
        Cards.Add(new StatCard("Average damage", Fmt.Num(s.AvgDamage), $"{s.Damage:N0} in total", "calculated"));
        Cards.Add(new StatCard("Kills", s.Kills.ToString("N0", Inv), $"{Fmt.Dec(s.AvgKills)} per battle", "official"));
        Cards.Add(new StatCard("Deaths", s.Deaths.ToString("N0", Inv), $"{Fmt.Dec(s.AvgDeaths)} per battle (battles − survived)", "calculated"));
        Cards.Add(new StatCard("Kills/deaths", Fmt.Dec(s.KillDeath), s.Deaths == 0 ? "no deaths" : "kills ÷ deaths", "calculated"));
        Cards.Add(new StatCard("Survival rate", Fmt.Pct(s.SurvivalRate), $"{n - s.Deaths:N0} of {n:N0} survived", "calculated"));
        Cards.Add(new StatCard("Average XP", Fmt.Num(s.AvgXp), "with Premium and bonuses", "calculated"));
    }

    /// <summary>Account PR for the ship filters of a selection (for the PR-over-time reference line).</summary>
    private (double? Pr, int Battles) AccountPr(AnalyticsFilter filter)
    {
        if (_accountRecords.Count == 0) return (null, 0);
        var s = AggregateStats.Of(filter.Apply(_accountRecords));
        return (s.Pr, s.PrBattles);
    }
}
