using System.Text.Json;
using Foretop.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Stats;

/// <summary>Expected per-battle values for one ship, from wows-numbers. WinRate is in percent.</summary>
public sealed record ExpectedValues(double AvgDamage, double AvgFrags, double WinRate);

public enum PrBand
{
    None,
    Bad,
    BelowAverage,
    Average,
    Good,
    VeryGood,
    Great,
    Unicum,
    SuperUnicum,
}

/// <summary>
/// Personal Rating as defined by wows-numbers.com (third-party). Ships without expected
/// values are left out of both the actual and the expected sums.
/// </summary>
public static class PersonalRating
{
    public static double? Compute(IEnumerable<(long ShipId, StatTotals Totals)> ships, IReadOnlyDictionary<long, ExpectedValues> expected)
    {
        double dmg = 0, wins = 0, frags = 0, expDmg = 0, expWins = 0, expFrags = 0;
        long battles = 0;
        foreach (var (shipId, t) in ships)
        {
            if (t.Battles <= 0 || !expected.TryGetValue(shipId, out var e)) continue;
            battles += t.Battles;
            dmg += t.Damage;
            wins += t.Wins;
            frags += t.Frags;
            expDmg += e.AvgDamage * t.Battles;
            expWins += e.WinRate / 100.0 * t.Battles;
            expFrags += e.AvgFrags * t.Battles;
        }

        if (battles == 0 || expDmg <= 0 || expWins <= 0 || expFrags <= 0) return null;

        var nDmg = Math.Max(0, (dmg / expDmg - 0.4) / 0.6);
        var nFrags = Math.Max(0, (frags / expFrags - 0.1) / 0.9);
        var nWins = Math.Max(0, (wins / expWins - 0.7) / 0.3);
        return 700 * nDmg + 300 * nFrags + 150 * nWins;
    }

    /// <summary>Actual damage ÷ expected damage over ships with expected values; 1.0 = average for those ships.</summary>
    public static double? DamageRatio(IEnumerable<(long ShipId, StatTotals Totals)> ships, IReadOnlyDictionary<long, ExpectedValues> expected)
    {
        double dmg = 0, exp = 0;
        foreach (var (shipId, t) in ships)
        {
            if (t.Battles <= 0 || !expected.TryGetValue(shipId, out var e)) continue;
            dmg += t.Damage;
            exp += e.AvgDamage * t.Battles;
        }
        return exp > 0 ? dmg / exp : null;
    }

    public static PrBand Band(double? pr) => pr switch
    {
        null => PrBand.None,
        < 750 => PrBand.Bad,
        < 1100 => PrBand.BelowAverage,
        < 1350 => PrBand.Average,
        < 1550 => PrBand.Good,
        < 1750 => PrBand.VeryGood,
        < 2100 => PrBand.Great,
        < 2450 => PrBand.Unicum,
        _ => PrBand.SuperUnicum,
    };

    public static string BandLabel(PrBand band) => band switch
    {
        PrBand.Bad => "Bad",
        PrBand.BelowAverage => "Below average",
        PrBand.Average => "Average",
        PrBand.Good => "Good",
        PrBand.VeryGood => "Very good",
        PrBand.Great => "Great",
        PrBand.Unicum => "Unicum",
        PrBand.SuperUnicum => "Super unicum",
        _ => "—",
    };
}

/// <summary>Downloads and caches the wows-numbers expected values (refreshed daily).</summary>
public sealed class ExpectedValuesProvider(HttpClient http, KvStore kv, ILogger<ExpectedValuesProvider>? log = null)
{
    public const string SourceUrl = "https://api.wows-numbers.com/personal/rating/expected/json/";
    private const string KvKey = "pr_expected_json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyDictionary<long, ExpectedValues>? _values;
    private DateTimeOffset _loadedAt;

    public DateTimeOffset? DataTime { get; private set; }

    public async Task<IReadOnlyDictionary<long, ExpectedValues>> GetAsync(CancellationToken ct)
    {
        if (_values is not null && DateTimeOffset.UtcNow - _loadedAt < MaxAge) return _values;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_values is not null && DateTimeOffset.UtcNow - _loadedAt < MaxAge) return _values;

            var cached = kv.Get(KvKey);
            if (cached is { } c && DateTimeOffset.UtcNow - c.UpdatedAt < MaxAge && TryParse(c.Value, out var fresh))
                return Remember(fresh, c.UpdatedAt);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (TryParse(json, out var parsed))
                {
                    kv.Set(KvKey, json);
                    _log.LogInformation("PR expected values updated: {Count} ships", parsed.Count);
                    return Remember(parsed, DateTimeOffset.UtcNow);
                }
                _log.LogWarning("PR expected values response could not be parsed");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Could not download PR expected values; using cached copy if any");
            }

            if (cached is { } stale && TryParse(stale.Value, out var old)) return Remember(old, stale.UpdatedAt);
            return _values ?? new Dictionary<long, ExpectedValues>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private IReadOnlyDictionary<long, ExpectedValues> Remember(Dictionary<long, ExpectedValues> v, DateTimeOffset at)
    {
        _values = v;
        _loadedAt = at;
        return v;
    }

    /// <summary>Format: {"time": unix, "data": {"shipId": {average_damage_dealt, average_frags, win_rate} | []}}.</summary>
    internal bool TryParse(string json, out Dictionary<long, ExpectedValues> values)
    {
        values = new Dictionary<long, ExpectedValues>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("time", out var t) && t.TryGetInt64(out var unix))
                DataTime = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in data.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || !long.TryParse(p.Name, out var id)) continue;
                if (p.Value.TryGetProperty("average_damage_dealt", out var d) && d.ValueKind == JsonValueKind.Number &&
                    p.Value.TryGetProperty("average_frags", out var f) && f.ValueKind == JsonValueKind.Number &&
                    p.Value.TryGetProperty("win_rate", out var w) && w.ValueKind == JsonValueKind.Number)
                    values[id] = new ExpectedValues(d.GetDouble(), f.GetDouble(), w.GetDouble()); // ships with nulls are left out
            }
            return values.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
