using System.Globalization;
using System.Text.Json;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Stats;

public sealed record PlayerRequest(string Name, int Relation, long ShipId, bool IsBot);

/// <summary>
/// Loads statistics for a set of players, cache first, and publishes each player's state as it
/// changes so the UI can fill rows progressively. Order: IDs → account totals → (clans ‖ ship
/// stats + PR) → recent activity.
/// </summary>
public sealed class PlayerStatsService
{
    private const string KindAccount = "account_info";
    private const string KindShips = "ship_stats";
    private const string KindClan = "clan";
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(1);

    private readonly WgApiClient _api;
    private readonly ApiCache _cache;
    private readonly AccountStore _accounts;
    private readonly SnapshotStore _snapshots;
    private readonly ExpectedValuesProvider _expected;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public PlayerStatsService(WgApiClient api, ApiCache cache, AccountStore accounts, SnapshotStore snapshots,
        ExpectedValuesProvider expected, Func<AppSettings> settings, ILogger<PlayerStatsService>? log = null)
    {
        _api = api;
        _cache = cache;
        _accounts = accounts;
        _snapshots = snapshots;
        _expected = expected;
        _settings = settings;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Ships whose statistics are never shown (ships in testing). Their per-ship results are dropped before anything
    /// is derived from them: no ship row, no current-ship values, and they do not count towards PR.
    /// </summary>
    public Func<long, bool>? IsTestShip { get; set; }

    /// <summary>Tier of a ship (null when unknown); needed for the tier-filtered totals.</summary>
    public Func<long, int?>? ShipTier { get; set; }

    /// <summary>(session key, new state). Raised from background threads.</summary>
    public event Action<string, PlayerSummary>? PlayerUpdated;

    public async Task<IReadOnlyList<PlayerSummary>> LoadAsync(
        string sessionKey, Region region, IReadOnlyList<PlayerRequest> players, RequestPriority priority,
        CancellationToken ct, bool forceRefresh = false)
    {
        var state = new Dictionary<string, PlayerSummary>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        void Set(string name, Func<PlayerSummary, PlayerSummary> change)
        {
            PlayerSummary updated;
            lock (gate)
            {
                updated = change(state[name]);
                state[name] = updated;
            }
            PlayerUpdated?.Invoke(sessionKey, updated);
        }

        foreach (var p in players)
        {
            state[p.Name] = new PlayerSummary
            {
                Name = p.Name, Relation = p.Relation, ShipId = p.ShipId, IsBot = p.IsBot,
                State = p.IsBot ? LoadState.Bot : LoadState.Resolving,
                StatusText = p.IsBot ? "Bot" : "Looking up player…",
            };
            PlayerUpdated?.Invoke(sessionKey, state[p.Name]);
        }

        var humans = players.Where(p => !p.IsBot).ToList();
        if (humans.Count == 0) return Snapshot();

        try
        {
            // 1. Nicknames → account IDs.
            var (ids, offline) = await ResolveIdsAsync(region, humans.Select(h => h.Name).ToList(), priority, ct).ConfigureAwait(false);
            foreach (var h in humans)
            {
                if (ids.TryGetValue(h.Name, out var id))
                    Set(h.Name, s => s with { AccountId = id, State = LoadState.LoadingStats, StatusText = "Loading statistics…" });
                else if (offline)
                    Set(h.Name, s => s with { State = LoadState.Error, StatusText = OfflineText });
                else
                    Set(h.Name, s => s with { State = LoadState.NotFound, StatusText = "Player not found on " + Regions.Label(region) });
            }

            var known = humans.Where(h => ids.ContainsKey(h.Name)).ToList();
            if (known.Count == 0) return Snapshot();

            // 2. Account totals, one batched call for everything not cached.
            var infos = await GetAccountInfosAsync(region, known.Select(h => ids[h.Name]).ToList(), priority, forceRefresh, ct)
                .ConfigureAwait(false);
            foreach (var h in known)
            {
                var id = ids[h.Name];
                if (infos.TryGetValue(id, out var unavailable) && unavailable is { Unavailable: true })
                {
                    Set(h.Name, s => s with { State = LoadState.Error, StatusText = "Statistics unavailable: the Wargaming API is not reachable and this player is not cached" });
                    continue;
                }
                if (!infos.TryGetValue(id, out var info) || info is null)
                {
                    Set(h.Name, s => s with { State = LoadState.NotFound, StatusText = "Account has no data" });
                    continue;
                }

                var overall = StatTotals.From(info.Dto.Statistics?.Pvp);
                RecentStats? r7 = null, r28 = null;
                if (overall is not null)
                {
                    // The API has no recent-stats endpoint any more (account/statsbydate returns
                    // METHOD_NOT_FOUND since at least 2026-11), so recent activity comes from our own snapshots.
                    _snapshots.AddIfChanged(region, id, overall);
                    (r7, r28) = RecentFromSnapshots(_snapshots.GetAll(region, id), overall);
                }
                Set(h.Name, s => s with
                {
                    Recent7 = r7,
                    Recent28 = r28,
                    CreatedAt = FromUnix(info.Dto.CreatedAt),
                    LastBattleAt = FromUnix(info.Dto.LastBattleTime),
                    ServiceLevel = info.Dto.LevelingTier > 0 ? info.Dto.LevelingTier : null,
                    Hidden = info.Dto.HiddenProfile,
                    Overall = overall,
                    Ranked = StatTotals.From(info.Dto.Statistics?.RankSolo),
                    StatsFetchedAt = info.FetchedAt,
                    StaleData = info.Stale,
                    State = info.Dto.HiddenProfile ? LoadState.Hidden : LoadState.LoadingShips,
                    StatusText = info.Dto.HiddenProfile ? "Profile hidden by the player" : "Loading ship statistics…",
                });
            }

            var visible = known.Where(h => infos.TryGetValue(ids[h.Name], out var i) && i is not null && !i.Dto.HiddenProfile).ToList();

            // 3 ‖ 4. Clans (one batched call) in parallel with per-player ship stats.
            var clanTask = LoadClansAsync(region, known.Select(h => ids[h.Name]).ToList(), priority, ct)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        foreach (var h in known)
                        {
                            if (t.Result.TryGetValue(ids[h.Name], out var tag) && tag is not null)
                                Set(h.Name, s => s with { ClanTag = tag });
                        }
                    }
                    else if (t.IsFaulted)
                    {
                        _log.LogWarning(t.Exception?.GetBaseException(), "Clan lookup failed");
                    }
                }, TaskScheduler.Default);

            var expected = await _expected.GetAsync(ct).ConfigureAwait(false);
            var shipTasks = visible.Select(async h =>
            {
                var id = ids[h.Name];
                try
                {
                    var (ships, shipsStale) = await GetShipStatsAsync(region, id, priority, forceRefresh, ct).ConfigureAwait(false);
                    var perf = ships
                        .Select(x => (x.ShipId, Totals: StatTotals.From(x.Pvp), x.LastBattleTime))
                        .Where(x => x.Totals is { Battles: > 0 } && IsTestShip?.Invoke(x.ShipId) != true)
                        .Select(x => new ShipPerformance(x.ShipId, x.Totals!,
                            PersonalRating.Compute([(x.ShipId, x.Totals!)], expected), FromUnix(x.LastBattleTime)))
                        .OrderByDescending(x => x.Totals.Battles)
                        .ToList();
                    var pairs = perf.Select(x => (x.ShipId, x.Totals)).ToList();
                    var pr = PersonalRating.Compute(pairs, expected);
                    var current = perf.FirstOrDefault(x => x.ShipId == h.ShipId);
                    Set(h.Name, s => s with
                    {
                        Ships = perf,
                        ByTier = ShipTier is { } tierOf ? TierFilter.ComputeAll(perf, tierOf, expected) : null,
                        Pr = pr,
                        DamageRatio = PersonalRating.DamageRatio(pairs, expected),
                        CurrentShip = current?.Totals,
                        ShipPr = current?.Pr,
                        ShipDamageRatio = current is null ? null : PersonalRating.DamageRatio([(current.ShipId, current.Totals)], expected),
                        StaleData = s.StaleData || shipsStale,
                        State = LoadState.Ready,
                        StatusText = s.StaleData || shipsStale ? OfflineDataText : null,
                    });
                }
                catch (Exception ex) when (ex is JsonException or WgApiException { Kind: not WgErrorKind.Configuration })
                {
                    _log.LogWarning(ex, "Ship stats for {Name} failed", h.Name);
                    Set(h.Name, s => s with { State = LoadState.Error, StatusText = ex.Message });
                }
            }).ToList();

            await Task.WhenAll(shipTasks).ConfigureAwait(false);
            await clanTask.ConfigureAwait(false);
        }
        catch (WgApiException ex) when (ex.Kind == WgErrorKind.Configuration)
        {
            _log.LogWarning("Stopping load: {Error}", ex.Message);
            foreach (var h in humans)
                Set(h.Name, s => s.State is LoadState.Ready or LoadState.Hidden or LoadState.NotFound
                    ? s
                    : s with { State = LoadState.Error, StatusText = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any other failure (API, unexpected JSON): finish with what we have instead of leaving rows spinning.
            _log.LogWarning(ex, "Stats load failed");
            foreach (var h in humans)
                Set(h.Name, s => s.State is LoadState.Ready or LoadState.Hidden or LoadState.NotFound
                    ? s
                    : s with { State = LoadState.Error, StatusText = ex.Message });
        }

        return Snapshot();

        IReadOnlyList<PlayerSummary> Snapshot()
        {
            lock (gate) return players.Select(p => state[p.Name]).ToList();
        }
    }

    // ------------------------------------------------------------------ steps

    private const string OfflineText = "Wargaming API unreachable and nothing cached for this player";
    private const string OfflineDataText = "Wargaming API unreachable — showing the last cached statistics";

    /// <summary>Nickname → ID. When the API is unreachable, any cached ID (however old) is used and Offline is true.</summary>
    private async Task<(Dictionary<string, long> Ids, bool Offline)> ResolveIdsAsync(
        Region region, List<string> names, RequestPriority priority, CancellationToken ct)
    {
        var s = _settings();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cached = _accounts.Get(region, names);
        var missing = new List<string>();
        foreach (var n in names)
        {
            if (cached.TryGetValue(n, out var c))
            {
                var age = DateTimeOffset.UtcNow - c.ResolvedAt;
                if (c.AccountId is long id && age < TimeSpan.FromDays(s.AccountIdCacheDays)) { result[n] = id; continue; }
                if (c.AccountId is null && age < NotFoundRetry) continue;
            }
            missing.Add(n);
        }

        if (missing.Count == 0) return (result, false);
        Dictionary<string, AccountListEntryDto> found;
        try
        {
            found = await _api.ResolveNicknamesAsync(region, missing, priority, ct).ConfigureAwait(false);
        }
        catch (WgApiException ex) when (ex.Kind == WgErrorKind.Transient)
        {
            _log.LogWarning("Name lookup failed ({Error}); using cached IDs where available", ex.Message);
            foreach (var n in missing)
                if (cached.TryGetValue(n, out var c) && c.AccountId is long old) result[n] = old;
            return (result, true);
        }
        foreach (var n in missing)
        {
            if (found.TryGetValue(n, out var e))
            {
                result[n] = e.AccountId;
                _accounts.Put(region, n, e.AccountId, e.Nickname);
            }
            else
            {
                _accounts.Put(region, n, null, n);
            }
        }
        return (result, false);
    }

    /// <param name="Unavailable">The API could not be reached and nothing was cached: unknown, not "no data".</param>
    private sealed record AccountEntry(AccountInfoDto Dto, DateTimeOffset FetchedAt, bool Stale = false, bool Unavailable = false);

    private async Task<Dictionary<long, AccountEntry?>> GetAccountInfosAsync(
        Region region, List<long> ids, RequestPriority priority, bool force, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromMinutes(_settings().PlayerCacheMinutes);
        var result = new Dictionary<long, AccountEntry?>();
        var missing = new List<long>();
        var stale = new Dictionary<long, CachedJson>();
        foreach (var id in ids)
        {
            var c = _cache.Get(region, KindAccount, Key(id));
            if (!force && c is not null && c.Age < maxAge)
                result[id] = c.Json is null ? null : new AccountEntry(Deserialize<AccountInfoDto>(c.Json), c.FetchedAt);
            else
            {
                missing.Add(id);
                if (c?.Json is not null) stale[id] = c;
            }
        }

        if (missing.Count > 0)
        {
            Dictionary<long, JsonElement?> raw;
            try
            {
                raw = await _api.GetAccountInfoRawAsync(region, missing, priority, ct, extra: "statistics.rank_solo").ConfigureAwait(false);
            }
            catch (WgApiException ex) when (ex.Kind == WgErrorKind.Transient && stale.Count > 0)
            {
                _log.LogWarning("account/info failed ({Error}); showing {Count} cached profiles", ex.Message, stale.Count);
                foreach (var id in missing)
                    result[id] = stale.TryGetValue(id, out var c) ? new AccountEntry(Deserialize<AccountInfoDto>(c.Json!), c.FetchedAt, true)
                        : new AccountEntry(new AccountInfoDto(), DateTimeOffset.UtcNow, Unavailable: true);
                return result;
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var id in missing)
            {
                raw.TryGetValue(id, out var el);
                var json = el?.GetRawText();
                _cache.Put(region, KindAccount, Key(id), json);
                result[id] = json is null ? null : new AccountEntry(Deserialize<AccountInfoDto>(json), now);
            }
        }
        return result;
    }

    private async Task<Dictionary<long, string?>> LoadClansAsync(Region region, List<long> ids, RequestPriority priority, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromHours(_settings().ClanCacheHours);
        var result = new Dictionary<long, string?>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            var c = _cache.Get(region, KindClan, Key(id));
            if (c is not null && c.Age < maxAge) result[id] = ClanTag(c.Json);
            else missing.Add(id);
        }

        if (missing.Count > 0)
        {
            Dictionary<long, JsonElement?> raw;
            try
            {
                raw = await _api.GetClanInfoRawAsync(region, missing, priority, ct).ConfigureAwait(false);
            }
            catch (WgApiException ex) when (ex.Kind == WgErrorKind.Transient)
            {
                foreach (var id in missing) result[id] = ClanTag(_cache.Get(region, KindClan, Key(id))?.Json);
                _log.LogInformation("Clan lookup failed ({Error}); using cached tags", ex.Message);
                return result;
            }
            foreach (var id in missing)
            {
                raw.TryGetValue(id, out var el);
                var json = el?.GetRawText();
                _cache.Put(region, KindClan, Key(id), json);
                result[id] = ClanTag(json);
            }
        }
        return result;

        static string? ClanTag(string? json) =>
            json is null ? null : Deserialize<ClanAccountInfoDto>(json).Clan?.Tag is { Length: > 0 } tag ? tag : null;
    }

    private async Task<(List<ShipStatsDto> Ships, bool Stale)> GetShipStatsAsync(
        Region region, long id, RequestPriority priority, bool force, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromMinutes(_settings().PlayerCacheMinutes);
        var c = _cache.Get(region, KindShips, Key(id));
        string? json;
        var stale = false;
        if (!force && c is not null && c.Age < maxAge)
        {
            json = c.Json;
        }
        else
        {
            try
            {
                var el = await _api.GetShipStatsRawAsync(region, id, priority, ct).ConfigureAwait(false);
                json = el?.GetRawText();
                _cache.Put(region, KindShips, Key(id), json);
            }
            catch (WgApiException ex) when (ex.Kind == WgErrorKind.Transient && c?.Json is not null)
            {
                _log.LogInformation("ships/stats failed for {Id} ({Error}); using cached copy", id, ex.Message);
                json = c.Json;
                stale = true;
            }
        }
        return (json is null ? [] : Deserialize<List<ShipStatsDto>>(json), stale);
    }

    /// <summary>Activity since the newest snapshot at least 7 (and 28) days old.</summary>
    internal static (RecentStats? R7, RecentStats? R28) RecentFromSnapshots(List<PlayerSnapshot> snaps, StatTotals overall)
    {
        var now = DateTimeOffset.UtcNow;
        var valid = snaps.Where(s => s.Totals.Battles <= overall.Battles).ToList();
        var b7 = valid.LastOrDefault(s => s.TakenAt <= now.AddDays(-7));
        var b28 = valid.LastOrDefault(s => s.TakenAt <= now.AddDays(-28)) ?? valid.FirstOrDefault(s => s.TakenAt <= now.AddDays(-8));
        // A baseline far older than the column's span would show months of activity under "7 days" or "28 days":
        // then there is no such value (shown as "—"), not a misleading one.
        if (b7 is not null && b7.TakenAt < now.AddDays(-14)) b7 = null;
        if (b28 is not null && b28.TakenAt < now.AddDays(-56)) b28 = null;
        return (Make(b7), Make(b28));

        RecentStats? Make(PlayerSnapshot? s) =>
            s is null ? null : new RecentStats(overall.Minus(s.Totals), s.TakenAt);
    }

    // ------------------------------------------------------------------ helpers

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, WgJson.Options)!;

    private static DateTimeOffset? FromUnix(long t) => t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
}
