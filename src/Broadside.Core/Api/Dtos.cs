using System.Text.Json;
using System.Text.Json.Serialization;

namespace Broadside.Core.Api;

// Shapes of the Wargaming WoWS API responses we use. Property names follow the API
// (snake_case) through JsonNamingPolicy.SnakeCaseLower. Only fields we read are declared.

public sealed class StatsBlockDto
{
    public int Battles { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public long DamageDealt { get; set; }
    public long Xp { get; set; }
    public int Frags { get; set; }
    public int SurvivedBattles { get; set; }
    public int SurvivedWins { get; set; }
    public int PlanesKilled { get; set; }
    public long MaxDamageDealt { get; set; }
    public int MaxFragsBattle { get; set; }
    public long MaxXp { get; set; }
    public long DamageScouting { get; set; }
    public long ArtAgro { get; set; }
}

public sealed class AccountStatisticsDto
{
    public int Battles { get; set; }
    public long Distance { get; set; }
    public StatsBlockDto? Pvp { get; set; }
    public StatsBlockDto? PvpSolo { get; set; }
    public StatsBlockDto? PvpDiv2 { get; set; }
    public StatsBlockDto? PvpDiv3 { get; set; }
    public StatsBlockDto? RankSolo { get; set; }
    public StatsBlockDto? Pve { get; set; }
}

public sealed class AccountInfoDto
{
    public long AccountId { get; set; }
    public string Nickname { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastBattleTime { get; set; }
    public long LogoutAt { get; set; }
    public bool HiddenProfile { get; set; }
    public int LevelingTier { get; set; }
    public AccountStatisticsDto? Statistics { get; set; }
}

public sealed class ShipStatsDto
{
    public long ShipId { get; set; }
    public int Battles { get; set; }
    public long LastBattleTime { get; set; }
    public StatsBlockDto? Pvp { get; set; }
    public StatsBlockDto? RankSolo { get; set; }
}

public sealed class AccountListEntryDto
{
    public long AccountId { get; set; }
    public string Nickname { get; set; } = "";
}

public sealed class ClanShortDto
{
    public long ClanId { get; set; }
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ClanAccountInfoDto
{
    public long AccountId { get; set; }
    public long? ClanId { get; set; }
    public string? Role { get; set; }
    public long? JoinedAt { get; set; }
    public ClanShortDto? Clan { get; set; }
}

public sealed class ShipImagesDto
{
    public string? Small { get; set; }
    public string? Contour { get; set; }
}

public sealed class EncyclopediaShipDto
{
    public long ShipId { get; set; }
    public string Name { get; set; } = "";
    public int Tier { get; set; }
    public string Type { get; set; } = "";
    public string Nation { get; set; } = "";
    public bool IsPremium { get; set; }
    public bool IsSpecial { get; set; }
    public ShipImagesDto? Images { get; set; }
}

public sealed class EncyclopediaInfoDto
{
    public string? GameVersion { get; set; }
    public long ShipsUpdatedAt { get; set; }
}

/// <summary>Envelope of every API response.</summary>
public sealed record WgResponse(JsonElement Data, JsonElement? Meta);

internal static class WgJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // The API sends null for many numbers (hidden profiles, accounts without that data).
        Converters =
        {
            new NullAsDefault<int>(), new NullAsDefault<long>(), new NullAsDefault<double>(), new NullAsDefault<bool>(),
        },
    };

    private sealed class NullAsDefault<T> : JsonConverter<T> where T : struct
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            if (typeof(T) == typeof(bool)) return (T)(object)(reader.TokenType == JsonTokenType.True);
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                if (typeof(T) == typeof(int)) return (T)(object)(int.TryParse(s, out var i) ? i : 0);
                if (typeof(T) == typeof(long)) return (T)(object)(long.TryParse(s, out var l) ? l : 0L);
                return (T)(object)(double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0);
            }
            if (typeof(T) == typeof(int)) return (T)(object)(reader.TryGetInt32(out var iv) ? iv : (int)reader.GetDouble());
            if (typeof(T) == typeof(long)) return (T)(object)(reader.TryGetInt64(out var lv) ? lv : (long)reader.GetDouble());
            return (T)(object)reader.GetDouble();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value);
    }
}
