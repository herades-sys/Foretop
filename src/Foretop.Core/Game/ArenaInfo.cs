using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Foretop.Core.Game;

/// <summary>One roster entry from tempArenaInfo.json.</summary>
/// <param name="Name">Player nickname (the only reliable player key in the file).</param>
/// <param name="ShipId">Encyclopedia ship ID.</param>
/// <param name="Relation">0 = the local player, 1 = ally, 2 = enemy.</param>
/// <param name="EntityId">Battle-local entity ID. Not an account ID; negative for bots.</param>
public sealed record ArenaVehicle(string Name, long ShipId, int Relation, long EntityId)
{
    public bool IsSelf => Relation == 0;

    /// <summary>Friendly side. Operations put allied AI at relation 1 too, so "≤ 1" is the rule.</summary>
    public bool IsFriendly => Relation <= 1;

    /// <summary>Bots use ":Name:" nicknames or negative entity IDs.</summary>
    public bool IsBot =>
        EntityId < 0 || (Name.Length > 2 && Name[0] == ':' && Name[^1] == ':');
}

/// <summary>Parsed content of tempArenaInfo.json (or the JSON header of a .wowsreplay).</summary>
public sealed record ArenaInfo(
    string MatchGroup,
    string? MapDisplayName,
    string? ScenarioName,
    string? GameLogic,
    int PlayersPerTeam,
    DateTime? StartedAt,
    string? PlayerName,
    string? PlayerVehicle,
    string? ClientVersion,
    IReadOnlyList<ArenaVehicle> Vehicles,
    string RawJson)
{
    public BattleType BattleType => BattleTypes.FromMatchGroup(MatchGroup);

    /// <summary>Readable map name: "42_Neighbors" becomes "Neighbors".</summary>
    public string MapName => BattleTypes.PrettyMapName(MapDisplayName);

    public IEnumerable<ArenaVehicle> Friendly => Vehicles.Where(v => v.IsFriendly);
    public IEnumerable<ArenaVehicle> Enemy => Vehicles.Where(v => !v.IsFriendly);
    public ArenaVehicle? Self => Vehicles.FirstOrDefault(v => v.IsSelf);

    /// <summary>
    /// Synthetic battle key. The file carries no battle ID, so this hashes the start
    /// time and the roster. Same battle → same key; it is not a Wargaming ID.
    /// </summary>
    public string BattleKey { get; } = ComputeKey(StartedAt, Vehicles);

    private static string ComputeKey(DateTime? startedAt, IReadOnlyList<ArenaVehicle> vehicles)
    {
        var sb = new StringBuilder();
        sb.Append(startedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-");
        foreach (var v in vehicles.OrderBy(v => v.Name, StringComparer.Ordinal))
            sb.Append('|').Append(v.Name).Append(':').Append(v.ShipId).Append(':').Append(v.Relation);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

public static class ArenaInfoParser
{
    /// <summary>Parses tempArenaInfo.json text. Returns false (with a reason) for partial or malformed files.</summary>
    public static bool TryParse(string json, out ArenaInfo? info, out string? error)
    {
        info = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "file is empty";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root is not an object";
                return false;
            }

            if (!root.TryGetProperty("vehicles", out var vehiclesEl) || vehiclesEl.ValueKind != JsonValueKind.Array)
            {
                error = "no vehicles array (replays disabled, or a client that does not write the roster)";
                return false;
            }

            var vehicles = new List<ArenaVehicle>(vehiclesEl.GetArrayLength());
            foreach (var v in vehiclesEl.EnumerateArray())
            {
                var name = GetString(v, "name");
                if (string.IsNullOrEmpty(name)) continue;
                vehicles.Add(new ArenaVehicle(
                    name,
                    GetLong(v, "shipId") ?? 0,
                    (int)(GetLong(v, "relation") ?? 2),
                    GetLong(v, "id") ?? 0));
            }

            if (vehicles.Count == 0)
            {
                error = "vehicles array is empty";
                return false;
            }

            info = new ArenaInfo(
                MatchGroup: GetString(root, "matchGroup") ?? "unknown",
                MapDisplayName: GetString(root, "mapDisplayName") ?? GetString(root, "mapName"),
                ScenarioName: GetString(root, "scenario"),
                GameLogic: GetString(root, "gameLogic") ?? GetString(root, "logic"),
                PlayersPerTeam: (int)(GetLong(root, "playersPerTeam") ?? 0),
                StartedAt: ParseDate(GetString(root, "dateTime")),
                PlayerName: GetString(root, "playerName"),
                PlayerVehicle: GetString(root, "playerVehicle"),
                ClientVersion: GetString(root, "clientVersionFromXml") ?? GetString(root, "clientVersionFromExe"),
                Vehicles: vehicles,
                RawJson: json);
            return true;
        }
        catch (JsonException ex)
        {
            error = "invalid JSON (" + ex.Message + ")";
            return false;
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? GetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var l) => l,
            JsonValueKind.Number => (long)p.GetDouble(),
            JsonValueKind.String when long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    /// <summary>The client writes local time as "dd.MM.yyyy HH:mm:ss".</summary>
    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParseExact(s, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var d) ? d : null;
}
