using System.Text.Json;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Game;
using Foretop.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Foretop.Core.Stats;

/// <summary>
/// Your own result in each battle, from the official API: the account's totals for the battle's mode are read when the
/// battle starts, and again after it ends until the API has counted it; the difference is that battle's damage, win,
/// base XP, frags and survival. Only Random, Ranked and Co-op battles have such totals. Nothing is estimated: a battle the
/// API does not count stays without values.
/// </summary>
public sealed class BattleResultTracker
{
    private static readonly TimeSpan[] PollDelays =
    [
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90),
    ];
    private static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(2);
    /// <summary>A battle you leave early is counted when it ends on the server, up to 20 minutes later.</summary>
    private static readonly TimeSpan PollFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    private readonly WgApiClient _api;
    private readonly AccountStore _accounts;
    private readonly BattleResultStore _store;
    private readonly SnapshotStore _snapshots;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentKey;
    private CancellationTokenSource? _poll;

    public BattleResultTracker(WgApiClient api, AccountStore accounts, BattleResultStore store, SnapshotStore snapshots,
        Func<AppSettings> settings, ILogger<BattleResultTracker> log)
    {
        _api = api;
        _accounts = accounts;
        _store = store;
        _snapshots = snapshots;
        _settings = settings;
        _log = log;
    }

    /// <summary>A result row was added or changed.</summary>
    public event Action? Changed;

    /// <summary>Name, tier and class of a ship (kept with each battle so later rebalances do not change history).</summary>
    public Func<long, (string Name, int Tier, string Class)?>? ShipLookup { get; set; }

    /// <summary>Expected values for the battle rating (wows-numbers); optional.</summary>
    public Func<CancellationToken, Task<IReadOnlyDictionary<long, ExpectedValues>>>? Expected { get; set; }

    public BattleResultStore Store => _store;

    /// <summary>Hook to the watcher. Call once.</summary>
    public void Attach(BattleWatcher watcher)
    {
        watcher.BattleStarted += info => _ = Task.Run(() => OnBattleStartedAsync(info));
        watcher.BattleEnded += () => _ = Task.Run(OnBattleEndedAsync);
    }

    /// <summary>At start-up: expires old open rows and checks the rest once (results may have come in while closed).</summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        var open = _store.AllOpen();
        if (open.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var r in open.Where(r => now - r.StartedAt > GiveUpAfter))
            _store.Upsert(r with { Status = ResultStatus.Expired, ResolvedAt = now });
        // Rows still open here are battles from before this start; a battle still running is re-detected by the watcher
        // and then gets its end time again.
        foreach (var r in _store.AllOpen().Where(r => r.EndedAt is null))
            _store.Upsert(r with { EndedAt = now });
        Changed?.Invoke();
        if (_store.AllOpen().Count > 0) StartPolling();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task OnBattleStartedAsync(ArenaInfo info)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await BattleStartedCoreAsync(info).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Battle result: start of {Key} not recorded: {Error}", info.BattleKey, ex.Message);
        }
    }

    private async Task BattleStartedCoreAsync(ArenaInfo info)
    {
        // A new battle while the previous one is still open means its end was missed (the deletion of
        // tempArenaInfo.json went unseen): it has ended now. Left open, it would hold back every later result.
        if (_currentKey is { } previous && previous != info.BattleKey && _store.Get(previous) is { EndedAt: null, IsOpen: true } open)
            _store.Upsert(open with { EndedAt = DateTimeOffset.UtcNow });
        _currentKey = info.BattleKey;
        var self = info.Self;
        if (self is null || string.IsNullOrWhiteSpace(info.PlayerName))
        {
            _log.LogInformation("Battle result: {Key} not tracked (no own ship or player name in the roster)", info.BattleKey);
            return;
        }

        var existing = _store.Get(info.BattleKey);
        if (existing is not null)
        {
            // Same battle seen again (Foretop restarted mid-battle): keep the baseline taken at its start.
            if (existing.EndedAt is not null && existing.IsOpen) _store.Upsert(existing with { EndedAt = null });
            return;
        }

        var region = _settings().Region;
        var started = info.StartedAt is { } s ? new DateTimeOffset(s) : DateTimeOffset.UtcNow;
        var mode = ResultModes.FromMatchGroup(info.MatchGroup);
        var accountId = await ResolveAccountAsync(region, info.PlayerName!).ConfigureAwait(false);
        if (accountId is null)
        {
            _log.LogWarning("Battle result: account {Name} not found on {Region}", info.PlayerName, region);
            return;
        }

        if (mode is null)
        {
            _store.Upsert(new BattleResult(info.BattleKey, region, accountId.Value, info.MatchGroup, self.ShipId, started, null,
                ResultStatus.Unavailable, null, null, null));
            return;
        }

        var stats = await FetchAsync(region, accountId.Value, retries: 3).ConfigureAwait(false);
        if (stats is not null) await ApplyTotalsAsync(region, accountId.Value, stats).ConfigureAwait(false);
        var baseline = ResultModes.Pick(stats, mode);
        var ship = ShipLookup?.Invoke(self.ShipId);
        var extras = new Personal.PersonalExtras
        {
            Map = info.MapName,
            MatchGroup = info.MatchGroup,
            ShipName = ship?.Name,
            ShipTier = ship?.Tier,
            ShipClass = ship?.Class,
            ShipBaseline = baseline is null ? null : await FetchShipAsync(region, accountId.Value, self.ShipId, mode).ConfigureAwait(false),
        };
        _store.Upsert(new BattleResult(info.BattleKey, region, accountId.Value, mode, self.ShipId, started, null,
            baseline is null ? ResultStatus.Unavailable : ResultStatus.Pending, baseline, null, null, extras));
        _log.LogInformation("Battle result: {Key} ({Mode}) baseline {Battles} battles", info.BattleKey, mode, baseline?.Battles);
    }

    private async Task OnBattleEndedAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_currentKey is { } key && _store.Get(key) is { EndedAt: null } r)
                    _store.Upsert(r with { EndedAt = DateTimeOffset.UtcNow });
                _currentKey = null;
            }
            finally
            {
                _gate.Release();
            }
            Changed?.Invoke();
            StartPolling();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Battle result: end not recorded: {Error}", ex.Message);
        }
    }

    private void StartPolling()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _poll, cts)?.Cancel();
        _ = Task.Run(() => PollAsync(cts.Token));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + PollFor;
        for (var i = 0; DateTimeOffset.UtcNow < until; i++)
        {
            try
            {
                await Task.Delay(i < PollDelays.Length ? PollDelays[i] : PollEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (!await CheckOpenAsync(ct).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Battle result: checking open battles failed; trying again at the next interval");
            }
        }
    }

    /// <summary>Fetches totals for every account with open battles that have ended. False when nothing is left to wait for.</summary>
    private async Task<bool> CheckOpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            var waiting = _store.AllOpen().Where(r => r.EndedAt is not null).GroupBy(r => (r.Region, r.AccountId)).ToList();
            if (waiting.Count == 0) return false;
            foreach (var g in waiting)
            {
                var stats = await FetchAsync(g.Key.Region, g.Key.AccountId, retries: 0).ConfigureAwait(false);
                if (stats is not null) changed |= await ApplyTotalsAsync(g.Key.Region, g.Key.AccountId, stats).ConfigureAwait(false);
            }
            return _store.AllOpen().Any(r => r.EndedAt is not null);
        }
        finally
        {
            _gate.Release();
            // Only when a result changed: every listener reloads (Dashboard, My performance, replay enrichment).
            if (changed) Changed?.Invoke();
        }
    }

    /// <summary>Runs <see cref="ResultMath.Apply"/> for each mode with open battles of this account.</summary>
    /// <returns>True when a battle's status changed (counted, combined, merged…).</returns>
    private async Task<bool> ApplyTotalsAsync(Region region, long accountId, AccountStatisticsDto stats)
    {
        if (StatTotals.From(stats.Pvp) is { } pvp) _snapshots.AddIfChanged(region, accountId, pvp);
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var mode in _store.Open(region, accountId).GroupBy(r => r.Mode))
        {
            if (ResultModes.Pick(stats, mode.Key) is not { } totals) continue;
            var before = mode.GroupBy(x => x.BattleKey).ToDictionary(g => g.Key, g => g.First().Status);
            foreach (var applied in ResultMath.Apply(mode.ToList(), totals, now))
            {
                if (!before.TryGetValue(applied.BattleKey, out var was) || was != applied.Status) changed = true;
                var r = applied.Status is ResultStatus.Ok or ResultStatus.Combined && applied.ResolvedAt == now
                    ? await CompleteAsync(applied).ConfigureAwait(false)
                    : applied;
                _store.Upsert(r);
                if (r.Status is ResultStatus.Ok or ResultStatus.Combined)
                    _log.LogInformation("Battle result: {Key} {Status}: {Battles} battle(s), {Damage} damage, {Wins} win(s)",
                        r.BattleKey, r.Status, r.Result!.Battles, r.Result.Damage, r.Result.Wins);
            }
        }
        return changed;
    }

    /// <summary>
    /// When a battle is counted: the played ship's combat difference (exactly one battle more on that ship, else not
    /// used) and the battle rating from the expected values of that moment. Both are kept with the row.
    /// </summary>
    private async Task<BattleResult> CompleteAsync(BattleResult r)
    {
        var x = r.Extras ?? new Personal.PersonalExtras();
        if (r.Status == ResultStatus.Combined)
            return r with { Extras = x with { ShipDeltaNote = "several battles were counted at once", ShipBaseline = null } };
        if (x.ShipBaseline is { } before && x.ShipDelta is null)
        {
            var after = await FetchShipAsync(r.Region, r.AccountId, r.ShipId, r.Mode).ConfigureAwait(false);
            var delta = after?.Minus(before);
            x = delta switch
            {
                { Battles: 1 } => x with { ShipDelta = delta },
                null => x with { ShipDeltaNote = "ship statistics could not be fetched" },
                _ => x with { ShipDeltaNote = $"the ship's totals changed by {delta.Battles} battles" },
            };
        }
        if (r.Result is { Battles: 1 } one && Expected is not null)
        {
            try
            {
                var expected = await Expected(CancellationToken.None).ConfigureAwait(false);
                if (expected.TryGetValue(r.ShipId, out var e))
                    x = x with
                    {
                        Rating = PersonalRating.Compute([(r.ShipId, one)], expected),
                        RatingExpected = [e.AvgDamage, e.AvgFrags, e.WinRate],
                    };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _log.LogInformation("Battle rating not calculated: {Error}", ex.Message);
            }
        }
        return r with { Extras = x };
    }

    /// <summary>The played ship's combat totals in the battle's mode; null when hidden, unreachable or never played.</summary>
    private async Task<Personal.ShipCombatTotals?> FetchShipAsync(Region region, long accountId, long shipId, string mode)
    {
        try
        {
            var raw = await _api.GetShipCombatRawAsync(region, accountId, shipId, mode, RequestPriority.High, CancellationToken.None)
                .ConfigureAwait(false);
            return raw is { } j ? Personal.ShipCombatTotals.From(j, mode) : null;
        }
        catch (Exception ex) when (ex is WgApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogInformation("Ship combat totals not fetched: {Error}", ex.Message);
            return null;
        }
    }

    private async Task<long?> ResolveAccountAsync(Region region, string name)
    {
        if (_accounts.Get(region, [name]).TryGetValue(name, out var cached) && cached.AccountId is { } id) return id;
        var found = await _api.ResolveNicknamesAsync(region, [name], RequestPriority.High, CancellationToken.None).ConfigureAwait(false);
        if (!found.TryGetValue(name, out var e)) return null;
        _accounts.Put(region, name, e.AccountId, e.Nickname);
        return e.AccountId;
    }

    /// <summary>Fresh account totals (never from the cache). Null when the profile is hidden or the API is unreachable.</summary>
    private async Task<AccountStatisticsDto?> FetchAsync(Region region, long accountId, int retries)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var raw = await _api.GetAccountInfoRawAsync(region, [accountId], RequestPriority.High, CancellationToken.None,
                    extra: "statistics.rank_solo,statistics.pve").ConfigureAwait(false);
                if (!raw.TryGetValue(accountId, out var json) || json is null) return null;
                var dto = json.Value.Deserialize<AccountInfoDto>(WgJson.Options);
                return dto is null || dto.HiddenProfile ? null : dto.Statistics;
            }
            catch (Exception ex) when (ex is WgApiException or HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt >= retries)
                {
                    _log.LogWarning("Battle result: account totals not fetched: {Error}", ex.Message);
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }
}
