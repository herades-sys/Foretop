namespace Broadside.Core.Game;

public enum BattleType
{
    Unknown,
    Random,
    Ranked,
    Clan,
    Coop,
    Operation,
    Event,
    Brawl,
    Training,
}

public static class BattleTypes
{
    /// <summary>Maps tempArenaInfo "matchGroup" values. Unknown groups keep their raw name in the UI.</summary>
    public static BattleType FromMatchGroup(string? matchGroup) => matchGroup?.ToLowerInvariant() switch
    {
        "pvp" => BattleType.Random,
        "ranked" => BattleType.Ranked,
        "clan" => BattleType.Clan,
        "pve" => BattleType.Coop,
        "pve_premade" or "operation" => BattleType.Operation,
        "event" => BattleType.Event,
        "brawl" or "clan_brawl" => BattleType.Brawl,
        "training" => BattleType.Training,
        _ => BattleType.Unknown,
    };

    public static string Label(BattleType type, string? rawMatchGroup = null) => type switch
    {
        BattleType.Random => "Random Battle",
        BattleType.Ranked => "Ranked Battle",
        BattleType.Clan => "Clan Battle",
        BattleType.Coop => "Co-op Battle",
        BattleType.Operation => "Operation",
        BattleType.Event => "Event Battle",
        BattleType.Brawl => "Brawl",
        BattleType.Training => "Training Room",
        _ => string.IsNullOrEmpty(rawMatchGroup) ? "Unknown battle type" : $"Battle ({rawMatchGroup})",
    };

    public static string PrettyMapName(string? mapDisplayName)
    {
        if (string.IsNullOrWhiteSpace(mapDisplayName)) return "Unknown map";
        var s = mapDisplayName;
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i > 0 && i < s.Length && s[i] == '_') s = s[(i + 1)..];
        return s.Replace('_', ' ');
    }
}
