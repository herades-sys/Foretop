using System.Text.Json;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Microsoft.Data.Sqlite;

namespace Foretop.Core.Stats;

/// <summary>Which block of account/info statistics counts a battle type. Other battle types have no API totals.</summary>
public static class ResultModes
{
    public const string Random = "pvp";
    public const string Ranked = "rank_solo";
    public const string Coop = "pve";

    public static string? FromMatchGroup(string? matchGroup) => matchGroup?.ToLowerInvariant() switch
    {
        "pvp" => Random,
        "ranked" => Ranked,
        "pve" => Coop,
        _ => null,
    };

    public static StatTotals? Pick(AccountStatisticsDto? s, string mode) => mode switch
    {
        Random => StatTotals.From(s?.Pvp),
        Ranked => StatTotals.From(s?.RankSolo),
        Coop => StatTotals.From(s?.Pve),
        _ => null,
    };
}

public enum ResultStatus
{
    /// <summary>Waiting for the API to count the battle.</summary>
    Pending,
    /// <summary>Exactly this battle: the totals grew by one battle.</summary>
    Ok,
    /// <summary>The totals grew by several battles at once (you left battles early); this row carries all of them.</summary>
    Combined,
    /// <summary>Counted in an earlier row's combined result.</summary>
    Merged,
    /// <summary>No API totals for this battle type (operations, clan battles, events, training…).</summary>
    Unavailable,
    /// <summary>The API never counted it (gave up after a day).</summary>
    Expired,
}

public sealed record BattleResult(
    string BattleKey, Region Region, long AccountId, string Mode, long ShipId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, ResultStatus Status,
    StatTotals? Baseline, StatTotals? Result, DateTimeOffset? ResolvedAt, Personal.PersonalExtras? Extras = null)
{
    public bool IsOpen => Status == ResultStatus.Pending;
}

/// <summary>Totals over a set of results (only rows with values count).</summary>
public sealed record ResultTotals(int Battles, int Wins, int Losses, int Draws, long Damage, long Xp, int Frags, int Survived, int Pending)
{
    public double? WinRate => Battles > 0 ? 100.0 * Wins / Battles : null;
    public double? AvgDamage => Battles > 0 ? (double)Damage / Battles : null;
    public double? AvgXp => Battles > 0 ? (double)Xp / Battles : null;
    public double? AvgFrags => Battles > 0 ? (double)Frags / Battles : null;
    public double? SurvivalRate => Battles > 0 ? 100.0 * Survived / Battles : null;

    public static ResultTotals Of(IEnumerable<BattleResult> rows)
    {
        int b = 0, w = 0, l = 0, d = 0, f = 0, s = 0, p = 0;
        long dmg = 0, xp = 0;
        foreach (var r in rows)
        {
            if (r.Status == ResultStatus.Pending) p++;
            if (r.Result is not { } t) continue;
            b += t.Battles; w += t.Wins; l += t.Losses; d += t.Draws; dmg += t.Damage; xp += t.Xp; f += t.Frags; s += t.Survived;
        }
        return new ResultTotals(b, w, l, d, dmg, xp, f, s, p);
    }
}

/// <summary>Pure rules for turning account totals into per-battle results. Unit-tested.</summary>
public static class ResultMath
{
    /// <summary>
    /// Applies freshly fetched totals of one mode to that mode's open battles (oldest first). Returns the rows that
    /// changed. The oldest open battle whose baseline is behind the totals gets the difference. One battle more is an
    /// exact result; several at once (you left a battle early and it was counted later) go into that row as a combined
    /// result, and the following open battles are marked as merged into it, as many as the extra battles. Rows still open
    /// get the new totals as their baseline, since their own battle is not counted yet.
    /// </summary>
    public static List<BattleResult> Apply(IReadOnlyList<BattleResult> openOldestFirst, StatTotals totals, DateTimeOffset now)
    {
        var changed = new List<BattleResult>();
        var open = openOldestFirst.Where(r => r.IsOpen && r.Baseline is not null).ToList();
        if (open.Count == 0) return changed;

        var first = open[0];
        var delta = totals.Battles - first.Baseline!.Battles;
        if (delta <= 0) return changed; // nothing new counted (a negative delta would be an API glitch: ignore)

        // A battle that has not ended yet cannot be in the totals.
        if (first.EndedAt is null) return changed;

        var diff = totals.Minus(first.Baseline);
        var status = delta == 1 ? ResultStatus.Ok : ResultStatus.Combined;
        changed.Add(first with { Status = status, Result = diff, ResolvedAt = now });

        var i = 1;
        for (; i < open.Count && i < delta; i++)
        {
            if (open[i].EndedAt is null) break;
            changed.Add(open[i] with { Status = ResultStatus.Merged, Result = null, ResolvedAt = now });
        }
        for (; i < open.Count; i++)
            changed.Add(open[i] with { Baseline = totals });
        return changed;
    }

    /// <summary>
    /// The current session: the newest battles with no gap of more than <paramref name="gap"/> between one battle's
    /// start and the next. Rows newest first in, newest first out.
    /// </summary>
    public static List<BattleResult> Session(IReadOnlyList<BattleResult> newestFirst, TimeSpan gap)
    {
        var list = new List<BattleResult>();
        foreach (var r in newestFirst)
        {
            if (list.Count > 0 && list[^1].StartedAt - r.StartedAt > gap) break;
            list.Add(r);
        }
        return list;
    }
}

/// <summary>Own battle results (table battle_results).</summary>
public sealed class BattleResultStore(Database db)
{
    private static readonly JsonSerializerOptions Json = new();

    public void Upsert(BattleResult r)
    {
        using var c = db.Open();
        Write(c, null, r);
    }

    /// <summary>Several rows in one transaction: all of them are stored, or none (a crash mid-way leaves no half-applied result).</summary>
    public void UpsertMany(IReadOnlyCollection<BattleResult> rows)
    {
        if (rows.Count == 0) return;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var r in rows) Write(c, tx, r);
        tx.Commit();
    }

    private static void Write(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction? tx, BattleResult r)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO battle_results(battle_key, region, account_id, mode, ship_id, started_at, ended_at, status, baseline_json,
                                       battles, wins, losses, draws, damage, xp, frags, survived, planes, resolved_at, extra_json)
            VALUES ($k, $r, $a, $m, $s, $st, $en, $status, $base, $b, $w, $l, $d, $dmg, $xp, $f, $sv, $p, $res, $x)
            ON CONFLICT(battle_key) DO UPDATE SET
                ended_at = excluded.ended_at, status = excluded.status, baseline_json = excluded.baseline_json,
                battles = excluded.battles, wins = excluded.wins, losses = excluded.losses, draws = excluded.draws,
                damage = excluded.damage, xp = excluded.xp, frags = excluded.frags, survived = excluded.survived,
                planes = excluded.planes, resolved_at = excluded.resolved_at, extra_json = excluded.extra_json
            """;
        var t = r.Result;
        cmd.Parameters.AddWithValue("$k", r.BattleKey);
        cmd.Parameters.AddWithValue("$r", r.Region.ToString());
        cmd.Parameters.AddWithValue("$a", r.AccountId);
        cmd.Parameters.AddWithValue("$m", r.Mode);
        cmd.Parameters.AddWithValue("$s", r.ShipId);
        cmd.Parameters.AddWithValue("$st", r.StartedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$en", r.EndedAt is { } e ? e.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$base", r.Baseline is null ? DBNull.Value : JsonSerializer.Serialize(r.Baseline, Json));
        cmd.Parameters.AddWithValue("$b", t is null ? DBNull.Value : t.Battles);
        cmd.Parameters.AddWithValue("$w", t is null ? DBNull.Value : t.Wins);
        cmd.Parameters.AddWithValue("$l", t is null ? DBNull.Value : t.Losses);
        cmd.Parameters.AddWithValue("$d", t is null ? DBNull.Value : t.Draws);
        cmd.Parameters.AddWithValue("$dmg", t is null ? DBNull.Value : t.Damage);
        cmd.Parameters.AddWithValue("$xp", t is null ? DBNull.Value : t.Xp);
        cmd.Parameters.AddWithValue("$f", t is null ? DBNull.Value : t.Frags);
        cmd.Parameters.AddWithValue("$sv", t is null ? DBNull.Value : t.Survived);
        cmd.Parameters.AddWithValue("$p", t is null ? DBNull.Value : t.PlanesKilled);
        cmd.Parameters.AddWithValue("$res", r.ResolvedAt is { } x ? x.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$x", (object?)Personal.PersonalJson.Write(r.Extras) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public BattleResult? Get(string battleKey)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE battle_key = $k";
        cmd.Parameters.AddWithValue("$k", battleKey);
        return Read(cmd).FirstOrDefault();
    }

    /// <summary>Results started after <paramref name="since"/>, newest first.</summary>
    public List<BattleResult> Since(DateTimeOffset since, int limit = 500)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE started_at >= $t ORDER BY started_at DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$t", since.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$l", limit);
        return Read(cmd);
    }

    /// <summary>Open (pending) results of one account, oldest first.</summary>
    public List<BattleResult> Open(Region region, long accountId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE region = $r AND account_id = $a AND status = 'Pending' ORDER BY started_at";
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$a", accountId);
        return Read(cmd);
    }

    /// <summary>Every result of one account, newest first (personal analytics).</summary>
    public List<BattleResult> All(Region region, long accountId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE region = $r AND account_id = $a ORDER BY started_at DESC";
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$a", accountId);
        return Read(cmd);
    }

    /// <summary>Every result by battle key (Battle History joins them to the recorded battles).</summary>
    public Dictionary<string, BattleResult> AllByKey()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select;
        return Read(cmd).GroupBy(r => r.BattleKey).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>All open results, any account, oldest first.</summary>
    public List<BattleResult> AllOpen()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE status = 'Pending' ORDER BY started_at";
        return Read(cmd);
    }

    private const string Select = """
        SELECT battle_key, region, account_id, mode, ship_id, started_at, ended_at, status, baseline_json,
               battles, wins, losses, draws, damage, xp, frags, survived, planes, resolved_at, extra_json
        FROM battle_results
        """;

    private static List<BattleResult> Read(SqliteCommand cmd)
    {
        var list = new List<BattleResult>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            StatTotals? result = rd.IsDBNull(9) ? null : new StatTotals(
                rd.GetInt32(9), rd.GetInt32(10), rd.GetInt32(11), rd.GetInt32(12),
                rd.GetInt64(13), rd.GetInt64(14), rd.GetInt32(15), rd.GetInt32(16), rd.GetInt32(17));
            list.Add(new BattleResult(
                rd.GetString(0), Enum.Parse<Region>(rd.GetString(1)), rd.GetInt64(2), rd.GetString(3), rd.GetInt64(4),
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(5)),
                rd.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(6)),
                Enum.TryParse<ResultStatus>(rd.GetString(7), out var s) ? s : ResultStatus.Pending,
                rd.IsDBNull(8) ? null : JsonSerializer.Deserialize<StatTotals>(rd.GetString(8), Json),
                result,
                rd.IsDBNull(18) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(18)),
                rd.IsDBNull(19) ? null : Personal.PersonalJson.Read(rd.GetString(19))));
        }
        return list;
    }
}
