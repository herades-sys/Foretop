using System.Collections.Concurrent;
using Broadside.Core.Api;
using Broadside.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broadside.Core.Stats;

/// <summary>
/// Ship encyclopedia, kept in SQLite. Refreshed when the game version changes
/// (checked at most every 6 h) or after 7 days.
/// </summary>
public sealed class ShipCatalog(Database db, KvStore kv, WgApiClient api, ILogger<ShipCatalog>? log = null)
{
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<long, ShipInfo> _ships = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loadedFromDb;

    public int Count => _ships.Count;

    public event Action? Updated;

    public ShipInfo? Get(long shipId) => _ships.TryGetValue(shipId, out var s) ? s : null;

    public IEnumerable<ShipInfo> All => _ships.Values;

    /// <summary>Loads from the database, then refreshes from the API when needed. Never throws for API errors.</summary>
    public async Task EnsureLoadedAsync(Region region, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loadedFromDb)
            {
                LoadFromDb();
                _loadedFromDb = true;
                if (_ships.Count > 0) Updated?.Invoke();
            }

            var checkedAt = kv.Get("ships_checked_at");
            var fetchedAt = kv.Get("ships_fetched_at");
            var stale = _ships.Count == 0 || fetchedAt is null || DateTimeOffset.UtcNow - fetchedAt.Value.UpdatedAt > MaxAge;
            var dueCheck = checkedAt is null || DateTimeOffset.UtcNow - checkedAt.Value.UpdatedAt > VersionCheckInterval;
            if (!stale && !dueCheck) return;
            if (!api.HasApplicationId) return;

            var info = await api.GetEncyclopediaInfoAsync(region, RequestPriority.Normal, ct).ConfigureAwait(false);
            kv.Set("ships_checked_at", info.GameVersion ?? "");
            var knownVersion = kv.Get("ships_game_version")?.Value;
            if (!stale && knownVersion == info.GameVersion) return;

            var ships = await api.GetAllShipsAsync(region, RequestPriority.Normal, ct).ConfigureAwait(false);
            if (ships.Count == 0) return;
            Save(ships);
            kv.Set("ships_game_version", info.GameVersion ?? "");
            kv.Set("ships_fetched_at", ships.Count.ToString());
            _log.LogInformation("Ship catalogue updated: {Count} ships, game version {Version}", ships.Count, info.GameVersion);
            Updated?.Invoke();
        }
        catch (Exception ex) when (ex is WgApiException or HttpRequestException)
        {
            _log.LogWarning(ex, "Ship catalogue refresh failed; using {Count} cached ships", _ships.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadFromDb()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ship_id, name, tier, type, nation, is_premium, is_special, image_small, image_contour FROM ships";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var s = new ShipInfo(rd.GetInt64(0), rd.GetString(1), rd.GetInt32(2), ShipInfo.ParseClass(rd.GetString(3)),
                rd.GetString(4), rd.GetInt64(5) != 0, rd.GetInt64(6) != 0,
                rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetString(8));
            _ships[s.ShipId] = s;
        }
    }

    private void Save(List<EncyclopediaShipDto> ships)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var s in ships)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO ships(ship_id, name, tier, type, nation, is_premium, is_special, image_small, image_contour)
                VALUES ($id, $n, $t, $ty, $na, $p, $s, $is, $ic)
                """;
            cmd.Parameters.AddWithValue("$id", s.ShipId);
            cmd.Parameters.AddWithValue("$n", s.Name);
            cmd.Parameters.AddWithValue("$t", s.Tier);
            cmd.Parameters.AddWithValue("$ty", s.Type);
            cmd.Parameters.AddWithValue("$na", s.Nation);
            cmd.Parameters.AddWithValue("$p", s.IsPremium ? 1 : 0);
            cmd.Parameters.AddWithValue("$s", s.IsSpecial ? 1 : 0);
            cmd.Parameters.AddWithValue("$is", (object?)s.Images?.Small ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ic", (object?)s.Images?.Contour ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            _ships[s.ShipId] = new ShipInfo(s.ShipId, s.Name, s.Tier, ShipInfo.ParseClass(s.Type), s.Nation,
                s.IsPremium, s.IsSpecial, s.Images?.Small, s.Images?.Contour);
        }
        tx.Commit();
    }
}
