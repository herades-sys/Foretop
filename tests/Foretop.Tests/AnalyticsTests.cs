using Foretop.Core.Personal;
using Foretop.Core.Stats;
using Xunit;

namespace Foretop.Tests;

public class AnalyticsTests
{
    private static readonly ShipMeta Bb8 = new(1, "Bb Eight", 8, "Battleship", "usa");
    private static readonly ShipMeta Ca8 = new(2, "Ca Eight", 8, "Cruiser", "japan");
    private static readonly ShipMeta Bb10 = new(3, "Bb Ten", 10, "Battleship", "germany");
    private static readonly ShipMeta Dd6 = new(4, "Dd Six", 6, "Destroyer", "usa");
    private static readonly ShipMeta Unknown = new(5, "Old ship", null, null, null);
    private static readonly ExpectedValues Exp = new(50_000, 1.0, 50);

    private static StatTotals T(int battles, int wins, long damage, int frags, int survived, int draws = 0) =>
        new(battles, wins, battles - wins - draws, draws, damage, battles * 1000L, frags, survived, 0);

    // Account-style records: per-ship totals.
    private static readonly AnalyticsRecord[] Account =
    [
        AnalyticsRecord.FromShip(new ShipPerformance(1, T(100, 60, 6_000_000, 90, 40), null, null), Bb8, Exp),
        AnalyticsRecord.FromShip(new ShipPerformance(2, T(10, 2, 300_000, 5, 1), null, null), Ca8, Exp),
        AnalyticsRecord.FromShip(new ShipPerformance(3, T(30, 15, 2_400_000, 30, 12, draws: 1), null, null), Bb10, Exp),
        AnalyticsRecord.FromShip(new ShipPerformance(4, T(50, 20, 1_500_000, 40, 10), null, null), Dd6, null), // no expected values
        AnalyticsRecord.FromShip(new ShipPerformance(5, T(5, 5, 500_000, 10, 5), null, null), Unknown, Exp),
    ];

    [Fact]
    public void Aggregates_divide_totals_not_averages_of_ships()
    {
        var s = AggregateStats.Of(Account.Where(r => r.Ship.Tier == 8));
        Assert.Equal(110, s.Battles);
        Assert.Equal(100.0 * 62 / 110, s.WinRate!.Value, 9);          // not (60 % + 20 %) / 2 = 40 %
        Assert.Equal(6_300_000.0 / 110, s.AvgDamage!.Value, 9);
        Assert.Equal(95.0 / 110, s.AvgKills!.Value, 9);
        Assert.Equal(69, s.Deaths);                                    // (100 − 40) + (10 − 1)
        Assert.Equal(69.0 / 110, s.AvgDeaths!.Value, 9);
        Assert.Equal(95.0 / 69, s.KillDeath!.Value, 9);
        Assert.Equal(100.0 * 41 / 110, s.SurvivalRate!.Value, 9);
        Assert.Equal(2, s.Ships);
    }

    [Fact]
    public void Draws_are_neither_wins_nor_losses()
    {
        var s = AggregateStats.Of(Account.Where(r => r.Ship.Id == 3));
        Assert.Equal((30, 15, 14, 1), (s.Battles, s.Wins, s.Losses, s.Draws));
        Assert.Equal(50.0, s.WinRate!.Value, 9);
    }

    [Fact]
    public void Pr_matches_the_existing_calculation_and_skips_ships_without_expected_values()
    {
        var expected = new Dictionary<long, ExpectedValues> { [1] = Exp, [2] = Exp, [3] = Exp, [5] = Exp };
        var ships = Account.Select(r => (r.Ship.Id, r.Totals));
        var s = AggregateStats.Of(Account);
        Assert.Equal(PersonalRating.Compute(ships, expected)!.Value, s.Pr!.Value, 6);
        Assert.Equal(145, s.PrBattles); // the destroyer (50 battles) has no expected values
    }

    [Fact]
    public void Recorded_battle_pr_uses_the_expected_values_stored_with_each_battle()
    {
        static PersonalBattle B(int day, long ship, bool win, long dmg, int kills, double[] exp) => new(
            $"k{day}", new DateTimeOffset(2026, 11, day, 20, 0, 0, TimeSpan.Zero), ResultModes.Random, "pvp", "map", ship, "x", 8, "Battleship",
            new StatTotals(1, win ? 1 : 0, win ? 0 : 1, 0, dmg, 1000, kills, win ? 1 : 0, 0), new PersonalExtras { RatingExpected = exp });
        var a = AnalyticsRecord.FromBattle(B(1, 1, true, 80_000, 2, [60_000, 1.0, 50]), Bb8);
        var b = AnalyticsRecord.FromBattle(B(2, 1, false, 20_000, 0, [40_000, 0.8, 48]), Bb8);
        var s = AggregateStats.Of([a, b]);
        // Same as the period PR of the existing analytics (sums against sums).
        var period = PeriodStats.Of([a.Battle!, b.Battle!]);
        Assert.Equal(period.Pr!.Value, s.Pr!.Value, 9);
        Assert.Equal(2, s.PrBattles);
    }

    [Fact]
    public void Missing_values_are_not_zero()
    {
        var empty = AggregateStats.Empty;
        Assert.Null(empty.WinRate);
        Assert.Null(empty.AvgDamage);
        Assert.Null(empty.KillDeath);
        Assert.Null(empty.Pr);
        Assert.Null(empty.BaseXp.Value);
        var noDeaths = AggregateStats.Of(Account.Where(r => r.Ship.Id == 5));
        Assert.Null(noDeaths.KillDeath); // 5 battles, all survived
        Assert.Null(AggregateStats.Of(Account.Where(r => r.Ship.Id == 4)).Pr);
    }

    [Fact]
    public void Filters_work_together()
    {
        var f = new AnalyticsFilter { Tiers = new HashSet<int> { 8, 10 }, Classes = new HashSet<string> { "Battleship" }, MinBattles = 20 };
        Assert.Equal([1L, 3L], f.Apply(Account).Select(r => r.Ship.Id).Order());
        f = f with { MinBattles = 50 };
        Assert.Equal([1L], f.Apply(Account).Select(r => r.Ship.Id));
        f = new AnalyticsFilter { Nations = new HashSet<string> { "usa" } };
        Assert.Equal([1L, 4L], f.Apply(Account).Select(r => r.Ship.Id).Order());
    }

    [Fact]
    public void Tier_filter_never_includes_unknown_tiers()
    {
        var all = AnalyticsFilter.All.Apply(Account);
        Assert.Contains(all, r => r.Ship.Id == 5);
        Assert.Equal(1, AggregateStats.Of(all).UnknownTier);
        var tiered = new AnalyticsFilter { Tiers = Enumerable.Range(1, 11).ToHashSet() }.Apply(Account);
        Assert.DoesNotContain(tiered, r => r.Ship.Id == 5);
    }

    [Fact]
    public void Date_filters_need_dates()
    {
        var f = new AnalyticsFilter { From = DateTimeOffset.UtcNow.AddDays(-90) };
        Assert.Empty(f.Apply(Account)); // account totals have no dates: they cannot match a date range
        Assert.True(f.NeedsDates);
    }

    [Fact]
    public void Chart_filters_only_narrow_the_global_filter()
    {
        var global = new AnalyticsFilter { Tiers = new HashSet<int> { 8 }, MinBattles = 5 };
        var local = new AnalyticsFilter { Classes = new HashSet<string> { "Battleship" }, MinBattles = 1 };
        var both = global.Intersect(local);
        Assert.Equal([8], both.Tiers);
        Assert.Equal(["Battleship"], both.Classes);
        Assert.Equal(5, both.MinBattles);
        Assert.Equal([1L], both.Apply(Account).Select(r => r.Ship.Id));

        var disjoint = global.Intersect(new AnalyticsFilter { Tiers = new HashSet<int> { 10 } });
        Assert.True(disjoint.NoMatch);
        Assert.Empty(disjoint.Apply(Account)); // Tier VIII and Tier X: nothing, not "Tier X"

        var modes = new AnalyticsFilter { Mode = "pvp" }.Intersect(new AnalyticsFilter { Mode = "rank_solo" });
        Assert.True(modes.NoMatch);
    }

    [Fact]
    public void Last_battles_then_minimum_per_ship()
    {
        var day = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var recs = Enumerable.Range(0, 6).Select(i => new AnalyticsRecord(i < 4 ? Bb8 : Ca8, T(1, 1, 50_000, 1, 1), day.AddDays(i), "pvp", PrParts.None, null)).ToList();
        // newest 3 = days 5, 4 (Ca8) and 3 (Bb8)
        var last3 = new AnalyticsFilter { LastBattles = 3 }.Apply(recs);
        Assert.Equal([5, 4, 3], last3.Select(r => r.When!.Value.Day - 1));
        var withMin = new AnalyticsFilter { LastBattles = 3, MinBattles = 2 }.Apply(recs);
        Assert.All(withMin, r => Assert.Equal(Ca8, r.Ship));
    }

    [Fact]
    public void Grouping_by_tier_and_class_uses_totals()
    {
        var tiers = AnalyticsGroups.ByTier(Account);
        Assert.Equal([6, 8, 10], tiers.Select(g => g.Tier!.Value));
        Assert.Equal(110, tiers.Single(g => g.Tier == 8).Stats.Battles);
        var classes = AnalyticsGroups.ByClass(Account);
        Assert.Equal(130, classes.Single(g => g.Key == "Battleship").Stats.Battles);
        Assert.Equal(100.0 * 75 / 130, classes.Single(g => g.Key == "Battleship").Stats.WinRate!.Value, 9);
    }

    [Fact]
    public void Rankings_respect_the_minimum_and_never_rank_missing_values()
    {
        var ships = AnalyticsGroups.ByShip(Account);
        var (best, qualified, without) = AnalyticsRanking.Rank(ships, ShipMetric.WinRate, highestFirst: true, minBattles: 20, take: 10);
        Assert.Equal([1L, 3L, 4L], best.Select(g => g.Ship!.Id)); // 60 %, 50 %, 40 %; the 10- and 5-battle ships are not ranked
        Assert.Equal(3, qualified);
        Assert.Equal(0, without);
        var (worst, _, _) = AnalyticsRanking.Rank(ships, ShipMetric.WinRate, highestFirst: false, minBattles: 20, take: 2);
        Assert.Equal([4L, 3L], worst.Select(g => g.Ship!.Id));
        var (byPr, _, noPr) = AnalyticsRanking.Rank(ships, ShipMetric.Pr, highestFirst: true, minBattles: 20, take: 10);
        Assert.DoesNotContain(byPr, g => g.Ship!.Id == 4);
        Assert.Equal(1, noPr);
    }

    [Fact]
    public void Pr_progression_is_the_formula_on_running_totals()
    {
        var day = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var recs = new[]
        {
            new AnalyticsRecord(Bb8, T(1, 1, 100_000, 2, 1), day, "pvp", new PrParts(100_000, 2, 1, 50_000, 1, 0.5, 1), null),
            new AnalyticsRecord(Bb8, T(1, 0, 10_000, 0, 0), day.AddDays(1), "pvp", PrParts.None, null), // no expected values: skipped
            new AnalyticsRecord(Bb8, T(1, 0, 20_000, 0, 0), day.AddDays(2), "pvp", new PrParts(20_000, 0, 0, 50_000, 1, 0.5, 1), null),
        };
        var all = PrProgression.Build(recs, null);
        Assert.Equal(2, all.Count);
        Assert.Equal(new PrParts(100_000, 2, 1, 50_000, 1, 0.5, 1).Pr!.Value, all[0].Pr, 9);
        var both = new PrParts(120_000, 2, 1, 100_000, 2, 1, 2).Pr!.Value;
        Assert.Equal(both, all[1].Pr, 9);
        Assert.Equal(2, all[1].Battles);
        var rolling = PrProgression.Build(recs, 1);
        Assert.Equal(new PrParts(20_000, 0, 0, 50_000, 1, 0.5, 1).Pr ?? -1, rolling[1].Pr, 9);
    }
}
