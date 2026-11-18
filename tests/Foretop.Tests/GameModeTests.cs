using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Stats;

namespace Foretop.Tests;

public class GameModeTests
{
    private static StatsBlockDto Block(int battles, int wins, long damage = 0, long xp = 0, int survived = 0) =>
        new() { Battles = battles, Wins = wins, Losses = battles - wins, DamageDealt = damage, Xp = xp, SurvivedBattles = survived };

    [Fact]
    public void Operations_add_up_solo_division_and_hard_and_have_no_damage()
    {
        var s = new AccountStatisticsDto
        {
            OperSolo = Block(2, 2, xp: 2500, survived: 1),
            OperDiv = Block(3, 1, xp: 3000, survived: 2),
            OperDivHard = Block(0, 0),
        };
        var t = GameModes.AccountTotals(s, GameMode.Operations)!;
        Assert.Equal(5, t.Battles);
        Assert.Equal(3, t.Wins);
        Assert.Equal(60.0, t.WinRate);
        Assert.Equal(1100.0, t.AvgXp);
        Assert.False(t.CombatKnown);
        Assert.Null(t.AvgDamage); // not reported by the API: "—", never 0
        Assert.Null(t.AvgFrags);
        Assert.Null(t.KillDeath);
    }

    [Fact]
    public void Ranked_and_coop_read_their_own_blocks()
    {
        var s = new AccountStatisticsDto { Pvp = Block(100, 50, 5_000_000), RankSolo = Block(10, 6, 700_000), Pve = Block(4, 4, 200_000) };
        Assert.Equal(10, GameModes.AccountTotals(s, GameMode.Ranked)!.Battles);
        Assert.Equal(70_000.0, GameModes.AccountTotals(s, GameMode.Ranked)!.AvgDamage);
        Assert.Equal(4, GameModes.AccountTotals(s, GameMode.Coop)!.Battles);
        Assert.Equal(100, GameModes.AccountTotals(s, GameMode.Random)!.Battles);
        Assert.Null(GameModes.AccountTotals(new AccountStatisticsDto(), GameMode.Operations)); // nothing sent: unknown
    }

    [Fact]
    public void Project_shows_the_mode_and_drops_random_only_values()
    {
        var random = new StatTotals(100, 55, 45, 0, 5_000_000, 100_000, 80, 40, 10);
        var ranked = new StatTotals(10, 6, 4, 0, 700_000, 15_000, 9, 5, 2);
        var p = new PlayerSummary
        {
            Name = "A", Overall = random, CurrentShip = random, Pr = 1500, ShipPr = 1400, DamageRatio = 1.1,
            Recent7 = new RecentStats(random, DateTimeOffset.UtcNow.AddDays(-7)),
            Modes = new Dictionary<GameMode, ModeStats> { [GameMode.Ranked] = new(ranked, null, null) },
        };

        Assert.Same(p, GameModes.Project(p, GameMode.Random));
        var r = GameModes.Project(p, GameMode.Ranked);
        Assert.Equal(ranked, r.Overall);
        Assert.Null(r.CurrentShip);    // never sailed in ranked
        Assert.Null(r.Pr);             // PR is a Random-battle rating
        Assert.Null(r.ShipPr);
        Assert.Null(r.DamageRatio);
        Assert.Null(r.Recent7);        // Foretop's snapshots are Random only

        var coop = GameModes.Project(p, GameMode.Coop);
        Assert.Null(coop.Overall);     // not loaded: no numbers rather than Random ones
        var old = GameModes.Project(p with { Modes = null }, GameMode.Ranked);
        Assert.Null(old.Overall);
    }

    [Fact]
    public void Cached_responses_from_before_modes_are_fetched_again()
    {
        Assert.False(PlayerStatsService.HasModeData("""{"statistics":{"pvp":{"battles":1},"rank_solo":{"battles":0}}}"""));
        Assert.True(PlayerStatsService.HasModeData("""{"statistics":{"pvp":{"battles":1},"pve":{"battles":0}}}"""));
        Assert.True(PlayerStatsService.HasModeData("""[{"ship_id":1,"pvp":{"battles":1},"pve":null}]"""));
        Assert.True(PlayerStatsService.HasModeData("""{"account_id":1,"hidden_profile":true,"statistics":null}"""));
        Assert.True(PlayerStatsService.HasModeData(null));
    }

    [Fact]
    public void Battle_type_picks_the_mode()
    {
        Assert.Equal(GameMode.Ranked, GameModes.ForBattle(BattleType.Ranked));
        Assert.Equal(GameMode.Coop, GameModes.ForBattle(BattleType.Coop));
        Assert.Equal(GameMode.Operations, GameModes.ForBattle(BattleType.Operation));
        Assert.Equal(GameMode.Random, GameModes.ForBattle(BattleType.Random));
        Assert.Equal(GameMode.Random, GameModes.ForBattle(BattleType.Clan)); // no clan-battle statistics in the API
    }

    [Fact]
    public void Tier_sum_keeps_missing_damage_missing_and_old_saved_totals_read_as_known()
    {
        var ops = new StatTotals(2, 1, 1, 0, 0, 2000, 0, 1, 0, CombatKnown: false);
        var sum = TierFilter.Sum([ops, ops]);
        Assert.False(sum.CombatKnown);
        Assert.True(TierFilter.Sum([]).CombatKnown);

        // A battle saved before this change has no CombatKnown in its JSON: it was Random data, so known.
        var saved = """[{"Name":"A","Overall":{"Battles":10,"Wins":5,"Losses":5,"Draws":0,"Damage":500000,"Xp":10000,"Frags":8,"Survived":4,"PlanesKilled":0}}]""";
        var p = Assert.Single(BattleSummaryCodec.Deserialize(saved));
        Assert.Equal(50_000.0, p.Overall!.AvgDamage);
    }
}
