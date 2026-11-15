using System.Diagnostics;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Game;
using Foretop.Core.Stats;
using Xunit.Abstractions;

namespace Foretop.Tests;

public class PerformanceTests(ITestOutputHelper output)
{
    private static (string Arena, string Summary) MakeBattle(int n)
    {
        var vehicles = string.Join(",", Enumerable.Range(0, 24).Select(i =>
            $$"""{"shipId":{{4000000000L + i}},"relation":{{(i == 0 ? 0 : i < 12 ? 1 : 2)}},"id":{{i + 1}},"name":"Player_{{(n * 7 + i) % 400}}"}"""));
        var arena = $$"""{"matchGroup":"pvp","mapDisplayName":"19_OC_prey","dateTime":"{{new DateTime(2026, 1, 1).AddHours(n):dd.MM.yyyy HH:mm:ss}}","playersPerTeam":12,"vehicles":[{{vehicles}}]}""";
        var players = Enumerable.Range(0, 24).Select(i => new PlayerSummary
        {
            Name = $"Player_{(n * 7 + i) % 400}", Relation = i == 0 ? 0 : i < 12 ? 1 : 2, ShipId = 4000000000L + i,
            AccountId = 500000000 + i, State = LoadState.Ready, ClanTag = "TAG",
            Overall = new StatTotals(5000 + i, 2600, 2380, 20, 250_000_000, 6_000_000, 4200, 1700, 900),
            CurrentShip = new StatTotals(200, 110, 89, 1, 10_000_000, 240_000, 170, 70, 10),
            Pr = 1200 + i * 10, ShipPr = 1300, DamageRatio = 1.05, CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_500_000_000),
            LastBattleAt = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), StatsFetchedAt = DateTimeOffset.UtcNow,
        });
        return (arena, BattleSummaryCodec.Serialize(players));
    }

    [Fact]
    public void Thousand_recorded_battles_load_quickly()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db"));
        var store = new BattleHistoryStore(db);
        for (var i = 0; i < 1000; i++)
        {
            var (arena, summary) = MakeBattle(i);
            store.Upsert("k" + i, Region.Eu, new DateTime(2026, 1, 1).AddHours(i), "pvp", "OC prey", "Player_0", arena, summary);
        }

        var sw = Stopwatch.StartNew();
        var records = store.List(1000);
        var listMs = sw.ElapsedMilliseconds;
        var rows = 0;
        foreach (var r in records)
        {
            ArenaInfoParser.TryParse(r.ArenaJson, out _, out _);
            var players = BattleSummaryCodec.Deserialize(r.SummaryJson);
            TeamSummary.From(players.Where(p => p.IsFriendly).ToList());
            TeamSummary.From(players.Where(p => !p.IsFriendly).ToList());
            rows++;
        }
        var totalMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var found = store.FindWithPlayer("Player_123");
        var findMs = sw.ElapsedMilliseconds;

        output.WriteLine($"1000 battles: query {listMs} ms, query+parse {totalMs} ms; find player {findMs} ms ({found.Count} hits); db {new FileInfo(db.Path).Length / 1024} KB");
        Assert.Equal(1000, rows);
        Assert.True(totalMs < 5000, $"took {totalMs} ms");
    }
}
