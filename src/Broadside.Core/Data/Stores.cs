using System.Globalization;
using Broadside.Core.Api;
using Broadside.Core.Stats;
using Microsoft.Data.Sqlite;

namespace Broadside.Core.Data;

public sealed record CachedJson(string? Json, DateTimeOffset FetchedAt)
{
    public TimeSpan Age => DateTimeOffset.UtcNow - FetchedAt;
}

/// <summary>
/// Raw API JSON per (region, kind, key), Brotli-compressed (ship lists shrink about 6×). Callers decide
/// freshness. A row with no data records "the API had nothing for this key".
/// </summary>
public sealed class ApiCache(Database db)
{
    public CachedJson? Get(Region region, string kind, string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, zjson, fetched_at FROM api_cache WHERE region=$r AND kind=$k AND key=$key";
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        var json = !rd.IsDBNull(1) ? Compression.Unpack((byte[])rd.GetValue(1)) : rd.IsDBNull(0) ? null : rd.GetString(0);
        return new CachedJson(json, DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(2)));
    }

    public void Put(Region region, string kind, string key, string? json)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_cache(region, kind, key, json, zjson, fetched_at) VALUES ($r, $k, $key, NULL, $z, $t)
            ON CONFLICT(region, kind, key) DO UPDATE SET json=NULL, zjson=excluded.zjson, fetched_at=excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$z", json is null ? DBNull.Value : Compression.Pack(json));
        cmd.Parameters.AddWithValue("$t", Database.Now());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes responses not refreshed for <paramref name="olderThan"/>. These are regenerable cache rows
    /// (refetched on demand), not user data. Returns the number removed.
    /// </summary>
    public int Prune(TimeSpan olderThan)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM api_cache WHERE ABS(fetched_at) < $cut";
        cmd.Parameters.AddWithValue("$cut", DateTimeOffset.UtcNow.Subtract(olderThan).ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    public long SizeBytes()
    {
        using var c = db.Open();
        return Convert.ToInt64(Database.Scalar(c, "SELECT COALESCE(SUM(LENGTH(zjson)) + SUM(LENGTH(json)), 0) FROM api_cache"),
            CultureInfo.InvariantCulture);
    }

    public int Count()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM api_cache"), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Marks every cached response stale: refetched next time, but still there as an offline fallback.
    /// The time is negated rather than zeroed so <see cref="Prune"/> still knows its real age.
    /// </summary>
    public void Invalidate()
    {
        using var c = db.Open();
        Database.Exec(c, "UPDATE api_cache SET fetched_at = -ABS(fetched_at) WHERE fetched_at > 0");
    }
}

public sealed record CachedAccount(long? AccountId, string Nickname, DateTimeOffset ResolvedAt);

/// <summary>Nickname → account ID. A null ID records "no such player on this region".</summary>
public sealed class AccountStore(Database db)
{
    public Dictionary<string, CachedAccount> Get(Region region, IEnumerable<string> names)
    {
        var result = new Dictionary<string, CachedAccount>(StringComparer.OrdinalIgnoreCase);
        using var c = db.Open();
        foreach (var name in names)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT account_id, nickname, resolved_at FROM accounts WHERE region=$r AND name_key=$n";
            cmd.Parameters.AddWithValue("$r", region.ToString());
            cmd.Parameters.AddWithValue("$n", name.ToLowerInvariant());
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
                result[name] = new CachedAccount(rd.IsDBNull(0) ? null : rd.GetInt64(0), rd.GetString(1),
                    DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(2)));
        }
        return result;
    }

    public void Put(Region region, string name, long? accountId, string nickname)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts(region, name_key, account_id, nickname, resolved_at) VALUES ($r, $n, $id, $nick, $t)
            ON CONFLICT(region, name_key) DO UPDATE SET account_id=excluded.account_id, nickname=excluded.nickname, resolved_at=excluded.resolved_at
            """;
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$n", name.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$id", (object?)accountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$t", Database.Now());
        cmd.ExecuteNonQuery();
    }
}

public sealed record PlayerSnapshot(DateTimeOffset TakenAt, StatTotals Totals);

/// <summary>
/// A copy of a player's totals each time they change. The difference between two snapshots
/// is real recent activity; the API itself no longer offers any.
/// </summary>
public sealed class SnapshotStore(Database db)
{
    public void AddIfChanged(Region region, long accountId, StatTotals t)
    {
        using var c = db.Open();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT battles FROM player_snapshots WHERE region=$r AND account_id=$a ORDER BY taken_at DESC LIMIT 1";
            q.Parameters.AddWithValue("$r", region.ToString());
            q.Parameters.AddWithValue("$a", accountId);
            var last = q.ExecuteScalar();
            if (last is long lastBattles && lastBattles == t.Battles) return;
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO player_snapshots(region, account_id, taken_at, battles, wins, damage, xp, frags, survived, planes)
            VALUES ($r, $a, $t, $b, $w, $d, $x, $f, $s, $p)
            """;
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", Database.Now());
        cmd.Parameters.AddWithValue("$b", t.Battles);
        cmd.Parameters.AddWithValue("$w", t.Wins);
        cmd.Parameters.AddWithValue("$d", t.Damage);
        cmd.Parameters.AddWithValue("$x", t.Xp);
        cmd.Parameters.AddWithValue("$f", t.Frags);
        cmd.Parameters.AddWithValue("$s", t.Survived);
        cmd.Parameters.AddWithValue("$p", t.PlanesKilled);
        cmd.ExecuteNonQuery();
    }

    public List<PlayerSnapshot> GetAll(Region region, long accountId)
    {
        var list = new List<PlayerSnapshot>();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT taken_at, battles, wins, damage, xp, frags, survived, planes
            FROM player_snapshots WHERE region=$r AND account_id=$a ORDER BY taken_at
            """;
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$a", accountId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var b = rd.GetInt32(1);
            var w = rd.GetInt32(2);
            list.Add(new PlayerSnapshot(DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(0)),
                new StatTotals(b, w, 0, 0, rd.GetInt64(3), rd.GetInt64(4), rd.GetInt32(5), rd.GetInt32(6), rd.GetInt32(7))));
        }
        return list;
    }
}

public sealed record BattleRecord(
    long Id, string BattleKey, Region Region, DateTimeOffset? StartedAt, DateTimeOffset DetectedAt,
    string MatchGroup, string? Map, string? PlayerName, string ArenaJson, string? SummaryJson, string Source)
{
    public DateTimeOffset When => StartedAt ?? DetectedAt;
}

/// <summary>Detected battles: the arena roster plus a JSON summary of everyone's stats at the time.</summary>
public sealed class BattleHistoryStore(Database db)
{
    private const string Columns =
        "b.id, b.battle_key, b.region, b.started_at, b.detected_at, b.match_group, b.map, b.player_name, b.arena_json, b.summary_json, b.source, b.summary_z";

    /// <param name="source">"live" for detected battles, "file" for opened replays.</param>
    public void Upsert(string battleKey, Region region, DateTime? startedAt, string matchGroup, string? map,
        string? playerName, string arenaJson, string? summaryJson, string source = "live")
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO battles(battle_key, region, started_at, detected_at, match_group, map, player_name, arena_json, summary_json, summary_z, source)
            VALUES ($k, $r, $s, $d, $g, $m, $p, $a, NULL, $z, $src)
            ON CONFLICT(battle_key) DO UPDATE SET
                summary_z = COALESCE(excluded.summary_z, battles.summary_z),
                summary_json = CASE WHEN excluded.summary_z IS NULL THEN battles.summary_json ELSE NULL END,
                source = CASE WHEN battles.source = 'live' THEN 'live' ELSE excluded.source END
            """;
        cmd.Parameters.AddWithValue("$k", battleKey);
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$s", startedAt is null ? DBNull.Value : new DateTimeOffset(startedAt.Value).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$d", Database.Now());
        cmd.Parameters.AddWithValue("$g", matchGroup);
        cmd.Parameters.AddWithValue("$m", (object?)map ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)playerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", arenaJson);
        cmd.Parameters.AddWithValue("$z", summaryJson is null ? DBNull.Value : Compression.Pack(summaryJson));
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    public List<BattleRecord> List(int limit = 500)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM battles b ORDER BY COALESCE(b.started_at, b.detected_at) DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        return Read(cmd);
    }

    public BattleRecord? Get(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM battles b WHERE b.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Read(cmd).FirstOrDefault();
    }

    /// <summary>Recorded battles in which a nickname appears (either team), newest first.</summary>
    public List<BattleRecord> FindWithPlayer(string nickname, int limit = 100)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM battles b
            WHERE EXISTS (SELECT 1 FROM json_each(b.arena_json, '$.vehicles') v
                          WHERE json_extract(v.value, '$.name') = $n COLLATE NOCASE)
            ORDER BY COALESCE(b.started_at, b.detected_at) DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$n", nickname);
        cmd.Parameters.AddWithValue("$l", limit);
        return Read(cmd);
    }

    public int Count()
    {
        using var c = db.Open();
        return Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM battles"), CultureInfo.InvariantCulture);
    }

    /// <summary>Cheap index of (id, version) so callers can re-read only battles that are new or whose stats changed.</summary>
    public Dictionary<long, long> Index(int limit = 5000)
    {
        var map = new Dictionary<long, long>();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, COALESCE(LENGTH(summary_z), 0) * 7 + COALESCE(LENGTH(summary_json), 0) * 3 + LENGTH(source)
            FROM battles ORDER BY COALESCE(started_at, detected_at) DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$l", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) map[rd.GetInt64(0)] = rd.GetInt64(1);
        return map;
    }

    /// <summary>Several battles by id.</summary>
    public List<BattleRecord> Get(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return [];
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM battles b WHERE b.id IN ({string.Join(',', ids)})";
        return Read(cmd);
    }

    private static List<BattleRecord> Read(SqliteCommand cmd)
    {
        var list = new List<BattleRecord>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new BattleRecord(
                rd.GetInt64(0), rd.GetString(1), Enum.Parse<Region>(rd.GetString(2)),
                rd.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(3)),
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(4)),
                rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetString(8),
                !rd.IsDBNull(11) ? Compression.Unpack((byte[])rd.GetValue(11)) : rd.IsDBNull(9) ? null : rd.GetString(9),
                rd.GetString(10)));
        }
        return list;
    }
}

/// <summary>Small key/value table (game version, timestamps, expected values).</summary>
public sealed class KvStore(Database db)
{
    public (string Value, DateTimeOffset UpdatedAt)? Get(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value, updated_at FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(1))) : null;
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kv(key, value, updated_at) VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$t", Database.Now());
        cmd.ExecuteNonQuery();
    }
}
