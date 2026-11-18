using Foretop.Core.Api;
using Foretop.Core.Game;

namespace Foretop.Core.Stats;

/// <summary>Kinds of battle whose statistics the Wargaming API reports per account and per ship.</summary>
public enum GameMode
{
    Random,
    Ranked,
    Coop,
    Operations,
}

/// <summary>One player's statistics in a game mode other than Random (Random stays in <see cref="PlayerSummary"/> itself).</summary>
/// <param name="Overall">Account totals in this mode; null when not reported.</param>
/// <param name="CurrentShip">Totals in the ship of this battle; null when never played in this mode.</param>
/// <param name="ByTier">Totals over ships of tier V+ … XI (as <see cref="PlayerSummary.ByTier"/>); PR is always null.</param>
public sealed record ModeStats(StatTotals? Overall, StatTotals? CurrentShip, IReadOnlyList<TierStats>? ByTier);

public static class GameModes
{
    public static IReadOnlyList<GameMode> All { get; } = [GameMode.Random, GameMode.Ranked, GameMode.Coop, GameMode.Operations];

    public static string Label(GameMode m) => m switch
    {
        GameMode.Random => "Random",
        GameMode.Ranked => "Ranked",
        GameMode.Coop => "Co-op",
        GameMode.Operations => "Operations",
        _ => m.ToString(),
    };

    /// <summary>Badge text next to the team name: "RANKED".</summary>
    public static string Badge(GameMode m) => Label(m).ToUpperInvariant();

    /// <summary>The mode whose statistics fit a battle: Ranked for a ranked battle, and so on; Random otherwise.</summary>
    public static GameMode ForBattle(BattleType type) => type switch
    {
        BattleType.Ranked => GameMode.Ranked,
        BattleType.Coop => GameMode.Coop,
        BattleType.Operation => GameMode.Operations,
        _ => GameMode.Random,
    };

    /// <summary>Account totals of a mode. Operations adds solo, division and hard division (no damage or kills there).</summary>
    public static StatTotals? AccountTotals(AccountStatisticsDto? s, GameMode m) => s is null ? null : m switch
    {
        GameMode.Random => StatTotals.From(s.Pvp),
        GameMode.Ranked => StatTotals.From(s.RankSolo),
        GameMode.Coop => StatTotals.From(s.Pve),
        GameMode.Operations => Operations(s.OperSolo, s.OperDiv, s.OperDivHard),
        _ => null,
    };

    /// <summary>Per-ship totals of a mode; null when the API sent nothing for it.</summary>
    public static StatTotals? ShipTotals(ShipStatsDto s, GameMode m) => m switch
    {
        GameMode.Random => StatTotals.From(s.Pvp),
        GameMode.Ranked => StatTotals.From(s.RankSolo),
        GameMode.Coop => StatTotals.From(s.Pve),
        GameMode.Operations => Operations(s.OperSolo, s.OperDiv, s.OperDivHard),
        _ => null,
    };

    private static StatTotals? Operations(params StatsBlockDto?[] blocks)
    {
        var present = blocks.Where(b => b is not null).Select(StatTotals.From).OfType<StatTotals>().ToList();
        return present.Count == 0 ? null : TierFilter.Sum(present) with { CombatKnown = false };
    }

    /// <summary>
    /// The player as seen in a game mode: overall, current-ship and per-tier totals of that mode. PR, damage vs.
    /// expected and recent activity exist for Random only and are removed (null, shown as "—"). A player whose other
    /// modes were never loaded (an older saved battle) shows no statistics in them rather than Random ones.
    /// </summary>
    public static PlayerSummary Project(PlayerSummary p, GameMode m)
    {
        if (m == GameMode.Random) return p;
        var ms = p.Modes is { } modes && modes.TryGetValue(m, out var v) ? v : null;
        return p with
        {
            Overall = ms?.Overall,
            CurrentShip = ms?.CurrentShip,
            ByTier = ms?.ByTier,
            Pr = null,
            ShipPr = null,
            DamageRatio = null,
            ShipDamageRatio = null,
            Recent7 = null,
            Recent28 = null,
        };
    }
}
