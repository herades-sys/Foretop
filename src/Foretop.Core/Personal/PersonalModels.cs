using System.Text.Json;
using Foretop.Core.Api;

namespace Foretop.Core.Personal;

/// <summary>
/// Combat totals of one ship from ships/stats (one battle-type block), beyond what account totals give. The difference
/// of two readings around exactly one battle is that battle's spotting damage, potential damage, hits and shots.
/// </summary>
public sealed record ShipCombatTotals(
    int Battles, long Spotting, long ArtPotential, long TorpPotential, int ShipsSpotted, int Planes,
    int MbHits, int MbShots, int TorpHits, int TorpShots, int SecHits, int SecShots)
{
    public long Potential => ArtPotential + TorpPotential;

    public ShipCombatTotals Minus(ShipCombatTotals o) => new(
        Battles - o.Battles, Spotting - o.Spotting, ArtPotential - o.ArtPotential, TorpPotential - o.TorpPotential,
        ShipsSpotted - o.ShipsSpotted, Planes - o.Planes, MbHits - o.MbHits, MbShots - o.MbShots,
        TorpHits - o.TorpHits, TorpShots - o.TorpShots, SecHits - o.SecHits, SecShots - o.SecShots);

    /// <summary>The fields to request for one block ("pvp", "rank_solo", "pve").</summary>
    public static string Fields(string block) => string.Join(",", new[]
    {
        "battles", "damage_scouting", "art_agro", "torpedo_agro", "ships_spotted", "planes_killed",
        "main_battery.hits", "main_battery.shots", "torpedoes.hits", "torpedoes.shots", "second_battery.hits", "second_battery.shots",
    }.Select(f => block + "." + f));

    public static ShipCombatTotals? From(JsonElement ship, string block)
    {
        if (!ship.TryGetProperty(block, out var b) || b.ValueKind != JsonValueKind.Object) return null;
        long L(string name) => b.TryGetProperty(name, out var v) && v.TryGetInt64(out var x) ? x : 0;
        int H(string group, string name) => b.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty(name, out var v) && v.TryGetInt32(out var x) ? x : 0;
        return new ShipCombatTotals((int)L("battles"), L("damage_scouting"), L("art_agro"), L("torpedo_agro"), (int)L("ships_spotted"),
            (int)L("planes_killed"), H("main_battery", "hits"), H("main_battery", "shots"), H("torpedoes", "hits"), H("torpedoes", "shots"),
            H("second_battery", "hits"), H("second_battery", "shots"));
    }
}

/// <summary>What the replay of a battle says about the recording player, after the checks in <see cref="ReplayFacts"/>.</summary>
public sealed record ReplayBattleFacts
{
    /// <summary>Base XP (before Premium Account, flags and bonuses), from the replay's own result data.</summary>
    public int? BaseXp { get; init; }
    public long? Damage { get; init; }
    /// <summary>Ships this player sank, counted from the replay's death messages.</summary>
    public int? Kills { get; init; }
    public bool? Survived { get; init; }
    /// <summary>Seconds from the start of the battle to the player's death, or to the end of the recording when they survived.</summary>
    public double? LifeTime { get; init; }
    /// <summary>Place within the own team by damage dealt (1 = most damage), and the team size it is out of.</summary>
    public int? TeamPosition { get; init; }
    public int? TeamSize { get; init; }
    /// <summary>Length of the battle as recorded (from the start of the battle timer).</summary>
    public double? Duration { get; init; }
    public string? ReplayFile { get; init; }
}

/// <summary>
/// Extra per-battle data kept with an own battle result (battle_results.extra_json). Written once and not recalculated,
/// so later API refreshes or rebalances do not change past battles.
/// </summary>
public sealed record PersonalExtras
{
    public string? Map { get; init; }
    public string? MatchGroup { get; init; }
    public int? ShipTier { get; init; }
    public string? ShipClass { get; init; }
    public string? ShipName { get; init; }

    /// <summary>The played ship's combat totals at the start of the battle.</summary>
    public ShipCombatTotals? ShipBaseline { get; init; }
    /// <summary>The difference for exactly this battle; null until known.</summary>
    public ShipCombatTotals? ShipDelta { get; init; }
    /// <summary>Why <see cref="ShipDelta"/> is missing for good ("several battles counted at once", …).</summary>
    public string? ShipDeltaNote { get; init; }

    public ReplayBattleFacts? Replay { get; init; }
    /// <summary>Why <see cref="Replay"/> is missing for good ("no replay found", "replay data did not match", …).</summary>
    public string? ReplayNote { get; init; }

    /// <summary>Battle rating: the wows-numbers PR formula applied to this one battle (calculated by Foretop).</summary>
    public double? Rating { get; init; }
    /// <summary>Expected damage, frags and win rate of the ship used for <see cref="Rating"/>, kept with it.</summary>
    public double[]? RatingExpected { get; init; }
}

public static class PersonalJson
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string? Write(PersonalExtras? e) => e is null ? null : JsonSerializer.Serialize(e, Options);

    public static PersonalExtras? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PersonalExtras>(json, Options); }
        catch (JsonException) { return null; }
    }
}
