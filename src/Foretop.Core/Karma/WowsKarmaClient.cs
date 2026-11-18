using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Karma;

/// <summary>
/// A player's karma from WoWS Karma (wows-karma.com, a community site): the in-game karma it reads from Wargaming,
/// and its own site karma and ratings, which come from players' posts on the site.
/// </summary>
/// <param name="GameKarma">Karma shown in the game (players' compliments and reports).</param>
/// <param name="SiteKarma">Karma from posts on WoWS Karma.</param>
/// <param name="Performance">Site rating for performance (from posts' flairs).</param>
/// <param name="Teamplay">Site rating for teamplay.</param>
/// <param name="Courtesy">Site rating for courtesy.</param>
public sealed record KarmaInfo(int GameKarma, int SiteKarma, int Performance, int Teamplay, int Courtesy);

/// <summary>
/// Reads karma from the WoWS Karma API (one request per battle: POST /api/Player/karmas-full with every account ID)
/// and caches it per account. Players the site does not know (or who opted out) are not in the answer and stay
/// without karma. Never throws for network or format problems: it logs, falls back to cached values and returns what
/// it has.
/// </summary>
public sealed class WowsKarmaClient(HttpClient http, ApiCache cache, ILogger<WowsKarmaClient>? log = null)
{
    private const string Kind = "wows_karma";
    private const int BatchSize = 100;
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    /// <summary>How long a fetched value is reused before asking again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>API host of the region's WoWS Karma instance.</summary>
    public static string ApiHost(Region r) => r switch
    {
        Region.Eu => "https://api.wows-karma.com",
        Region.Na => "https://api.na.wows-karma.com",
        Region.Asia => "https://api.asia.wows-karma.com",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    /// <summary>The player's page on the region's WoWS Karma site.</summary>
    public static string ProfileUrl(Region r, long accountId, string nickname)
    {
        var host = r switch
        {
            Region.Eu => "https://wows-karma.com",
            Region.Na => "https://na.wows-karma.com",
            Region.Asia => "https://asia.wows-karma.com",
            _ => throw new ArgumentOutOfRangeException(nameof(r)),
        };
        return $"{host}/player/{accountId},{Uri.EscapeDataString(nickname)}";
    }

    /// <summary>Karma for the accounts WoWS Karma knows; cached values first, the rest in one request per 100.</summary>
    public async Task<IReadOnlyDictionary<long, KarmaInfo>> GetAsync(Region region, IReadOnlyCollection<long> accountIds, CancellationToken ct)
    {
        var result = new Dictionary<long, KarmaInfo>();
        var missing = new List<long>();
        var stale = new Dictionary<long, KarmaInfo?>();
        foreach (var id in accountIds.Distinct())
        {
            var c = cache.Get(region, Kind, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var value = c is null ? null : Parse(c.Json);
            if (c is not null && c.Age < MaxAge)
            {
                if (value is not null) result[id] = value;
                continue; // fresh, also when the site did not know the player
            }
            missing.Add(id);
            if (c is not null) stale[id] = value;
        }

        foreach (var batch in missing.Chunk(BatchSize))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ApiHost(region) + "/api/Player/karmas-full")
                {
                    Content = JsonContent.Create(batch),
                };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var rows = JsonSerializer.Deserialize<List<KarmaDto>>(json, Json) ?? [];
                var byId = rows.Where(r => r.Id > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var id in batch)
                {
                    var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (byId.TryGetValue(id, out var dto))
                    {
                        var info = dto.ToInfo();
                        cache.Put(region, Kind, key, JsonSerializer.Serialize(info, Json));
                        result[id] = info;
                    }
                    else
                    {
                        cache.Put(region, Kind, key, null); // not on WoWS Karma: remembered, asked again after MaxAge
                    }
                }
                _log.LogInformation("WoWS Karma: {Found} of {Asked} players known", batch.Count(byId.ContainsKey), batch.Length);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                           || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("WoWS Karma not reachable ({Message}); using cached karma where there is some", ex.Message);
                foreach (var id in batch)
                    if (stale.TryGetValue(id, out var old) && old is not null) result[id] = old;
            }
        }
        return result;
    }

    private static KarmaInfo? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<KarmaInfo>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>AccountFullKarmaDTO of the WoWS Karma API.</summary>
    internal sealed record KarmaDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("karma")] int Karma,
        [property: JsonPropertyName("gameKarma")] int GameKarma,
        [property: JsonPropertyName("performance")] int Performance,
        [property: JsonPropertyName("teamplay")] int Teamplay,
        [property: JsonPropertyName("courtesy")] int Courtesy)
    {
        public KarmaInfo ToInfo() => new(GameKarma, Karma, Performance, Teamplay, Courtesy);
    }
}
