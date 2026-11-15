using Foretop.Core.Stats;
using Xunit;

namespace Foretop.Tests;

public class ShipRankingTests
{
    private static (ShipPerformance, ShipInfo?) Ship(long id, int tier, int battles, double? pr, string? name = null) =>
        (new ShipPerformance(id, new StatTotals(battles, battles / 2, battles - battles / 2, 0, battles * 50_000L, battles * 1000L, battles, battles / 3, 0), pr, null),
         tier == 0 ? null : new ShipInfo(id, name ?? $"Ship {id}", tier, ShipClass.Cruiser, "usa", false, false, null, null));

    private static readonly (ShipPerformance, ShipInfo?)[] Ships =
    [
        Ship(1, 8, 45, 3200),
        Ship(2, 8, 32, 2950),
        Ship(3, 8, 61, 2800),
        Ship(4, 8, 12, 4000),   // too few battles at 20+
        Ship(5, 9, 80, 3500),
        Ship(6, 10, 25, 1500),
        Ship(7, 6, 100, 2000),
        Ship(8, 8, 50, null),   // no expected values → no PR
        Ship(9, 0, 70, 9999),   // not in the catalogue → tier unknown
        Ship(10, 11, 30, 2500),
    ];

    [Fact]
    public void Tier_VIII_with_20_battles_shows_only_tier_VIII_ships_ranked_by_PR()
    {
        var r = ShipRanking.Rank(Ships, new HashSet<int> { 8 }, 20);
        Assert.Equal([1L, 2L, 3L], r.Top.Select(s => s.Perf.ShipId));
        Assert.All(r.Top, s => Assert.Equal(8, s.Ship.Tier));
        Assert.Equal(3, r.Matching);
        Assert.Equal(1, r.WithoutPr);
    }

    [Fact]
    public void All_tiers_rank_every_known_ship_and_never_the_unknown_one()
    {
        var r = ShipRanking.Rank(Ships, new HashSet<int>(), 20);
        Assert.Equal([5L, 1L, 2L, 3L, 10L, 7L, 6L], r.Top.Select(s => s.Perf.ShipId));
        Assert.DoesNotContain(r.Top, s => s.Perf.ShipId == 9);
        Assert.Equal(1, r.UnknownShips);
    }

    [Fact]
    public void Several_tiers_and_minimum_battles_work_together()
    {
        var r = ShipRanking.Rank(Ships, new HashSet<int> { 9, 10 }, 1);
        Assert.Equal([5L, 6L], r.Top.Select(s => s.Perf.ShipId));
        r = ShipRanking.Rank(Ships, new HashSet<int> { 8 }, 1);
        Assert.Equal(4L, r.Top[0].Perf.ShipId); // 12 battles counts at 1+
        r = ShipRanking.Rank(Ships, new HashSet<int> { 6, 7, 8 }, 50);
        Assert.Equal([3L, 7L], r.Top.Select(s => s.Perf.ShipId));
    }

    [Fact]
    public void Empty_when_nothing_matches_and_top_is_limited()
    {
        Assert.Empty(ShipRanking.Rank(Ships, new HashSet<int> { 3 }, 20).Top);
        Assert.Empty(ShipRanking.Rank(Ships, new HashSet<int>(), 101).Top);
        var r = ShipRanking.Rank(Ships, new HashSet<int>(), 1, take: 2);
        Assert.Equal(2, r.Top.Count);
        Assert.Equal(8, r.Matching);
    }

    [Theory]
    [InlineData(new int[0], "All tiers")]
    [InlineData(new[] { 8 }, "Tier VIII")]
    [InlineData(new[] { 9, 10 }, "Tiers IX, X")]
    [InlineData(new[] { 8, 6, 7 }, "Tiers VI–VIII")]
    [InlineData(new[] { 6, 7, 8, 10, 11 }, "Tiers VI–VIII, X, XI")]
    [InlineData(new[] { 1, 11 }, "Tiers I, XI")]
    public void Tier_label_joins_runs(int[] tiers, string expected) => Assert.Equal(expected, ShipRanking.TierLabel(tiers));
}
