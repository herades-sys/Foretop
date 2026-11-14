using Broadside.Core.Data;
using Broadside.Core.Stats;

namespace Broadside.Tests;

public class StatsTests
{
    private static StatTotals T(int battles, int wins, long dmg, int frags) =>
        new(battles, wins, battles - wins, 0, dmg, battles * 1000L, frags, battles / 3, 0);

    [Fact]
    public void Derived_averages()
    {
        var t = new StatTotals(200, 110, 88, 2, 9_000_000, 260_000, 160, 70, 40);
        Assert.Equal(55.0, t.WinRate);
        Assert.Equal(45_000, t.AvgDamage);
        Assert.Equal(1_300, t.AvgXp);
        Assert.Equal(0.8, t.AvgFrags);
        Assert.Equal(35.0, t.SurvivalRate);
        Assert.Equal(0.2, t.AvgPlanes);
        Assert.Null(new StatTotals(0, 0, 0, 0, 0, 0, 0, 0, 0).WinRate);
    }

    [Fact]
    public void PR_matches_hand_calculation()
    {
        var expected = new Dictionary<long, ExpectedValues> { [1] = new(50_000, 1.0, 50.0) };
        // Exactly expected performance: rDmg = rFrags = rWins = 1 â†’ 700 + 300 + 150 = 1150.
        var pr = PersonalRating.Compute([(1L, T(100, 50, 5_000_000, 100))], expected);
        Assert.Equal(1150, pr!.Value, 6);

        // Damage Ã—1.3, frags Ã—1.2, wins 55% â†’ nDmg = 0.9/0.6 = 1.5, nFrags = 1.1/0.9, nWins = (1.1-0.7)/0.3.
        var pr2 = PersonalRating.Compute([(1L, T(100, 55, 6_500_000, 120))], expected)!.Value;
        var hand = 700 * 1.5 + 300 * (1.1 / 0.9) + 150 * (0.4 / 0.3);
        Assert.Equal(hand, pr2, 6);
    }

    [Fact]
    public void PR_ignores_ships_without_expected_values_and_floors_at_zero()
    {
        var expected = new Dictionary<long, ExpectedValues> { [1] = new(50_000, 1.0, 50.0) };
        var withUnknown = PersonalRating.Compute([(1L, T(100, 50, 5_000_000, 100)), (999L, T(500, 0, 0, 0))], expected);
        Assert.Equal(1150, withUnknown!.Value, 6);

        var awful = PersonalRating.Compute([(1L, T(100, 10, 100_000, 1))], expected);
        Assert.Equal(0, awful!.Value, 6);

        Assert.Null(PersonalRating.Compute([(999L, T(10, 5, 1, 1))], expected));
    }

    [Theory]
    [InlineData(null, PrBand.None)]
    [InlineData(500.0, PrBand.Bad)]
    [InlineData(1000.0, PrBand.BelowAverage)]
    [InlineData(1200.0, PrBand.Average)]
    [InlineData(1400.0, PrBand.Good)]
    [InlineData(1600.0, PrBand.VeryGood)]
    [InlineData(1900.0, PrBand.Great)]
    [InlineData(2200.0, PrBand.Unicum)]
    [InlineData(3000.0, PrBand.SuperUnicum)]
    public void PR_bands(double? pr, PrBand band) => Assert.Equal(band, PersonalRating.Band(pr));

    [Fact]
    public void Local_snapshots_give_recent_activity()
    {
        var now = DateTimeOffset.UtcNow;
        var snaps = new List<PlayerSnapshot>
        {
            new(now.AddDays(-40), new StatTotals(900, 450, 0, 0, 0, 0, 0, 0, 0)),
            new(now.AddDays(-10), new StatTotals(980, 490, 0, 0, 0, 0, 0, 0, 0)),
            new(now.AddDays(-2), new StatTotals(995, 500, 0, 0, 0, 0, 0, 0, 0)),
        };
        var overall = new StatTotals(1000, 503, 0, 0, 0, 0, 0, 0, 0);
        var (r7, r28) = PlayerStatsService.RecentFromSnapshots(snaps, overall);
        Assert.Equal(20, r7!.Totals.Battles);
        Assert.Equal(100, r28!.Totals.Battles);
        Assert.Equal(40, r28.Days); // base is the newest snapshot at least 28 days old
    }
}
