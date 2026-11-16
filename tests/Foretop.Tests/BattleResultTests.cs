using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Stats;

namespace Foretop.Tests;

public class BattleResultTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    private static StatTotals Totals(int battles, int wins = 0, long damage = 0, int survived = 0) =>
        new(battles, wins, battles - wins, 0, damage, battles * 1000L, battles, survived, 0);

    private static BattleResult Open(string key, StatTotals baseline, int minutes, bool ended = true) =>
        new(key, Region.Eu, 42, ResultModes.Random, 7, T0.AddMinutes(minutes), ended ? T0.AddMinutes(minutes + 15) : null,
            ResultStatus.Pending, baseline, null, null);

    [Fact]
    public void One_more_battle_is_that_battles_result()
    {
        var before = Totals(100, wins: 55, damage: 5_000_000, survived: 40);
        var after = before with { Battles = 101, Wins = 56, Damage = 5_087_310, Xp = before.Xp + 1_650, Frags = 102, Survived = 41 };

        var changed = ResultMath.Apply([Open("a", before, 0)], after, T0.AddMinutes(20));

        var r = Assert.Single(changed);
        Assert.Equal(ResultStatus.Ok, r.Status);
        Assert.Equal(1, r.Result!.Battles);
        Assert.Equal(1, r.Result.Wins);
        Assert.Equal(87_310, r.Result.Damage);
        Assert.Equal(1_650, r.Result.Xp);
        Assert.Equal(2, r.Result.Frags);
        Assert.Equal(1, r.Result.Survived);
    }

    [Fact]
    public void Nothing_counted_yet_changes_nothing()
    {
        var before = Totals(100);
        Assert.Empty(ResultMath.Apply([Open("a", before, 0)], before, T0));
    }

    [Fact]
    public void A_battle_still_running_is_not_resolved()
    {
        Assert.Empty(ResultMath.Apply([Open("a", Totals(100), 0, ended: false)], Totals(101), T0));
    }

    [Fact]
    public void Next_battle_rebases_while_the_previous_one_is_counted()
    {
        // You left battle a early and started b before a was counted: both have the same baseline.
        var b0 = Totals(100);
        var open = new[] { Open("a", b0, 0), Open("b", b0, 5, ended: false) };

        var changed = ResultMath.Apply(open, Totals(101, damage: 40_000), T0.AddMinutes(18));

        Assert.Equal(2, changed.Count);
        Assert.Equal(ResultStatus.Ok, changed[0].Status);
        Assert.Equal("b", changed[1].BattleKey);
        Assert.Equal(ResultStatus.Pending, changed[1].Status);
        Assert.Equal(101, changed[1].Baseline!.Battles); // b's own result is measured from here
    }

    [Fact]
    public void Two_battles_counted_at_once_are_combined_not_split()
    {
        var b0 = Totals(100);
        var open = new[] { Open("a", b0, 0), Open("b", b0, 5) };

        var changed = ResultMath.Apply(open, Totals(102, wins: 1, damage: 130_000), T0.AddMinutes(30));

        Assert.Equal(ResultStatus.Combined, changed[0].Status);
        Assert.Equal(2, changed[0].Result!.Battles);
        Assert.Equal(ResultStatus.Merged, changed[1].Status);
        Assert.Null(changed[1].Result);
        // Totals over both rows stay exact.
        var sum = ResultTotals.Of(changed);
        Assert.Equal(2, sum.Battles);
        Assert.Equal(130_000, sum.Damage);
    }

    [Fact]
    public void Session_ends_at_a_gap_of_more_than_an_hour()
    {
        BattleResult At(int minutes) => Open("k" + minutes, Totals(1), minutes);
        var newestFirst = new[] { At(300), At(270), At(230), At(100), At(80) };

        var session = ResultMath.Session(newestFirst, TimeSpan.FromHours(1));

        Assert.Equal(3, session.Count); // 230 → 100 is more than an hour apart
    }

    [Fact]
    public void Totals_skip_rows_without_values_and_count_pending()
    {
        var ok = Open("a", Totals(1), 0) with { Status = ResultStatus.Ok, Result = Totals(1, wins: 1, damage: 50_000, survived: 1) };
        var pending = Open("b", Totals(1), 20);
        var none = Open("c", Totals(1), 40) with { Status = ResultStatus.Unavailable, Baseline = null };

        var t = ResultTotals.Of([ok, pending, none]);

        Assert.Equal(1, t.Battles);
        Assert.Equal(100.0, t.WinRate);
        Assert.Equal(50_000, t.AvgDamage);
        Assert.Equal(1, t.Pending);
    }

    [Theory]
    [InlineData("pvp", "pvp")]
    [InlineData("ranked", "rank_solo")]
    [InlineData("pve", "pve")]
    [InlineData("clan", null)]
    [InlineData("pve_premade", null)]
    public void Modes_with_api_totals(string matchGroup, string? mode) =>
        Assert.Equal(mode, ResultModes.FromMatchGroup(matchGroup));

    [Fact]
    public void Several_results_are_written_together()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db"));
        var store = new BattleResultStore(db);
        var a = Open("a", Totals(100), 0);
        var b = Open("b", Totals(100), 20);
        store.UpsertMany([a, b]);
        Assert.Equal(2, store.Open(Region.Eu, 42).Count);
        // A row that cannot be written (null key violates NOT NULL) rolls back the whole set: nothing half-applied.
        var bad = Open(null!, Totals(1), 40);
        Assert.ThrowsAny<Exception>(() => store.UpsertMany([a with { Status = ResultStatus.Ok, Result = Totals(1), ResolvedAt = T0 }, bad]));
        Assert.Equal(ResultStatus.Pending, store.Get("a")!.Status);
    }

    [Fact]
    public void Store_keeps_baseline_and_result()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db"));
        var store = new BattleResultStore(db);
        var r = Open("a", Totals(100, wins: 50), 0);
        store.Upsert(r);
        Assert.Single(store.Open(Region.Eu, 42));

        store.Upsert(r with { Status = ResultStatus.Ok, Result = Totals(1, wins: 1, damage: 77_000), ResolvedAt = T0.AddMinutes(16) });

        var back = store.Get("a")!;
        Assert.Equal(ResultStatus.Ok, back.Status);
        Assert.Equal(100, back.Baseline!.Battles);
        Assert.Equal(77_000, back.Result!.Damage);
        Assert.Empty(store.Open(Region.Eu, 42));
        Assert.Single(store.Since(T0.AddHours(-1)));
    }
}
