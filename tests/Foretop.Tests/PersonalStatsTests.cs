using System.Text.Json;
using Foretop.Core.Api;
using Foretop.Core.Personal;
using Foretop.Core.Replays;
using Foretop.Core.Stats;

namespace Foretop.Tests;

public class PersonalStatsTests
{
    private static int _n;

    private static PersonalBattle B(char result, long damage, int kills, bool survived, int tier = 8, string cls = "Battleship",
        ShipCombatTotals? delta = null, int? baseXp = null, double[]? expected = null, long ship = 100, int daysAgo = 0, string mode = "pvp")
    {
        var t = new StatTotals(1, result == 'W' ? 1 : 0, result == 'L' ? 1 : 0, result == 'D' ? 1 : 0, damage, 2000, kills, survived ? 1 : 0, 0);
        var x = new PersonalExtras
        {
            ShipDelta = delta, Replay = baseXp is null ? null : new ReplayBattleFacts { BaseXp = baseXp }, RatingExpected = expected,
        };
        return new PersonalBattle($"k{_n++}", DateTimeOffset.UtcNow.AddDays(-daysAgo), mode, "pvp", "Map", ship, "Ship", tier, cls, t, x);
    }

    private static ShipCombatTotals D(int mbHits, int mbShots, long spotting = 0) => new(1, spotting, 0, 0, 0, 0, mbHits, mbShots, 0, 0, 0, 0);

    [Fact]
    public void Only_exactly_counted_battles_are_personal_battles()
    {
        var ok = new BattleResult("a", Region.Eu, 1, "pvp", 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ResultStatus.Ok, null,
            new StatTotals(1, 1, 0, 0, 100, 10, 1, 1, 0), DateTimeOffset.UtcNow);
        Assert.NotNull(PersonalBattle.From(ok));
        Assert.Null(PersonalBattle.From(ok with { Status = ResultStatus.Combined, Result = ok.Result! with { Battles = 2 } }));
        Assert.Null(PersonalBattle.From(ok with { Status = ResultStatus.Pending, Result = null }));
    }

    [Fact]
    public void Period_values_are_totals_divided_by_battles()
    {
        var s = PeriodStats.Of([B('W', 100_000, 2, true), B('L', 40_000, 0, false), B('D', 70_000, 1, false), B('W', 10_000, 0, true)]);
        Assert.Equal(4, s.Battles);
        Assert.Equal((2, 1, 1), (s.Wins, s.Losses, s.Draws));
        Assert.Equal(50.0, s.WinRate);
        Assert.Equal(55_000, s.AvgDamage);
        Assert.Equal(0.75, s.AvgKills);
        Assert.Equal(0.5, s.AvgDeaths);
        Assert.Equal(1.5, s.KillDeath);
        Assert.Equal(50.0, s.SurvivalRate);
    }

    [Fact]
    public void Missing_values_are_not_zero()
    {
        var s = PeriodStats.Of([B('W', 1, 0, true, delta: D(0, 0, spotting: 0)), B('L', 1, 0, true)]);
        Assert.Equal(new Metric(0, 1), s.Spotting);       // one battle with 0 spotting damage
        Assert.Equal(Metric.None, s.BaseXp);              // no replay facts at all
        Assert.Null(s.MbAccuracy.Value);                  // no shots: no accuracy, not 0 %
        Assert.Null(PeriodStats.Of([B('W', 1, 3, true)]).KillDeath); // no deaths: "—"
        Assert.Null(PeriodStats.Of([]).WinRate);
    }

    [Fact]
    public void Accuracy_is_weighted_by_shots()
    {
        // 10/10 and 0/90: weighted 10 %, not the mean of 100 % and 0 %.
        var s = PeriodStats.Of([B('W', 1, 0, true, delta: D(10, 10)), B('W', 1, 0, true, delta: D(0, 90))]);
        Assert.Equal(10.0, s.MbAccuracy.Value);
        Assert.Equal(2, s.MbAccuracy.Count);
    }

    [Fact]
    public void Period_pr_uses_the_stored_expected_values_and_matches_the_battle_rating()
    {
        double[] e = [50_000, 0.8, 50];
        var one = B('W', 75_000, 1, true, expected: e);
        var expected = new Dictionary<long, ExpectedValues> { [100] = new(50_000, 0.8, 50) };
        Assert.Equal(PersonalRating.Compute([(100L, one.Totals)], expected), PeriodStats.Of([one]).Pr);

        var two = B('L', 25_000, 0, false, expected: e);
        var both = PeriodStats.Of([one, two]);
        var summed = new StatTotals(2, 1, 1, 0, 100_000, 4000, 1, 1, 0);
        Assert.Equal(PersonalRating.Compute([(100L, summed)], expected)!.Value, both.Pr!.Value, 6);
        Assert.Equal(1, PeriodStats.Of([one, B('W', 1, 0, true)]).PrBattles); // a battle without stored values is left out
    }

    [Fact]
    public void Query_filters_then_takes_the_newest()
    {
        var list = new[]
        {
            B('W', 1, 0, true, tier: 10, daysAgo: 0), B('W', 1, 0, true, tier: 5, daysAgo: 1), B('L', 1, 0, true, tier: 8, daysAgo: 2),
            B('L', 1, 0, true, tier: 6, daysAgo: 3, mode: "rank_solo"),
        };
        Assert.Equal(2, new PersonalQuery { Last = 2 }.Apply(list).Count);
        Assert.Equal([list[0], list[2]], new PersonalQuery { Last = 2, MinTier = 6, Mode = "pvp" }.Apply(list));
        Assert.Equal([list[1]], new PersonalQuery { From = DateTimeOffset.UtcNow.AddDays(-1.5), To = DateTimeOffset.UtcNow.AddDays(-0.5) }.Apply(list));
    }

    [Fact]
    public void Series_per_battle_rolling_and_cumulative()
    {
        var b = new[] { B('W', 10, 0, true), B('W', 20, 0, true), B('W', 30, 0, true), B('W', 40, 0, true) };
        Assert.Equal([10.0, 20, 30, 40], PersonalSeries.Build(b, x => x.Damage, SeriesMode.PerBattle).Select(p => p.Value));
        Assert.Equal([10.0, 15, 25, 35], PersonalSeries.Build(b, x => x.Damage, SeriesMode.Rolling, 2).Select(p => p.Value));
        Assert.Equal([10.0, 15, 20, 25], PersonalSeries.Build(b, x => x.Damage, SeriesMode.Cumulative).Select(p => p.Value));

        // K/D cumulative: Σkills ÷ Σdeaths; battles before the first death have no value.
        var kd = new[] { B('W', 1, 2, true), B('L', 1, 1, false), B('L', 1, 0, false) };
        Assert.Equal([3.0, 1.5], PersonalSeries.BuildRatio(kd, x => (x.Kills, x.Deaths), SeriesMode.Cumulative).Select(p => p.Value));
    }

    [Fact]
    public void Ship_combat_totals_parse_the_api_block()
    {
        // Shape as returned by ships/stats for the user's Jean Bart B (values shortened).
        using var doc = JsonDocument.Parse("""
            {"pvp":{"main_battery":{"hits":864,"shots":2296},"ships_spotted":12,"torpedoes":{"hits":0,"shots":0},"art_agro":36253350,
             "torpedo_agro":3600906,"battles":29,"second_battery":{"hits":1628,"shots":8069},"planes_killed":105,"damage_scouting":426535},
             "ship_id":3666818896}
            """);
        var t = ShipCombatTotals.From(doc.RootElement, "pvp")!;
        Assert.Equal((29, 426_535L, 39_854_256L, 864, 2296, 12), (t.Battles, t.Spotting, t.Potential, t.MbHits, t.MbShots, t.ShipsSpotted));
        Assert.Null(ShipCombatTotals.From(doc.RootElement, "rank_solo"));
        Assert.Contains("pvp.main_battery.hits", ShipCombatTotals.Fields("pvp"));
    }

    // ------------------------------------------------------------------ replay facts

    private static string Results(long self, long ship, long dmg, long baseXp, params (long Acc, long Dmg)[] others)
    {
        string Row(long acc, long s, long d, long xp)
        {
            var row = new object[550];
            Array.Fill(row, 0);
            row[0] = acc; row[7] = s; row[416] = xp; row[438] = d;
            return JsonSerializer.Serialize(row);
        }
        var rows = new List<string> { $"\"{self}\": {Row(self, ship, dmg, baseXp)}" };
        rows.AddRange(others.Select(o => $"\"{o.Acc}\": {Row(o.Acc, 1, o.Dmg, 900)}"));
        return "{\"playersPublicInfo\": {" + string.Join(",", rows) + "}}";
    }

    private static readonly ReplayPlayer[] Players =
    [
        new("Me", 7, 0, 1001, 11, null), new("Mate", 8, 1, 1002, 22, null), new("Mate2", 8, 1, 1003, 33, null), new("Foe", 9, 2, 2001, 44, null),
    ];

    private static readonly ValuePoint[] Clock = [new(0, 0), new(1, 30), new(20, 11), new(31, 1200), new(500, 731)];

    [Fact]
    public void Replay_facts_with_matching_anchors()
    {
        var json = Results(11, 7, 120_348, 1498, (22, 200_000), (33, 50_000), (44, 999_999));
        var deaths = new ReplayDeath[] { new(100, 2001, 1001, 1), new(326, 1001, 2001, 1) };
        var o = ReplayFacts.Compute(json, 11, 7, 1001, 0, Players, deaths, Clock, 822,
            new StatTotals(1, 0, 1, 0, 120_348, 2472, 1, 0, 0));
        Assert.Null(o.Note);
        Assert.Equal(1498, o.Facts.BaseXp);
        Assert.Equal(1, o.Facts.Kills);
        Assert.False(o.Facts.Survived);
        Assert.Equal(326 - 31, o.Facts.LifeTime!.Value, 3);
        Assert.Equal(822 - 31, o.Facts.Duration!.Value, 3);
        Assert.Equal((2, 3), (o.Facts.TeamPosition, o.Facts.TeamSize)); // enemy damage not counted
    }

    [Fact]
    public void Replay_positional_values_are_dropped_when_anchors_fail()
    {
        var json = Results(11, 7, 120_348, 1498, (22, 1));
        var wrongDamage = ReplayFacts.Compute(json, 11, 7, 1001, 0, Players, [], Clock, 822, new StatTotals(1, 0, 1, 0, 99_999, 2472, 0, 0, 0));
        Assert.NotNull(wrongDamage.Note);
        Assert.Null(wrongDamage.Facts.BaseXp);
        Assert.Null(wrongDamage.Facts.TeamPosition);
        Assert.True(wrongDamage.Facts.Survived); // decoded messages still count

        var wrongShip = ReplayFacts.Compute(json, 11, 8, 1001, 0, Players, [], Clock, 822, null);
        Assert.Null(wrongShip.Facts.BaseXp);
        Assert.Contains("layout", wrongShip.Note);

        var noResults = ReplayFacts.Compute(null, 11, 7, 1001, 0, Players, [], Clock, 822, null);
        Assert.Null(noResults.Facts.BaseXp);
        Assert.Equal(822 - 31, noResults.Facts.LifeTime!.Value, 3);
    }

    [Fact]
    public void Base_xp_above_the_api_xp_is_rejected()
    {
        var json = Results(11, 7, 1000, 5000);
        var o = ReplayFacts.Compute(json, 11, 7, 1001, 0, Players, [], Clock, 822, new StatTotals(1, 1, 0, 0, 1000, 2000, 0, 1, 0));
        Assert.Null(o.Facts.BaseXp);
    }

    [Fact]
    public void Extras_round_trip_through_json()
    {
        var x = new PersonalExtras { Map = "Tears of the Desert", ShipTier = 9, ShipDelta = D(1, 2, 3), Rating = 1234.5, RatingExpected = [1, 2, 3] };
        var back = PersonalJson.Read(PersonalJson.Write(x))!;
        Assert.Equal(x.ShipDelta, back.ShipDelta);
        Assert.Equal((x.Map, x.ShipTier, x.Rating), (back.Map, back.ShipTier, back.Rating));
        Assert.Equal(x.RatingExpected, back.RatingExpected);
    }
}
