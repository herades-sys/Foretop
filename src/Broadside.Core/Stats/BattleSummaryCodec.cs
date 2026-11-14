using System.Text.Json;
using System.Text.Json.Serialization;

namespace Broadside.Core.Stats;

/// <summary>
/// Stores everyone's statistics at the time of a battle. The per-ship list is left out
/// (it is large and refetchable); everything shown in the battle tables is kept.
/// </summary>
public static class BattleSummaryCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IEnumerable<PlayerSummary> players) =>
        JsonSerializer.Serialize(players.Select(p => p with { Ships = null }).ToList(), Options);

    /// <summary>Empty list for missing or unreadable summaries (older formats included).</summary>
    public static List<PlayerSummary> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PlayerSummary>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
