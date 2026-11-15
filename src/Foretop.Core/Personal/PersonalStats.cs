using Foretop.Core.Stats;

namespace Foretop.Core.Personal;

/// <summary>
/// One of your own battles with a known result: exactly one battle counted by the API (status Ok). Combined rows
/// (several battles counted at once) are not single battles and are left out of personal analytics.
/// </summary>
public sealed record PersonalBattle(
    string Key, DateTimeOffset When, string Mode, string? MatchGroup, string? Map, long ShipId, string? ShipName, int? Tier,
    string? ShipClass, StatTotals Totals, PersonalExtras Extras)
{
    public char Result => Totals.Wins > 0 ? 'W' : Totals.Draws > 0 ? 'D' : 'L';
    public string ResultText => Result switch { 'W' => "Victory", 'D' => "Draw", _ => "Defeat" };
    public long Damage => Totals.Damage;
    public int Kills => Totals.Frags;
    public bool Survived => Totals.Survived > 0;
    public int Deaths => Survived ? 0 : 1;
    /// <summary>XP as the API counts it: including Premium Account, flags and bonuses.</summary>
    public long Xp => Totals.Xp;
    public int Planes => Totals.PlanesKilled;
    public int? BaseXp => Extras.Replay?.BaseXp;
    public long? Spotting => Extras.ShipDelta?.Spotting;
    public long? Potential => Extras.ShipDelta?.Potential;
    public double? LifeTime => Extras.Replay?.LifeTime;
    public int? TeamPosition => Extras.Replay?.TeamPosition;
    public int? TeamSize => Extras.Replay?.TeamSize;
    public double? Rating => Extras.Rating;

    /// <summary>Main battery hits ÷ shots of this battle; null without the ship difference or without shots.</summary>
    public double? MbAccuracy => Extras.ShipDelta is { MbShots: > 0 } d ? 100.0 * d.MbHits / d.MbShots : null;
    public double? TorpAccuracy => Extras.ShipDelta is { TorpShots: > 0 } d ? 100.0 * d.TorpHits / d.TorpShots : null;

    public static PersonalBattle? From(BattleResult r) =>
        r.Status == ResultStatus.Ok && r.Result is { Battles: 1 } t
            ? new PersonalBattle(r.BattleKey, r.StartedAt, r.Mode, r.Extras?.MatchGroup, r.Extras?.Map, r.ShipId, r.Extras?.ShipName,
                r.Extras?.ShipTier, r.Extras?.ShipClass, t, r.Extras ?? new PersonalExtras())
            : null;
}

/// <summary>An average over the battles that have the value: <see cref="Count"/> of them (0 = unavailable, never 0).</summary>
public readonly record struct Metric(double? Value, int Count)
{
    public static Metric None => new(null, 0);
    public static Metric Average(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? None : new Metric(list.Average(), list.Count);
    }
    /// <summary>Σnumerator ÷ Σdenominator × 100 over the battles that have both (weighted, not an average of per-battle %).</summary>
    public static Metric Ratio(IEnumerable<(long Num, long Den)?> pairs)
    {
        long n = 0, d = 0;
        var count = 0;
        foreach (var p in pairs)
        {
            if (p is not { } x) continue;
            n += x.Num; d += x.Den; count++;
        }
        return d > 0 ? new Metric(100.0 * n / d, count) : new Metric(null, count);
    }
}

/// <summary>Statistics over a set of your battles. Rates and averages divide totals by battles.</summary>
public sealed record PeriodStats(
    int Battles, int Wins, int Losses, int Draws, int Kills, int Deaths, long Damage, long Xp,
    Metric BaseXp, Metric Spotting, Metric Potential, Metric MbAccuracy, Metric TorpAccuracy, Metric LifeTime,
    Metric Planes, Metric TeamPosition, double? Pr, int PrBattles, DateTimeOffset? From, DateTimeOffset? To)
{
    public double? WinRate => Battles > 0 ? 100.0 * Wins / Battles : null;
    public double? SurvivalRate => Battles > 0 ? 100.0 * (Battles - Deaths) / Battles : null;
    public double? AvgDamage => Battles > 0 ? (double)Damage / Battles : null;
    public double? AvgXp => Battles > 0 ? (double)Xp / Battles : null;
    public double? AvgKills => Battles > 0 ? (double)Kills / Battles : null;
    public double? AvgDeaths => Battles > 0 ? (double)Deaths / Battles : null;
    /// <summary>Kills ÷ deaths; null when there were no deaths ("—", not infinity).</summary>
    public double? KillDeath => Deaths > 0 ? (double)Kills / Deaths : null;

    public static PeriodStats Of(IReadOnlyCollection<PersonalBattle> battles)
    {
        int wins = 0, losses = 0, draws = 0, kills = 0, deaths = 0;
        long damage = 0, xp = 0;
        // PR of the period: the wows-numbers formula on the summed values, against the expected values stored with each
        // battle (so it does not change when wows-numbers updates them). Battles without stored values are left out.
        double prDmg = 0, prWins = 0, prFrags = 0, expDmg = 0, expWins = 0, expFrags = 0;
        var prBattles = 0;
        foreach (var b in battles)
        {
            if (b.Result == 'W') wins++; else if (b.Result == 'D') draws++; else losses++;
            kills += b.Kills; deaths += b.Deaths; damage += b.Damage; xp += b.Xp;
            if (b.Extras.RatingExpected is [var ed, var ef, var ew])
            {
                prBattles++;
                prDmg += b.Damage; prFrags += b.Kills; prWins += b.Result == 'W' ? 1 : 0;
                expDmg += ed; expFrags += ef; expWins += ew / 100.0;
            }
        }
        double? pr = null;
        if (prBattles > 0 && expDmg > 0 && expFrags > 0 && expWins > 0)
        {
            var nDmg = Math.Max(0, (prDmg / expDmg - 0.4) / 0.6);
            var nFrags = Math.Max(0, (prFrags / expFrags - 0.1) / 0.9);
            var nWins = Math.Max(0, (prWins / expWins - 0.7) / 0.3);
            pr = 700 * nDmg + 300 * nFrags + 150 * nWins;
        }
        return new PeriodStats(battles.Count, wins, losses, draws, kills, deaths, damage, xp,
            Metric.Average(battles.Select(b => (double?)b.BaseXp)),
            Metric.Average(battles.Select(b => (double?)b.Spotting)),
            Metric.Average(battles.Select(b => (double?)b.Potential)),
            Metric.Ratio(battles.Select(b => b.Extras.ShipDelta is { } d && d.MbShots > 0 ? ((long)d.MbHits, (long)d.MbShots) : ((long, long)?)null)),
            Metric.Ratio(battles.Select(b => b.Extras.ShipDelta is { } d && d.TorpShots > 0 ? ((long)d.TorpHits, (long)d.TorpShots) : ((long, long)?)null)),
            Metric.Average(battles.Select(b => b.LifeTime)),
            Metric.Average(battles.Select(b => (double?)b.Planes)),
            Metric.Average(battles.Select(b => (double?)b.TeamPosition)),
            pr, prBattles,
            battles.Count > 0 ? battles.Min(b => b.When) : null, battles.Count > 0 ? battles.Max(b => b.When) : null);
    }
}

/// <summary>Which battles a view covers: the newest N, all, or a date range; plus battle type, class and tier filters.</summary>
public sealed record PersonalQuery
{
    /// <summary>Newest N battles after filtering; null = all.</summary>
    public int? Last { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>"pvp", "rank_solo", "pve"; null = all.</summary>
    public string? Mode { get; init; }
    public string? ShipClass { get; init; }
    public int MinTier { get; init; }
    public int MaxTier { get; init; } = 99;
    public long? ShipId { get; init; }

    /// <summary>Applies the filters, then the period, to battles newest first; returns newest first.</summary>
    public List<PersonalBattle> Apply(IEnumerable<PersonalBattle> newestFirst)
    {
        var q = newestFirst.Where(b =>
            (Mode is null || b.Mode == Mode)
            && (ShipClass is null || b.ShipClass == ShipClass)
            && (MinTier <= 0 || b.Tier >= MinTier)
            && (MaxTier >= 99 || b.Tier <= MaxTier)
            && (ShipId is null || b.ShipId == ShipId)
            && (From is null || b.When >= From)
            && (To is null || b.When < To));
        return (Last is int n ? q.Take(n) : q).ToList();
    }
}

public static class PersonalSeries
{
    /// <summary>Values in time order: per battle, rolling mean over <paramref name="window"/> battles, or cumulative mean.</summary>
    public static List<(PersonalBattle Battle, double Value)> Build(IReadOnlyList<PersonalBattle> oldestFirst,
        Func<PersonalBattle, double?> value, SeriesMode mode, int window = 10)
    {
        var points = new List<(PersonalBattle, double)>();
        var seen = new List<double>();
        foreach (var b in oldestFirst)
        {
            if (value(b) is not double v) continue;
            seen.Add(v);
            var y = mode switch
            {
                SeriesMode.PerBattle => v,
                SeriesMode.Rolling => seen.Skip(Math.Max(0, seen.Count - window)).Average(),
                _ => seen.Average(),
            };
            points.Add((b, y));
        }
        return points;
    }

    /// <summary>Rolling or cumulative win rate / K-D / survival need totals, not means of 0/1 values for K/D.</summary>
    public static List<(PersonalBattle Battle, double Value)> BuildRatio(IReadOnlyList<PersonalBattle> oldestFirst,
        Func<PersonalBattle, (double Num, double Den)> parts, SeriesMode mode, int window = 10, double scale = 1)
    {
        var points = new List<(PersonalBattle, double)>();
        var seen = new List<(double N, double D)>();
        foreach (var b in oldestFirst)
        {
            var p = parts(b);
            seen.Add(p);
            var span = mode switch
            {
                SeriesMode.PerBattle => seen.Skip(seen.Count - 1),
                SeriesMode.Rolling => seen.Skip(Math.Max(0, seen.Count - window)),
                _ => seen,
            };
            double n = 0, d = 0;
            foreach (var (sn, sd) in span) { n += sn; d += sd; }
            if (d > 0) points.Add((b, scale * n / d));
        }
        return points;
    }
}

public enum SeriesMode { PerBattle, Rolling, Cumulative }

/// <summary>Tier bands for the tier analysis: I–V together, then each tier.</summary>
public static class TierBands
{
    public static IReadOnlyList<(string Label, int Min, int Max)> All { get; } =
    [
        ("I–V", 1, 5), ("VI", 6, 6), ("VII", 7, 7), ("VIII", 8, 8), ("IX", 9, 9), ("X", 10, 10), ("XI", 11, 11),
    ];

    /// <summary>Your whole account by band, from the per-ship API totals (all Random battles ever).</summary>
    public static List<(string Label, StatTotals Totals, double? Pr)> Account(IReadOnlyCollection<ShipPerformance> ships,
        Func<long, int?> tierOf, IReadOnlyDictionary<long, ExpectedValues> expected) =>
        All.Select(band =>
        {
            var eligible = ships.Where(s => tierOf(s.ShipId) is int t && t >= band.Min && t <= band.Max).ToList();
            return (band.Label, TierFilter.Sum(eligible.Select(s => s.Totals)),
                PersonalRating.Compute(eligible.Select(s => (s.ShipId, s.Totals)), expected));
        }).ToList();
}
