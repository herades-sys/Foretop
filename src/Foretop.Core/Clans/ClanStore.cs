using Foretop.Core.Api;
using Foretop.Core.Data;

namespace Foretop.Core.Clans;

/// <summary>A member as the API lists them now.</summary>
public sealed record ClanMember(long AccountId, string Name, string Role, DateTimeOffset? JoinedAt);

/// <summary>A member Foretop saw before who is no longer in the clan: they left between LastSeen and Noticed.</summary>
public sealed record LeftMember(long AccountId, string Name, string Role, DateTimeOffset? JoinedAt, DateTimeOffset LastSeen, DateTimeOffset Noticed);

/// <summary>What Foretop knows about a clan from its own checks.</summary>
public sealed record ClanRecord(Region Region, long ClanId, string Tag, string Name, bool Tracked, DateTimeOffset FirstChecked, DateTimeOffset LastChecked);

/// <summary>
/// Member lists of the clans Foretop has looked at. The Wargaming API lists only current members, so departures are
/// found by comparing each check with the previous one; Foretop can only know about departures after its first check.
/// </summary>
public sealed class ClanStore(Database db)
{
    /// <summary>Records the current member list; members of the last check who are missing now are marked as left.</summary>
    public void Record(Region region, long clanId, string tag, string name, IReadOnlyCollection<ClanMember> members, DateTimeOffset? at = null)
    {
        var now = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO clans(region, clan_id, tag, name, tracked, first_checked, last_checked) VALUES ($r, $c, $t, $n, 0, $now, $now)
                ON CONFLICT(region, clan_id) DO UPDATE SET tag=excluded.tag, name=excluded.name, last_checked=excluded.last_checked
                """;
            cmd.Parameters.AddWithValue("$r", region.ToString());
            cmd.Parameters.AddWithValue("$c", clanId);
            cmd.Parameters.AddWithValue("$t", tag);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        foreach (var m in members)
        {
            using var cmd = c.CreateCommand();
            // A member who comes back is current again (left_seen cleared); first_seen stays the first time Foretop saw them.
            cmd.CommandText = """
                INSERT INTO clan_members(region, clan_id, account_id, name, role, joined_at, first_seen, last_seen, left_seen)
                VALUES ($r, $c, $a, $n, $role, $j, $now, $now, NULL)
                ON CONFLICT(region, clan_id, account_id) DO UPDATE SET name=excluded.name, role=excluded.role,
                    joined_at=excluded.joined_at, last_seen=excluded.last_seen, left_seen=NULL
                """;
            cmd.Parameters.AddWithValue("$r", region.ToString());
            cmd.Parameters.AddWithValue("$c", clanId);
            cmd.Parameters.AddWithValue("$a", m.AccountId);
            cmd.Parameters.AddWithValue("$n", m.Name);
            cmd.Parameters.AddWithValue("$role", m.Role);
            cmd.Parameters.AddWithValue("$j", m.JoinedAt is { } j ? j.ToUnixTimeSeconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE clan_members SET left_seen=$now WHERE region=$r AND clan_id=$c AND left_seen IS NULL AND last_seen < $now";
            cmd.Parameters.AddWithValue("$r", region.ToString());
            cmd.Parameters.AddWithValue("$c", clanId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Members seen earlier who are gone, most recently noticed first.</summary>
    public List<LeftMember> Left(Region region, long clanId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, name, role, joined_at, last_seen, left_seen FROM clan_members
            WHERE region=$r AND clan_id=$c AND left_seen IS NOT NULL ORDER BY left_seen DESC, name
            """;
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$c", clanId);
        using var rd = cmd.ExecuteReader();
        var list = new List<LeftMember>();
        while (rd.Read())
            list.Add(new LeftMember(rd.GetInt64(0), rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(3)),
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(4)), DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(5))));
        return list;
    }

    public ClanRecord? Get(Region region, long clanId) =>
        Query("WHERE region=$r AND clan_id=$c", region, clanId).FirstOrDefault();

    /// <summary>Clans to re-check in the background, on any server.</summary>
    public List<ClanRecord> Tracked() => Query("WHERE tracked=1", null, null);

    public void SetTracked(Region region, long clanId, bool tracked)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE clans SET tracked=$t WHERE region=$r AND clan_id=$c";
        cmd.Parameters.AddWithValue("$t", tracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$r", region.ToString());
        cmd.Parameters.AddWithValue("$c", clanId);
        cmd.ExecuteNonQuery();
    }

    private List<ClanRecord> Query(string where, Region? region, long? clanId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT region, clan_id, tag, name, tracked, first_checked, last_checked FROM clans " + where + " ORDER BY tag";
        if (region is { } r) cmd.Parameters.AddWithValue("$r", r.ToString());
        if (clanId is { } id) cmd.Parameters.AddWithValue("$c", id);
        using var rd = cmd.ExecuteReader();
        var list = new List<ClanRecord>();
        while (rd.Read())
        {
            if (!Enum.TryParse<Region>(rd.GetString(0), out var reg)) continue;
            list.Add(new ClanRecord(reg, rd.GetInt64(1), rd.GetString(2), rd.GetString(3), rd.GetInt64(4) != 0,
                DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(5)), DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(6))));
        }
        return list;
    }
}
