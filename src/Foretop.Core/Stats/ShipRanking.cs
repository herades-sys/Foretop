namespace Foretop.Core.Stats;

/// <summary>A ship ranked by its Personal Rating, with the tier read from the ship catalogue.</summary>
public sealed record RankedShip(ShipPerformance Perf, ShipInfo Ship)
{
    public int Battles => Perf.Totals.Battles;
    public double Pr => Perf.Pr!.Value;
}

/// <summary>The result of ranking a player's ships: the ships shown, how many matched and what was left out.</summary>
public sealed record BestShipsResult(
    IReadOnlyList<RankedShip> Top,
    int Matching,
    int WithoutPr,
    int UnknownShips);

/// <summary>
/// "Best performing ships": a player's ships ranked by per-ship PR (unchanged, from <see cref="PersonalRating"/>), filtered
/// by the ships' real tiers and a minimum number of battles. Ships whose tier is unknown (not in the catalogue) or that
/// have no PR (no expected values) are never ranked; they are counted so the page can say so.
/// </summary>
public static class ShipRanking
{
    public static readonly int[] Tiers = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>Roman numeral for a tier; 11 is "XI" here (the ship lists use ★).</summary>
    public static string Roman(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X",
        11 => "XI", _ => tier.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <param name="ships">The player's per-ship results with the catalogue entry (null when the ship is unknown).</param>
    /// <param name="tiers">Selected tiers; empty = all tiers.</param>
    /// <param name="minBattles">Minimum battles in the ship (1 or more).</param>
    /// <param name="take">How many ships to show.</param>
    public static BestShipsResult Rank(IEnumerable<(ShipPerformance Perf, ShipInfo? Ship)> ships, IReadOnlySet<int> tiers, int minBattles, int take = 10)
    {
        minBattles = Math.Max(1, minBattles);
        var withoutPr = 0;
        var unknown = 0;
        var matching = new List<RankedShip>();
        foreach (var (perf, ship) in ships)
        {
            if (perf.Totals.Battles < minBattles) continue;
            if (ship is null || ship.Tier <= 0)
            {
                unknown++; // its tier is not known, so it cannot be placed in any tier filter
                continue;
            }
            if (tiers.Count > 0 && !tiers.Contains(ship.Tier)) continue;
            if (perf.Pr is null)
            {
                withoutPr++;
                continue;
            }
            matching.Add(new RankedShip(perf, ship));
        }
        var top = matching.OrderByDescending(r => r.Pr).ThenByDescending(r => r.Battles).ThenBy(r => r.Ship.Name, StringComparer.CurrentCulture)
            .Take(Math.Max(0, take)).ToList();
        return new BestShipsResult(top, matching.Count, withoutPr, unknown);
    }

    /// <summary>Selected tiers as text, with runs joined: {6,7,8,10} → "VI–VIII, X"; empty → "All tiers".</summary>
    public static string TierLabel(IEnumerable<int> tiers)
    {
        var sorted = tiers.Distinct().Order().ToList();
        if (sorted.Count == 0) return "All tiers";
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? Roman(sorted[i]) : j == i + 1 ? $"{Roman(sorted[i])}, {Roman(sorted[j])}" : $"{Roman(sorted[i])}–{Roman(sorted[j])}");
            i = j + 1;
        }
        return (sorted.Count == 1 ? "Tier " : "Tiers ") + string.Join(", ", parts);
    }
}
