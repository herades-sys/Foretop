using Foretop.Core.Stats;

namespace Foretop.Core.Personal;

/// <summary>
/// Where the analytics come from. <see cref="Recorded"/>: your battles as Foretop recorded them (dates, battle types,
/// base XP from replays, per-battle rating). <see cref="Account"/>: the official per-ship totals of your account (all
/// Random battles ever, no dates).
/// </summary>
public enum AnalyticsSource { Recorded, Account }

/// <summary>A ship as the filters see it: tier and class from the ship list (API, else game files), nation key lower case.</summary>
public sealed record ShipMeta(long Id, string Name, int? Tier, string? Class, string? Nation);

/// <summary>
/// The parts of the PR formula, summed: actual damage, kills and wins against the expected values for the same
/// battles. Adding parts and computing once gives the PR of the whole set (weighted by battles), the same as
/// <see cref="PersonalRating.Compute"/> does for per-ship totals.
/// </summary>
public readonly record struct PrParts(double Damage, double Frags, double Wins, double ExpDamage, double ExpFrags, double ExpWins, int Battles)
{
    public static PrParts None => default;

    public static PrParts operator +(PrParts a, PrParts b) => new(a.Damage + b.Damage, a.Frags + b.Frags, a.Wins + b.Wins,
        a.ExpDamage + b.ExpDamage, a.ExpFrags + b.ExpFrags, a.ExpWins + b.ExpWins, a.Battles + b.Battles);

    /// <summary>Per-ship totals against the ship's expected values (expected × battles); None without expected values.</summary>
    public static PrParts From(StatTotals t, ExpectedValues? e) => e is null || t.Battles <= 0
        ? None
        : new PrParts(t.Damage, t.Frags, t.Wins, e.AvgDamage * t.Battles, e.AvgFrags * t.Battles, e.WinRate / 100.0 * t.Battles, t.Battles);

    /// <summary>The wows-numbers formula; null when nothing has expected values.</summary>
    public double? Pr
    {
        get
        {
            if (Battles == 0 || ExpDamage <= 0 || ExpFrags <= 0 || ExpWins <= 0) return null;
            var nDmg = Math.Max(0, (Damage / ExpDamage - 0.4) / 0.6);
            var nFrags = Math.Max(0, (Frags / ExpFrags - 0.1) / 0.9);
            var nWins = Math.Max(0, (Wins / ExpWins - 0.7) / 0.3);
            return 700 * nDmg + 300 * nFrags + 150 * nWins;
        }
    }
}

/// <summary>
/// One unit of data: a recorded battle (Battles = 1, with a date) or a ship's account totals (no date). Everything
/// in the analytics is a sum over these.
/// </summary>
public sealed record AnalyticsRecord(ShipMeta Ship, StatTotals Totals, DateTimeOffset? When, string? Mode, PrParts Pr, int? BaseXp,
    PersonalBattle? Battle = null)
{
    public static AnalyticsRecord FromBattle(PersonalBattle b, ShipMeta ship)
    {
        var pr = b.Extras.RatingExpected is [var ed, var ef, var ew]
            ? new PrParts(b.Damage, b.Kills, b.Result == 'W' ? 1 : 0, ed, ef, ew / 100.0, 1)
            : PrParts.None;
        return new AnalyticsRecord(ship, b.Totals, b.When, b.Mode, pr, b.BaseXp, b);
    }

    public static AnalyticsRecord FromShip(ShipPerformance s, ShipMeta ship, ExpectedValues? expected) =>
        new(ship, s.Totals, null, ResultModes.Random, PrParts.From(s.Totals, expected), null);
}

/// <summary>Sums over a set of records. Every rate and average divides totals (never averages of averages).</summary>
public sealed record AggregateStats(StatTotals Totals, PrParts PrParts, Metric BaseXp, int Ships, int Records, int UnknownTier)
{
    public int Battles => Totals.Battles;
    public int Wins => Totals.Wins;
    public int Losses => Totals.Losses;
    public int Draws => Totals.Draws;
    public int Kills => Totals.Frags;
    public int Deaths => Totals.Deaths;
    public long Damage => Totals.Damage;
    /// <summary>Victories ÷ battles × 100.</summary>
    public double? WinRate => Totals.WinRate;
    public double? AvgDamage => Totals.AvgDamage;
    public double? AvgXp => Totals.AvgXp;
    public double? AvgKills => Totals.AvgFrags;
    public double? AvgDeaths => Battles > 0 ? (double)Deaths / Battles : null;
    /// <summary>Kills ÷ deaths; null without deaths.</summary>
    public double? KillDeath => Totals.KillDeath;
    public double? SurvivalRate => Totals.SurvivalRate;
    public double? Pr => PrParts.Pr;
    /// <summary>Battles with expected values, i.e. behind <see cref="Pr"/>.</summary>
    public int PrBattles => PrParts.Battles;

    public static AggregateStats Empty { get; } = Of([]);

    public static AggregateStats Of(IEnumerable<AnalyticsRecord> records)
    {
        int battles = 0, wins = 0, losses = 0, draws = 0, frags = 0, survived = 0, planes = 0, count = 0, unknown = 0;
        long damage = 0, xp = 0;
        var pr = PrParts.None;
        var ships = new HashSet<long>();
        double baseXp = 0;
        var baseXpCount = 0;
        foreach (var r in records)
        {
            var t = r.Totals;
            battles += t.Battles; wins += t.Wins; losses += t.Losses; draws += t.Draws;
            damage += t.Damage; xp += t.Xp; frags += t.Frags; survived += t.Survived; planes += t.PlanesKilled;
            pr += r.Pr;
            ships.Add(r.Ship.Id);
            if (r.BaseXp is int bx) { baseXp += bx; baseXpCount++; }
            if (r.Ship.Tier is null) unknown++;
            count++;
        }
        return new AggregateStats(new StatTotals(battles, wins, losses, draws, damage, xp, frags, survived, planes), pr,
            baseXpCount == 0 ? Metric.None : new Metric(baseXp / baseXpCount, baseXpCount), ships.Count, count, unknown);
    }
}

/// <summary>
/// Which records the analytics use. Empty sets mean "all". Tier, class, nation and ship look at the ship; date,
/// battle type and "last N" need recorded battles (records without a date do not match a date filter). The minimum
/// is per ship, over the records left after every other filter.
/// </summary>
public sealed record AnalyticsFilter
{
    public static AnalyticsFilter All { get; } = new();

    public IReadOnlySet<int> Tiers { get; init; } = new HashSet<int>();
    public int MinBattles { get; init; } = 1;
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Newest N battles after the other filters; null = all.</summary>
    public int? LastBattles { get; init; }
    public IReadOnlySet<string> Classes { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Nations { get; init; } = new HashSet<string>();
    public IReadOnlySet<long> Ships { get; init; } = new HashSet<long>();
    /// <summary>"pvp", "rank_solo", "pve"; null = all.</summary>
    public string? Mode { get; init; }
    /// <summary>Set by <see cref="Intersect"/> when the two filters cannot both hold (e.g. Random and Ranked).</summary>
    public bool NoMatch { get; init; }

    public bool NeedsDates => From is not null || To is not null || LastBattles is not null;

    public bool Matches(AnalyticsRecord r) =>
        !NoMatch
        && (Tiers.Count == 0 || r.Ship.Tier is int t && Tiers.Contains(t))
        && (Classes.Count == 0 || r.Ship.Class is { } c && Classes.Contains(c))
        && (Nations.Count == 0 || r.Ship.Nation is { } n && Nations.Contains(n))
        && (Ships.Count == 0 || Ships.Contains(r.Ship.Id))
        && (Mode is null || r.Mode == Mode)
        && (From is null || r.When is { } w1 && w1 >= From)
        && (To is null || r.When is { } w2 && w2 < To);

    /// <summary>The matching records, newest first when dated; then "last N"; then ships below the minimum are dropped.</summary>
    public List<AnalyticsRecord> Apply(IEnumerable<AnalyticsRecord> records)
    {
        var q = records.Where(Matches).OrderByDescending(r => r.When ?? DateTimeOffset.MinValue).ToList();
        if (LastBattles is int n) q = q.Where(r => r.When is not null).Take(n).ToList();
        if (MinBattles > 1)
        {
            var enough = q.GroupBy(r => r.Ship.Id).Where(g => g.Sum(r => r.Totals.Battles) >= MinBattles).Select(g => g.Key).ToHashSet();
            q = q.Where(r => enough.Contains(r.Ship.Id)).ToList();
        }
        return q;
    }

    /// <summary>
    /// Both filters at once (a chart's own filter on top of the global one): sets intersect, the higher minimum and
    /// the narrower dates win. A chart filter can only narrow, never widen, the global selection.
    /// </summary>
    public AnalyticsFilter Intersect(AnalyticsFilter other)
    {
        static IReadOnlySet<T> Both<T>(IReadOnlySet<T> a, IReadOnlySet<T> b, ref bool none)
        {
            if (a.Count == 0) return b;
            if (b.Count == 0) return a;
            var x = a.Where(b.Contains).ToHashSet();
            if (x.Count == 0) none = true;
            return x;
        }
        var noMatch = NoMatch || other.NoMatch;
        var tiers = Both(Tiers, other.Tiers, ref noMatch);
        var classes = Both(Classes, other.Classes, ref noMatch);
        var nations = Both(Nations, other.Nations, ref noMatch);
        var ships = Both(Ships, other.Ships, ref noMatch);
        if (Mode is not null && other.Mode is not null && Mode != other.Mode) noMatch = true;
        return new AnalyticsFilter
        {
            Tiers = tiers, Classes = classes, Nations = nations, Ships = ships,
            MinBattles = Math.Max(MinBattles, other.MinBattles),
            From = Later(From, other.From), To = Earlier(To, other.To),
            LastBattles = LastBattles is int a && other.LastBattles is int b ? Math.Min(a, b) : LastBattles ?? other.LastBattles,
            Mode = Mode ?? other.Mode,
            NoMatch = noMatch,
        };
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    private static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;
}

/// <summary>Grouped sums: per ship, per tier, per class.</summary>
public static class AnalyticsGroups
{
    public sealed record Group(string Key, string Label, int? Tier, ShipMeta? Ship, AggregateStats Stats);

    public static List<Group> ByShip(IEnumerable<AnalyticsRecord> records) =>
        records.GroupBy(r => r.Ship.Id)
            .Select(g => { var s = g.First().Ship; return new Group(s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Name, s.Tier, s, AggregateStats.Of(g)); })
            .ToList();

    /// <summary>One group per tier 1–11 that has records (unknown tiers are left out and counted by the caller).</summary>
    public static List<Group> ByTier(IEnumerable<AnalyticsRecord> records) =>
        records.Where(r => r.Ship.Tier is int t && t > 0).GroupBy(r => r.Ship.Tier!.Value).OrderBy(g => g.Key)
            .Select(g => new Group(g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), "Tier " + ShipRanking.Roman(g.Key), g.Key, null, AggregateStats.Of(g)))
            .ToList();

    public static List<Group> ByClass(IEnumerable<AnalyticsRecord> records) =>
        records.Where(r => r.Ship.Class is not null).GroupBy(r => r.Ship.Class!)
            .Select(g => new Group(g.Key, ClassName(g.Key), null, null, AggregateStats.Of(g)))
            .OrderByDescending(g => g.Stats.Battles).ToList();

    public static string ClassName(string? c) => c switch
    {
        "Destroyer" => "Destroyers", "Cruiser" => "Cruisers", "Battleship" => "Battleships", "AirCarrier" => "Aircraft carriers",
        "Submarine" => "Submarines", null or "" => "Unknown class", _ => c,
    };
}

/// <summary>What ship rankings and per-ship charts sort by.</summary>
public enum ShipMetric { Pr, WinRate, AvgDamage, Battles, Kills, Survival }

public static class AnalyticsRanking
{
    public static double? Value(AggregateStats s, ShipMetric m) => m switch
    {
        ShipMetric.Pr => s.Pr,
        ShipMetric.WinRate => s.WinRate,
        ShipMetric.AvgDamage => s.AvgDamage,
        ShipMetric.Battles => s.Battles,
        ShipMetric.Kills => s.AvgKills,
        _ => s.SurvivalRate,
    };

    /// <summary>
    /// Groups with at least <paramref name="minBattles"/> battles and a value for the metric, best (or worst) first.
    /// Ties: more battles first. Returns the top <paramref name="take"/> and how many qualified.
    /// </summary>
    public static (List<AnalyticsGroups.Group> Top, int Qualified, int WithoutValue) Rank(IEnumerable<AnalyticsGroups.Group> groups, ShipMetric metric,
        bool highestFirst, int minBattles, int take)
    {
        var enough = groups.Where(g => g.Stats.Battles >= Math.Max(1, minBattles)).ToList();
        var withValue = enough.Where(g => Value(g.Stats, metric) is not null).ToList();
        var sorted = highestFirst
            ? withValue.OrderByDescending(g => Value(g.Stats, metric)).ThenByDescending(g => g.Stats.Battles)
            : withValue.OrderBy(g => Value(g.Stats, metric)).ThenByDescending(g => g.Stats.Battles);
        return (sorted.Take(Math.Max(0, take)).ToList(), withValue.Count, enough.Count - withValue.Count);
    }
}

/// <summary>PR over time from recorded battles: the PR formula on the totals up to each battle (or the last N).</summary>
public static class PrProgression
{
    /// <param name="oldestFirst">Dated records in time order.</param>
    /// <param name="window">Null = all battles up to each point; else the last <paramref name="window"/> battles.</param>
    public static List<(AnalyticsRecord Record, double Pr, int Battles)> Build(IReadOnlyList<AnalyticsRecord> oldestFirst, int? window)
    {
        var points = new List<(AnalyticsRecord, double, int)>();
        var parts = new List<PrParts>();
        foreach (var r in oldestFirst)
        {
            if (r.Pr.Battles == 0) continue; // no expected values stored: not part of a PR
            parts.Add(r.Pr);
            var span = window is int w ? parts.Skip(Math.Max(0, parts.Count - w)) : parts;
            var sum = span.Aggregate(PrParts.None, (a, b) => a + b);
            if (sum.Pr is double pr) points.Add((r, pr, sum.Battles));
        }
        return points;
    }
}
