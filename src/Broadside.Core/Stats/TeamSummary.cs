namespace Broadside.Core.Stats;

/// <summary>
/// Derived team averages. Each average only counts players that have the value
/// (hidden profiles and bots are left out), and says how many that was.
/// </summary>
public sealed record TeamSummary(
    int Players,
    int WithStats,
    double? AvgWinRate,
    double? AvgPr,
    int PrCount,
    double? AvgDamage,
    double? AvgXp,
    double? AvgShipWinRate,
    long TotalBattles,
    double? AvgBattles,
    int Hidden,
    int Bots,
    double? AvgFrags,
    double? AvgSurvival,
    double? AvgAccountAgeYears,
    double? AvgDamageRatio)
{
    public static TeamSummary From(IReadOnlyCollection<PlayerSummary> players)
    {
        var withStats = players.Where(p => p.Overall is { Battles: > 0 }).ToList();
        var prs = players.Where(p => p.Pr is not null).Select(p => p.Pr!.Value).ToList();
        var ship = players.Where(p => p.CurrentShip is { Battles: > 0 }).ToList();
        return new TeamSummary(
            Players: players.Count,
            WithStats: withStats.Count,
            AvgWinRate: Avg(withStats.Select(p => p.Overall!.WinRate)),
            AvgPr: prs.Count > 0 ? prs.Average() : null,
            PrCount: prs.Count,
            AvgDamage: Avg(withStats.Select(p => p.Overall!.AvgDamage)),
            AvgXp: Avg(withStats.Select(p => p.Overall!.AvgXp)),
            AvgShipWinRate: Avg(ship.Select(p => p.CurrentShip!.WinRate)),
            TotalBattles: withStats.Sum(p => (long)p.Overall!.Battles),
            AvgBattles: withStats.Count > 0 ? withStats.Average(p => (double)p.Overall!.Battles) : null,
            Hidden: players.Count(p => p.Hidden),
            Bots: players.Count(p => p.IsBot),
            AvgFrags: Avg(withStats.Select(p => p.Overall!.AvgFrags)),
            AvgSurvival: Avg(withStats.Select(p => p.Overall!.SurvivalRate)),
            AvgAccountAgeYears: Avg(players.Where(p => p.CreatedAt is not null && !p.IsBot)
                .Select(p => (double?)(DateTimeOffset.UtcNow - p.CreatedAt!.Value).TotalDays / 365.25)),
            AvgDamageRatio: Avg(players.Select(p => p.DamageRatio)));
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Average() : null;
    }
}

/// <summary>
/// Rough strength comparison from average PR and win rate. An indication from past
/// statistics, not a prediction of the battle.
/// </summary>
public static class TeamComparison
{
    /// <summary>Share of combined strength held by the friendly team (0..1), or null when there is too little data.</summary>
    public static double? FriendlyShare(TeamSummary friendly, TeamSummary enemy)
    {
        if (friendly.PrCount < 3 || enemy.PrCount < 3) return null;
        var f = friendly.AvgPr!.Value;
        var e = enemy.AvgPr!.Value;
        if (f + e <= 0) return null;
        return f / (f + e);
    }
}
