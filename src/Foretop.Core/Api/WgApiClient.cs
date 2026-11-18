using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Api;

/// <summary>
/// Thin typed client for the Wargaming World of Warships API. Every HTTP attempt goes
/// through the <see cref="RequestScheduler"/> (priority + rate limit); transient errors
/// are retried with exponential backoff.
/// </summary>
public sealed class WgApiClient
{
    private const int MaxAttempts = 4;
    private readonly HttpClient _http;
    private readonly RequestScheduler _scheduler;
    private readonly Func<string?> _applicationId;
    private readonly ILogger _log;

    public WgApiClient(HttpClient http, RequestScheduler scheduler, Func<string?> applicationId, ILogger<WgApiClient>? log = null)
    {
        _http = http;
        _scheduler = scheduler;
        _applicationId = applicationId;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>Total HTTP requests sent this session (for the status bar).</summary>
    public int RequestCount => _requestCount;
    private int _requestCount;

    public bool HasApplicationId => !string.IsNullOrWhiteSpace(_applicationId());

    /// <summary>Outcome of the most recent request, for the connection pill in the top bar.</summary>
    public ApiHealth Health { get; private set; } = ApiHealth.Unknown;
    public DateTimeOffset? HealthAt { get; private set; }

    private void SetHealth(ApiHealth h)
    {
        Health = h;
        HealthAt = DateTimeOffset.UtcNow;
    }

    // ---------------------------------------------------------------- typed calls

    /// <summary>Nickname → account. Exact, case-insensitive. Names not returned do not exist on that region.</summary>
    public async Task<Dictionary<string, AccountListEntryDto>> ResolveNicknamesAsync(
        Region region, IReadOnlyCollection<string> names, RequestPriority priority, CancellationToken ct)
    {
        var result = new Dictionary<string, AccountListEntryDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in names.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            List<AccountListEntryDto> found;
            try
            {
                found = await ListExactAsync(region, chunk, priority, ct).ConfigureAwait(false);
            }
            catch (WgApiException ex) when (ex.Kind == WgErrorKind.Request && chunk.Length > 1)
            {
                // Some deployments enforce the 24-char search limit on the whole string; fall back to one name per call.
                _log.LogWarning("Batched account/list rejected ({Error}); resolving names one by one", ex.Message);
                found = [];
                foreach (var name in chunk)
                    found.AddRange(await ListExactAsync(region, [name], priority, ct).ConfigureAwait(false));
            }
            foreach (var e in found) result[e.Nickname] = e;
        }
        return result;
    }

    /// <summary>Players whose nickname starts with <paramref name="prefix"/> (min. 3 characters), for search suggestions.</summary>
    public async Task<List<AccountListEntryDto>> SearchNicknamesAsync(Region region, string prefix, int limit, CancellationToken ct)
    {
        if (prefix.Length < 3) return [];
        var r = await GetAsync(region, "account/list", new()
        {
            ["search"] = prefix,
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture),
        }, RequestPriority.Normal, ct).ConfigureAwait(false);
        return r.Data.ValueKind == JsonValueKind.Array
            ? r.Data.Deserialize<List<AccountListEntryDto>>(WgJson.Options) ?? []
            : [];
    }

    private async Task<List<AccountListEntryDto>> ListExactAsync(Region region, string[] names, RequestPriority priority, CancellationToken ct)
    {
        var r = await GetAsync(region, "account/list", new()
        {
            ["search"] = string.Join(',', names),
            ["type"] = "exact",
        }, priority, ct).ConfigureAwait(false);
        return r.Data.ValueKind == JsonValueKind.Array
            ? r.Data.Deserialize<List<AccountListEntryDto>>(WgJson.Options) ?? []
            : [];
    }

    /// <summary>Raw account/info data per account ID (null entry = account does not exist).</summary>
    public async Task<Dictionary<long, JsonElement?>> GetAccountInfoRawAsync(
        Region region, IReadOnlyCollection<long> accountIds, RequestPriority priority, CancellationToken ct, string? extra = null)
    {
        var result = new Dictionary<long, JsonElement?>();
        foreach (var chunk in accountIds.Distinct().Chunk(100))
        {
            var p = new Dictionary<string, string>
            {
                ["account_id"] = string.Join(',', chunk),
                ["fields"] = "account_id,nickname,created_at,last_battle_time,logout_at,hidden_profile,leveling_tier,statistics",
            };
            if (extra is not null) p["extra"] = extra;
            var r = await GetAsync(region, "account/info", p, priority, ct).ConfigureAwait(false);
            foreach (var id in chunk) result[id] = TryGetKeyed(r.Data, id);
        }
        return result;
    }

    /// <summary>Per-ship fields of the other game modes (Ranked, Co-op, Operations), kept small: totals only.</summary>
    private static readonly string ModeShipFields = string.Join(",",
        new[] { "rank_solo", "pve" }
            .SelectMany(b => new[] { "battles", "wins", "losses", "draws", "damage_dealt", "xp", "frags", "survived_battles", "planes_killed" }
                .Select(f => b + "." + f))
            .Concat(new[] { "oper_solo", "oper_div", "oper_div_hard" }
                .SelectMany(b => new[] { "battles", "wins", "losses", "xp", "survived_battles" }.Select(f => b + "." + f))));

    /// <summary>Account statistics blocks besides Random (statistics.pvp), for the game-mode switch.</summary>
    public const string ModeAccountExtra = "statistics.rank_solo,statistics.pve,statistics.oper_solo,statistics.oper_div,statistics.oper_div_hard";

    /// <summary>Raw ships/stats array for one account (null = hidden profile or no data).</summary>
    public async Task<JsonElement?> GetShipStatsRawAsync(Region region, long accountId, RequestPriority priority, CancellationToken ct)
    {
        var r = await GetAsync(region, "ships/stats", new()
        {
            ["account_id"] = accountId.ToString(CultureInfo.InvariantCulture),
            ["fields"] = "ship_id,battles,last_battle_time,pvp.battles,pvp.wins,pvp.losses,pvp.draws,pvp.damage_dealt,pvp.xp," +
                         "pvp.frags,pvp.survived_battles,pvp.planes_killed,pvp.max_damage_dealt,pvp.max_frags_battle," +
                         ModeShipFields,
            ["extra"] = "rank_solo,pve,oper_solo,oper_div,oper_div_hard",
        }, priority, ct).ConfigureAwait(false);
        return TryGetKeyed(r.Data, accountId);
    }

    /// <summary>
    /// One ship of one account with the combat fields of one battle-type block (see
    /// <see cref="Personal.ShipCombatTotals.Fields"/>). Null when hidden or never played.
    /// </summary>
    public async Task<JsonElement?> GetShipCombatRawAsync(Region region, long accountId, long shipId, string block,
        RequestPriority priority, CancellationToken ct)
    {
        var r = await GetAsync(region, "ships/stats", new()
        {
            ["account_id"] = accountId.ToString(CultureInfo.InvariantCulture),
            ["ship_id"] = shipId.ToString(CultureInfo.InvariantCulture),
            ["fields"] = "ship_id," + Personal.ShipCombatTotals.Fields(block),
        }, priority, ct).ConfigureAwait(false);
        return TryGetKeyed(r.Data, accountId) is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0] : null;
    }

    public async Task<Dictionary<long, JsonElement?>> GetClanInfoRawAsync(
        Region region, IReadOnlyCollection<long> accountIds, RequestPriority priority, CancellationToken ct)
    {
        var result = new Dictionary<long, JsonElement?>();
        foreach (var chunk in accountIds.Distinct().Chunk(100))
        {
            var r = await GetAsync(region, "clans/accountinfo", new()
            {
                ["account_id"] = string.Join(',', chunk),
                ["extra"] = "clan",
            }, priority, ct).ConfigureAwait(false);
            foreach (var id in chunk) result[id] = TryGetKeyed(r.Data, id);
        }
        return result;
    }

    public async Task<EncyclopediaInfoDto> GetEncyclopediaInfoAsync(Region region, RequestPriority priority, CancellationToken ct,
        string? applicationIdOverride = null)
    {
        var r = await GetAsync(region, "encyclopedia/info", new() { ["fields"] = "game_version,ships_updated_at" }, priority, ct,
            applicationIdOverride).ConfigureAwait(false);
        return r.Data.Deserialize<EncyclopediaInfoDto>(WgJson.Options) ?? new EncyclopediaInfoDto();
    }

    /// <summary>All ships, paged 100 at a time.</summary>
    public async Task<List<EncyclopediaShipDto>> GetAllShipsAsync(Region region, RequestPriority priority, CancellationToken ct)
    {
        var ships = new List<EncyclopediaShipDto>();
        var page = 1;
        var pageTotal = 1;
        do
        {
            var r = await GetAsync(region, "encyclopedia/ships", new()
            {
                ["fields"] = "ship_id,name,tier,type,nation,is_premium,is_special,images.small,images.contour",
                ["limit"] = "100",
                ["page_no"] = page.ToString(CultureInfo.InvariantCulture),
            }, priority, ct).ConfigureAwait(false);

            if (r.Data.ValueKind == JsonValueKind.Object)
                foreach (var prop in r.Data.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        ships.Add(prop.Value.Deserialize<EncyclopediaShipDto>(WgJson.Options)!);

            if (r.Meta is { } meta && meta.TryGetProperty("page_total", out var pt) && pt.TryGetInt32(out var total))
                pageTotal = total;
            page++;
        } while (page <= pageTotal && page < 50);
        return ships;
    }

    /// <summary>Checks an application ID with the cheapest call there is.</summary>
    public async Task<(bool Ok, string Message)> TestApplicationIdAsync(Region region, string applicationId, CancellationToken ct)
    {
        try
        {
            var info = await GetEncyclopediaInfoAsync(region, RequestPriority.High, ct, applicationId).ConfigureAwait(false);
            return (true, $"OK — game version {info.GameVersion}");
        }
        catch (WgApiException ex)
        {
            return (false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (false, "Network error: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- core

    private static JsonElement? TryGetKeyed(JsonElement data, long id)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var el)) return null;
        return el.ValueKind == JsonValueKind.Null ? null : el.Clone();
    }

    public async Task<WgResponse> GetAsync(Region region, string method, Dictionary<string, string> parameters,
        RequestPriority priority, CancellationToken ct, string? applicationIdOverride = null)
    {
        var appId = applicationIdOverride ?? _applicationId();
        if (string.IsNullOrWhiteSpace(appId))
            throw new WgApiException(WgErrorKind.Configuration, "No Wargaming application ID set. Add one in Settings.");

        var url = BuildUrl(region, method, parameters, appId.Trim());
        var safeUrl = BuildUrl(region, method, parameters, "***");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _scheduler.Enqueue(token => SendOnceAsync(url, safeUrl, token), priority, ct).ConfigureAwait(false);
            }
            catch (WgApiException ex) when (ex.Kind == WgErrorKind.Transient && attempt < MaxAttempts)
            {
                if (ex.Message.Contains("REQUEST_LIMIT_EXCEEDED")) _scheduler.Limiter.Pause(TimeSpan.FromSeconds(1));
                var delay = Backoff(attempt);
                _log.LogWarning("{Method} attempt {Attempt} failed ({Error}); retrying in {Delay} ms",
                    method, attempt, ex.Message, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));

    private static string BuildUrl(Region region, string method, Dictionary<string, string> parameters, string appId)
    {
        var sb = new StringBuilder(Regions.ApiHost(region)).Append("/wows/").Append(method).Append("/?application_id=")
            .Append(Uri.EscapeDataString(appId));
        foreach (var (k, v) in parameters)
            sb.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v));
        return sb.ToString();
    }

    private async Task<WgResponse> SendOnceAsync(string url, string safeUrl, CancellationToken ct)
    {
        Interlocked.Increment(ref _requestCount);
        var started = DateTime.UtcNow;
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            SetHealth(ApiHealth.Offline);
            throw new WgApiException(WgErrorKind.Transient, "Network error: " + ex.Message, inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            SetHealth(ApiHealth.Offline);
            throw new WgApiException(WgErrorKind.Transient, "Request timed out", inner: ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;

            if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                SetHealth(ApiHealth.Offline);
                throw new WgApiException(WgErrorKind.Transient, $"HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
            }

            using var doc = ParseOrThrow(body, resp.StatusCode);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "ok")
            {
                var err = root.TryGetProperty("error", out var e) ? e : default;
                var code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                var msg = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var field = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("field", out var f) ? f.GetString() : null;
                _log.LogWarning("API error {Code} {Message} for {Url} ({Ms} ms)", code, msg, safeUrl, ms);
                var error = WgApiException.FromApiError(code, msg, field);
                if (error.Kind == WgErrorKind.Configuration) SetHealth(ApiHealth.Rejected);
                else if (error.Kind == WgErrorKind.Transient && msg != "REQUEST_LIMIT_EXCEEDED") SetHealth(ApiHealth.Offline);
                throw error;
            }

            _log.LogDebug("API ok {Url} ({Ms} ms, {Bytes} bytes)", safeUrl, ms, body.Length);
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            JsonElement? meta = root.TryGetProperty("meta", out var me) ? me.Clone() : null;
            SetHealth(ApiHealth.Ok);
            return new WgResponse(data, meta);
        }
    }

    private static JsonDocument ParseOrThrow(string body, HttpStatusCode code)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            throw new WgApiException(WgErrorKind.Transient, $"Unreadable response (HTTP {(int)code})", (int)code, inner: ex);
        }
    }
}
