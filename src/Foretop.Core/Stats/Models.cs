using Foretop.Core.Api;

namespace Foretop.Core.Stats;

/// <summary>Official totals from the API. Every average on it is derived (simple arithmetic).</summary>
public sealed record StatTotals(
    int Battles, int Wins, int Losses, int Draws,
    long Damage, long Xp, int Frags, int Survived, int PlanesKilled)
{
    public double? WinRate => Battles > 0 ? 100.0 * Wins / Battles : null;
    public double? AvgDamage => Battles > 0 ? (double)Damage / Battles : null;
    public double? AvgXp => Battles > 0 ? (double)Xp / Battles : null;
    public double? AvgFrags => Battles > 0 ? (double)Frags / Battles : null;
    public double? SurvivalRate => Battles > 0 ? 100.0 * Survived / Battles : null;
    public double? AvgPlanes => Battles > 0 ? (double)PlanesKilled / Battles : null;
    public int Deaths => Battles - Survived;
    /// <summary>Kills ÷ deaths; null when there are no deaths (or no battles), shown as "—", never as a division by zero.</summary>
    public double? KillDeath => Deaths > 0 ? (double)Frags / Deaths : null;

    public static StatTotals? From(StatsBlockDto? b) => b is null
        ? null
        : new StatTotals(b.Battles, b.Wins, b.Losses, b.Draws, b.DamageDealt, b.Xp, b.Frags, b.SurvivedBattles, b.PlanesKilled);

    /// <summary>Activity between an older snapshot and this one.</summary>
    public StatTotals Minus(StatTotals older) => new(
        Battles - older.Battles, Wins - older.Wins, Losses - older.Losses, Draws - older.Draws,
        Damage - older.Damage, Xp - older.Xp, Frags - older.Frags, Survived - older.Survived,
        PlanesKilled - older.PlanesKilled);
}

public enum ShipClass
{
    Unknown,
    Destroyer,
    Cruiser,
    Battleship,
    AirCarrier,
    Submarine,
}

public sealed record ShipInfo(
    long ShipId, string Name, int Tier, ShipClass Class, string Nation,
    bool IsPremium, bool IsSpecial, string? ImageSmall, string? ImageContour)
{
    public string ClassShort => Class switch
    {
        ShipClass.Destroyer => "DD",
        ShipClass.Cruiser => "CA",
        ShipClass.Battleship => "BB",
        ShipClass.AirCarrier => "CV",
        ShipClass.Submarine => "SS",
        _ => "?",
    };

    public string TierRoman => Tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI",
        7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X", 11 => "★", _ => Tier.ToString(),
    };

    public static ShipClass ParseClass(string? type) => type switch
    {
        "Destroyer" => ShipClass.Destroyer,
        "Cruiser" => ShipClass.Cruiser,
        "Battleship" => ShipClass.Battleship,
        "AirCarrier" => ShipClass.AirCarrier,
        "Submarine" => ShipClass.Submarine,
        _ => ShipClass.Unknown,
    };
}

public sealed record ShipPerformance(long ShipId, StatTotals Totals, double? Pr, DateTimeOffset? LastBattle);

/// <summary>
/// Activity since <see cref="Since"/>, from the difference between Foretop's own snapshots of a
/// player (the API has no recent-stats endpoint). The window is whatever the snapshots really
/// cover, not a promise of exactly N days.
/// </summary>
public sealed record RecentStats(StatTotals Totals, DateTimeOffset Since)
{
    public int Days => Math.Max(1, (DateTimeOffset.UtcNow.UtcDateTime.Date - Since.UtcDateTime.Date).Days);
}

public enum LoadState
{
    Pending,
    Resolving,
    LoadingStats,
    LoadingShips,
    Ready,
    Hidden,
    NotFound,
    Bot,
    Error,
}

/// <summary>Everything known about one player. Immutable; the service publishes a new instance on every change.</summary>
public sealed record PlayerSummary
{
    public required string Name { get; init; }
    public int Relation { get; init; }
    public long ShipId { get; init; }
    public bool IsBot { get; init; }

    public LoadState State { get; init; } = LoadState.Pending;
    public string? StatusText { get; init; }

    public long? AccountId { get; init; }
    public string? ClanTag { get; init; }

    /// <summary>Third-party: WoWS Karma (in-game karma and the site's own karma); null when not known there.</summary>
    public Karma.KarmaInfo? Karma { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastBattleAt { get; init; }
    public int? ServiceLevel { get; init; }
    public bool Hidden { get; init; }

    /// <summary>Random battles (statistics.pvp).</summary>
    public StatTotals? Overall { get; init; }
    public StatTotals? Ranked { get; init; }

    /// <summary>Random-battle stats in the ship sailed in this battle.</summary>
    public StatTotals? CurrentShip { get; init; }

    /// <summary>Third-party: wows-numbers Personal Rating over all ships with expected values.</summary>
    public double? Pr { get; init; }
    public double? ShipPr { get; init; }

    /// <summary>Third-party based: actual damage ÷ expected damage (wows-numbers) over all ships. 1.0 = average.</summary>
    public double? DamageRatio { get; init; }

    /// <summary>Same ratio for the ship sailed in this battle.</summary>
    public double? ShipDamageRatio { get; init; }

    public RecentStats? Recent7 { get; init; }
    public RecentStats? Recent28 { get; init; }

    public IReadOnlyList<ShipPerformance>? Ships { get; init; }

    /// <summary>
    /// Totals over ships of tier V+ … X+ and XI only (see <see cref="TierFilter"/>). Kept when a battle is saved, unlike
    /// <see cref="Ships"/>, so recorded battles can be filtered too. Null until the per-ship statistics are loaded.
    /// </summary>
    public IReadOnlyList<TierStats>? ByTier { get; init; }
    public DateTimeOffset? StatsFetchedAt { get; init; }

    /// <summary>The API was unreachable, so these are older cached statistics.</summary>
    public bool StaleData { get; init; }

    public bool IsFriendly => Relation <= 1;
}
