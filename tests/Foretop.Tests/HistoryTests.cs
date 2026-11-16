using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Stats;
using Microsoft.Data.Sqlite;

namespace Foretop.Tests;

public class HistoryTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db");

    private const string Arena = """
        {"matchGroup":"pvp","dateTime":"14.11.2026 19:30:00","vehicles":[
          {"shipId":1,"relation":0,"id":1,"name":"Me"},
          {"shipId":2,"relation":1,"id":2,"name":"Buddy"},
          {"shipId":3,"relation":2,"id":3,"name":"Rival_X"}]}
        """;

    [Theory]
    [InlineData("SELECT id FROM battles b ORDER BY COALESCE(b.started_at, b.detected_at) DESC LIMIT 50", "ix_battles_when")]
    [InlineData("SELECT battle_key FROM battle_results WHERE status = 'Pending' ORDER BY started_at", "ix_results_status")]
    [InlineData("SELECT battle_key FROM battle_results WHERE region = 'Eu' AND account_id = 1 ORDER BY started_at DESC", "ix_results_account")]
    public void Hot_queries_use_an_index(string sql, string index)
    {
        var db = new Database(NewPath());
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var rd = cmd.ExecuteReader();
        var plan = new List<string>();
        while (rd.Read()) plan.Add(rd.GetString(3));
        Assert.Contains(plan, line => line.Contains(index));
        Assert.DoesNotContain(plan, line => line.Contains("USE TEMP B-TREE FOR ORDER BY"));
    }

    [Fact]
    public void Finds_battles_by_player_name_case_insensitively()
    {
        var store = new BattleHistoryStore(new Database(NewPath()));
        store.Upsert("a", Region.Eu, new DateTime(2026, 11, 13, 20, 0, 0), "pvp", "Map A", "Me", Arena, null, "file");
        store.Upsert("b", Region.Eu, new DateTime(2026, 11, 14, 20, 0, 0), "pvp", "Map B", "Me",
            Arena.Replace("Rival_X", "Someone_Else"), null);

        var rival = store.FindWithPlayer("rival_x");
        Assert.Single(rival);
        Assert.Equal("a", rival[0].BattleKey);
        Assert.Equal("file", rival[0].Source);

        var buddy = store.FindWithPlayer("BUDDY");
        Assert.Equal(["b", "a"], buddy.Select(b => b.BattleKey)); // newest first
        Assert.Empty(store.FindWithPlayer("Rival")); // exact names only
        Assert.Equal(2, store.Count());
        Assert.Equal("Map B", store.Get(buddy[0].Id)!.Map);
    }

    [Fact]
    public void Live_source_is_not_downgraded_by_a_later_replay_open()
    {
        var store = new BattleHistoryStore(new Database(NewPath()));
        store.Upsert("k", Region.Eu, null, "pvp", null, null, Arena, null, "live");
        store.Upsert("k", Region.Eu, null, "pvp", null, null, Arena, null, "file");
        Assert.Equal("live", store.List().Single().Source);
    }

    [Fact]
    public void Version_1_database_is_migrated()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = new SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE api_cache (region TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL, json TEXT,
                  fetched_at INTEGER NOT NULL, PRIMARY KEY (region, kind, key));
                CREATE TABLE battles (id INTEGER PRIMARY KEY AUTOINCREMENT, battle_key TEXT NOT NULL UNIQUE, region TEXT NOT NULL,
                  started_at INTEGER, detected_at INTEGER NOT NULL, match_group TEXT NOT NULL, map TEXT, player_name TEXT,
                  arena_json TEXT NOT NULL, summary_json TEXT);
                INSERT INTO battles(battle_key, region, detected_at, match_group, arena_json) VALUES ('old', 'Eu', 1, 'pvp', '{}');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new BattleHistoryStore(new Database(path));
        var old = store.List().Single();
        Assert.Equal("live", old.Source);
    }

    [Fact]
    public void Summary_codec_round_trips_and_drops_ship_lists()
    {
        var p = new PlayerSummary
        {
            Name = "Buddy", Relation = 1, ShipId = 42, AccountId = 7, ClanTag = "ABC", State = LoadState.Ready,
            Overall = new StatTotals(100, 55, 44, 1, 5_000_000, 120_000, 80, 30, 5), Pr = 1234.5,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_500_000_000),
            Recent7 = new RecentStats(new StatTotals(10, 6, 4, 0, 1, 1, 1, 1, 1), DateTimeOffset.FromUnixTimeSeconds(1_790_000_000)),
            Ships = [new ShipPerformance(42, new StatTotals(1, 1, 0, 0, 1, 1, 1, 1, 1), null, null)],
        };
        var json = BattleSummaryCodec.Serialize([p]);
        var back = Assert.Single(BattleSummaryCodec.Deserialize(json));
        Assert.Equal(p with { Ships = null }, back);
        Assert.Empty(BattleSummaryCodec.Deserialize("not json"));
        Assert.Empty(BattleSummaryCodec.Deserialize(null));
    }

    [Fact]
    public void Team_summary_extras()
    {
        var now = DateTimeOffset.UtcNow;
        var players = new List<PlayerSummary>
        {
            new() { Name = "A", Overall = new StatTotals(100, 50, 50, 0, 4_000_000, 1, 100, 40, 0), CreatedAt = now.AddYears(-4), DamageRatio = 1.2 },
            new() { Name = "B", Overall = new StatTotals(300, 180, 120, 0, 9_000_000, 1, 300, 60, 0), CreatedAt = now.AddYears(-2), DamageRatio = 0.8 },
            new() { Name = "Hidden", Hidden = true, CreatedAt = now.AddYears(-10) },
            new() { Name = ":Bot:", IsBot = true },
        };
        var t = TeamSummary.From(players);
        Assert.Equal(2, t.WithStats);
        Assert.Equal(55.0, t.AvgWinRate!.Value, 6);      // (50 + 60) / 2
        Assert.Equal(1.0, t.AvgFrags!.Value, 6);
        Assert.Equal(30.0, t.AvgSurvival!.Value, 6);     // (40 + 20) / 2
        Assert.Equal(1.0, t.AvgDamageRatio!.Value, 6);
        Assert.InRange(t.AvgAccountAgeYears!.Value, 5.2, 5.4); // (4 + 2 + 10) / 3, bots excluded
        Assert.Equal(1, t.Hidden);
        Assert.Equal(1, t.Bots);
    }
}
