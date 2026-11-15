using Foretop.Core.Stats;

namespace Foretop.Tests;

public class TierFilterTests
{
    private static StatTotals T(int battles, int wins, long damage, int frags, int survived, long xp = 0) =>
        new(battles, wins, battles - wins, 0, damage, xp, frags, survived, 0);

    // Ship id = tier * 100 for readability; 999 has no known tier.
    private static readonly ShipPerformance[] Ships =
    [
        new(300, T(1000, 600, 20_000_000, 1000, 500, 600_000), null, null), // tier III: many easy battles
        new(600, T(200, 100, 8_000_000, 160, 80, 240_000), null, null),
        new(800, T(100, 45, 6_000_000, 70, 30, 150_000), null, null),
        new(1100, T(10, 5, 1_500_000, 10, 4, 20_000), null, null),
        new(999, T(50, 40, 5_000_000, 50, 25, 50_000), null, null),
    ];

    private static int? TierOf(long id) => id == 999 ? null : (int)(id / 100);

    private static readonly Dictionary<long, ExpectedValues> Expected = new()
    {
        [300] = new(15_000, 0.8, 50), [600] = new(35_000, 0.7, 50), [800] = new(55_000, 0.65, 50), [1100] = new(140_000, 0.8, 50),
    };

    [Fact]
    public void Filtered_totals_are_weighted_sums_not_averages_of_averages()
    {
        var vi = TierFilter.ComputeAll(Ships, TierOf, Expected).Single(t => t.MinTier == 6);
        Assert.Equal(310, vi.Totals.Battles);           // 200 + 100 + 10; tier III and the unknown ship left out
        Assert.Equal(150, vi.Totals.Wins);
        Assert.Equal(150.0 * 100 / 310, vi.Totals.WinRate!.Value, 6);     // not (50 % + 45 % + 50 %) / 3
        Assert.Equal(15_500_000.0 / 310, vi.Totals.AvgDamage!.Value, 6);
        Assert.Equal(410_000.0 / 310, vi.Totals.AvgXp!.Value, 6);
        Assert.Equal(114.0 * 100 / 310, vi.Totals.SurvivalRate!.Value, 6);
        Assert.Equal(240.0 / (310 - 114), vi.Totals.KillDeath!.Value, 6);
        Assert.Equal(3, vi.Ships);
        Assert.Equal(1, vi.UnknownTierShips);
    }

    [Fact]
    public void Pr_uses_the_same_ships_as_the_totals()
    {
        var levels = TierFilter.ComputeAll(Ships, TierOf, Expected);
        var viii = levels.Single(t => t.MinTier == 8);
        var direct = PersonalRating.Compute([(800L, Ships[2].Totals), (1100L, Ships[3].Totals)], Expected);
        Assert.Equal(direct, viii.Pr);
        Assert.NotEqual(levels.Single(t => t.MinTier == 5).Pr, viii.Pr);
    }

    [Fact]
    public void Tier_xi_only_and_empty_levels()
    {
        var levels = TierFilter.ComputeAll(Ships, TierOf, Expected);
        Assert.Equal(10, levels.Single(t => t.MinTier == 11).Totals.Battles);
        Assert.Equal([5, 6, 7, 8, 9, 10, 11], levels.Select(t => t.MinTier));

        var low = TierFilter.ComputeAll([Ships[0]], TierOf, Expected).Single(t => t.MinTier == 6);
        Assert.Equal(0, low.Totals.Battles);
        Assert.Null(low.Totals.WinRate);   // shown as "—", not 0 %
        Assert.Null(low.Pr);
    }

    [Fact]
    public void Kill_death_is_unavailable_without_deaths()
    {
        Assert.Null(T(5, 5, 100, 7, 5).KillDeath);
        Assert.Equal(2.0, T(5, 3, 100, 6, 2).KillDeath);
    }

    [Fact]
    public void Projection_replaces_overall_and_pr_and_keeps_missing_data_missing()
    {
        var p = new PlayerSummary
        {
            Name = "A", Overall = T(1360, 750, 40_500_000, 1290, 639), Pr = 1500, DamageRatio = 1.1,
            Recent7 = new RecentStats(T(10, 5, 1, 1, 1), DateTimeOffset.UtcNow.AddDays(-7)),
            ByTier = TierFilter.ComputeAll(Ships, TierOf, Expected),
        };
        Assert.Same(p, TierFilter.Project(p, 0));
        var vi = TierFilter.Project(p, 6);
        Assert.Equal(310, vi.Overall!.Battles);
        Assert.Equal(p.ByTier!.Single(t => t.MinTier == 6).Pr, vi.Pr);
        Assert.Null(vi.Recent7); // snapshots have no per-tier split

        var noShips = TierFilter.Project(p with { ByTier = null }, 6);
        Assert.Null(noShips.Overall);
        Assert.Null(noShips.Pr);
    }

    [Fact]
    public void Tier_totals_survive_saving_a_battle()
    {
        var p = new PlayerSummary { Name = "A", Ships = Ships, ByTier = TierFilter.ComputeAll(Ships, TierOf, Expected) };
        var back = BattleSummaryCodec.Deserialize(BattleSummaryCodec.Serialize([p])).Single();
        Assert.Null(back.Ships);
        Assert.Equal(p.ByTier!.Select(t => (t.MinTier, t.Totals, t.Pr)), back.ByTier!.Select(t => (t.MinTier, t.Totals, t.Pr)));
    }

    [Theory]
    [InlineData(0, "All tiers", "ALL TIERS")]
    [InlineData(6, "Tier VI+", "TIER VI+")]
    [InlineData(11, "Tier XI only", "TIER XI")]
    public void Labels(int min, string label, string badge)
    {
        Assert.Equal(label, TierFilter.Label(min));
        Assert.Equal(badge, TierFilter.Badge(min));
    }
}
