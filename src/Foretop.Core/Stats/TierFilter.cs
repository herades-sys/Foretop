namespace Foretop.Core.Stats;

/// <summary>
/// A player's Random-battle statistics counting only ships of <see cref="MinTier"/> and above, summed from the per-ship
/// totals of the official API (ships/stats). Ships whose tier is unknown are left out and counted in
/// <see cref="UnknownTierShips"/>. PR is the same wows-numbers formula over the same ships.
/// </summary>
public sealed record TierStats(int MinTier, StatTotals Totals, double? Pr, double? DamageRatio, int Ships, int UnknownTierShips);

public static class TierFilter
{
    /// <summary>Minimum tiers offered; 11 means tier XI (superships) only, as there is nothing above it.</summary>
    public static IReadOnlyList<int> Levels { get; } = [5, 6, 7, 8, 9, 10, 11];

    public static string Roman(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X",
        11 => "XI", _ => tier.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>"All tiers", "Tier VI+", "Tier XI only".</summary>
    public static string Label(int minTier) => minTier switch
    {
        <= 0 => "All tiers",
        >= 11 => "Tier XI only",
        _ => $"Tier {Roman(minTier)}+",
    };

    /// <summary>Badge text: "ALL TIERS", "TIER VI+", "TIER XI".</summary>
    public static string Badge(int minTier) => minTier switch
    {
        <= 0 => "ALL TIERS",
        >= 11 => "TIER XI",
        _ => $"TIER {Roman(minTier)}+",
    };

    /// <summary>Totals added field by field; averages and rates are then derived from these sums (weighted).</summary>
    public static StatTotals Sum(IEnumerable<StatTotals> items)
    {
        int battles = 0, wins = 0, losses = 0, draws = 0, frags = 0, survived = 0, planes = 0;
        long damage = 0, xp = 0;
        var combatKnown = true;
        foreach (var t in items)
        {
            battles += t.Battles; wins += t.Wins; losses += t.Losses; draws += t.Draws;
            damage += t.Damage; xp += t.Xp; frags += t.Frags; survived += t.Survived; planes += t.PlanesKilled;
            combatKnown &= t.CombatKnown;
        }
        return new StatTotals(battles, wins, losses, draws, damage, xp, frags, survived, planes, combatKnown);
    }

    /// <summary>One <see cref="TierStats"/> per level in <see cref="Levels"/>.</summary>
    public static IReadOnlyList<TierStats> ComputeAll(IReadOnlyCollection<ShipPerformance> ships, Func<long, int?> tierOf,
        IReadOnlyDictionary<long, ExpectedValues> expected)
    {
        var known = new List<(ShipPerformance Ship, int Tier)>(ships.Count);
        var unknown = 0;
        foreach (var s in ships)
        {
            if (tierOf(s.ShipId) is int t and > 0) known.Add((s, t));
            else unknown++;
        }
        return Levels.Select(min =>
        {
            var eligible = known.Where(k => k.Tier >= min).Select(k => k.Ship).ToList();
            var pairs = eligible.Select(s => (s.ShipId, s.Totals)).ToList();
            return new TierStats(min, Sum(eligible.Select(s => s.Totals)), PersonalRating.Compute(pairs, expected),
                PersonalRating.DamageRatio(pairs, expected), eligible.Count, unknown);
        }).ToList();
    }

    public static TierStats? For(PlayerSummary p, int minTier) =>
        minTier <= 0 ? null : p.ByTier?.FirstOrDefault(t => t.MinTier == minTier);

    /// <summary>
    /// The player as seen through the filter: overall totals, PR and damage ratio replaced by the filtered ones (null when
    /// they are not available, never zero), recent activity removed because Foretop's snapshots have no per-tier split.
    /// Everything that reads <see cref="PlayerSummary.Overall"/> (tables, team averages, strength estimate) then follows.
    /// </summary>
    public static PlayerSummary Project(PlayerSummary p, int minTier)
    {
        if (minTier <= 0) return p;
        var t = For(p, minTier);
        return p with
        {
            Overall = t?.Totals,
            Pr = t?.Pr,
            DamageRatio = t?.DamageRatio,
            Recent7 = null,
            Recent28 = null,
        };
    }
}
