using System.Collections.Concurrent;
using System.Diagnostics;
using Broadside.Core.Api;
using Broadside.Core.Data;
using Broadside.Core.Stats;

namespace Broadside.Tests;

public class InfrastructureTests
{
    [Fact]
    public async Task Rate_limiter_spaces_requests()
    {
        var limiter = new RateLimiter(20); // 50 ms apart
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++) await limiter.WaitAsync(CancellationToken.None);
        // first slot is immediate, then 5 × 50 ms
        Assert.InRange(sw.ElapsedMilliseconds, 220, 1000);
    }

    [Fact]
    public async Task Scheduler_runs_high_priority_first()
    {
        using var scheduler = new RequestScheduler(new RateLimiter(50), maxConcurrency: 1);
        var order = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        // Occupy the single slot so the rest queue up.
        var blocker = scheduler.Enqueue(async _ => { await gate.Task; return 0; }, RequestPriority.Normal, CancellationToken.None);
        await Task.Delay(50);

        var tasks = new List<Task<int>>
        {
            scheduler.Enqueue(_ => { order.Enqueue("low"); return Task.FromResult(1); }, RequestPriority.Low, CancellationToken.None),
            scheduler.Enqueue(_ => { order.Enqueue("normal"); return Task.FromResult(2); }, RequestPriority.Normal, CancellationToken.None),
            scheduler.Enqueue(_ => { order.Enqueue("high"); return Task.FromResult(3); }, RequestPriority.High, CancellationToken.None),
        };
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(tasks.Append(blocker));

        Assert.Equal(["high", "normal", "low"], order.ToArray());
    }

    [Fact]
    public async Task Scheduler_skips_cancelled_items()
    {
        using var scheduler = new RequestScheduler(new RateLimiter(50), maxConcurrency: 1);
        var gate = new TaskCompletionSource();
        var blocker = scheduler.Enqueue(async _ => { await gate.Task; return 0; }, RequestPriority.High, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var ran = false;
        var cancelled = scheduler.Enqueue(_ => { ran = true; return Task.FromResult(1); }, RequestPriority.Low, cts.Token);
        cts.Cancel();
        gate.SetResult();
        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(ran);
    }

    [Theory]
    [InlineData(407, "REQUEST_LIMIT_EXCEEDED", WgErrorKind.Transient)]
    [InlineData(504, "SOURCE_NOT_AVAILABLE", WgErrorKind.Transient)]
    [InlineData(407, "INVALID_APPLICATION_ID", WgErrorKind.Configuration)]
    [InlineData(407, "DEMO_APPLICATION_IS_BLOCKED", WgErrorKind.Configuration)]
    [InlineData(407, "INVALID_SEARCH", WgErrorKind.Request)]
    [InlineData(402, "SEARCH_NOT_SPECIFIED", WgErrorKind.Request)]
    public void Classifies_api_errors(int code, string message, WgErrorKind kind) =>
        Assert.Equal(kind, WgApiException.FromApiError(code, message, null).Kind);

    [Fact]
    public void Hidden_profile_nulls_deserialize()
    {
        // Shape returned by the live API for a hidden profile (2026-11-14).
        const string json = """
            {"account_id": 5, "nickname": "X", "created_at": 1500000000, "last_battle_time": 1790000000,
             "logout_at": null, "hidden_profile": true, "leveling_tier": null, "statistics": null}
            """;
        var dto = System.Text.Json.JsonSerializer.Deserialize<AccountInfoDto>(json, WgJson.Options)!;
        Assert.True(dto.HiddenProfile);
        Assert.Equal(0, dto.LevelingTier);
        Assert.Null(dto.Statistics);
    }

    private static Database NewDb() =>
        new(Path.Combine(Path.GetTempPath(), "broadside-tests", Guid.NewGuid() + ".db"));

    [Fact]
    public void Cache_round_trip_and_invalidate()
    {
        var cache = new ApiCache(NewDb());
        Assert.Null(cache.Get(Region.Eu, "k", "1"));
        cache.Put(Region.Eu, "k", "1", "{\"a\":1}");
        cache.Put(Region.Eu, "k", "2", null);
        var hit = cache.Get(Region.Eu, "k", "1")!;
        Assert.Equal("{\"a\":1}", hit.Json);
        Assert.True(hit.Age < TimeSpan.FromMinutes(1));
        Assert.Null(cache.Get(Region.Eu, "k", "2")!.Json); // cached "no data"
        Assert.Null(cache.Get(Region.Na, "k", "1"));        // regions are separate
        cache.Invalidate();
        Assert.True(cache.Get(Region.Eu, "k", "1")!.Age > TimeSpan.FromDays(365));
    }

    [Fact]
    public void Account_store_is_case_insensitive()
    {
        var store = new AccountStore(NewDb());
        store.Put(Region.Eu, "SomeCaptain", 123, "SomeCaptain");
        store.Put(Region.Eu, "Ghost", null, "Ghost");
        var got = store.Get(Region.Eu, ["somecaptain", "GHOST", "nobody"]);
        Assert.Equal(123, got["somecaptain"].AccountId);
        Assert.Null(got["GHOST"].AccountId);
        Assert.False(got.ContainsKey("nobody"));
    }

    [Fact]
    public void Snapshots_only_added_when_battles_change()
    {
        var store = new SnapshotStore(NewDb());
        var t = new StatTotals(100, 50, 50, 0, 1, 1, 1, 1, 1);
        store.AddIfChanged(Region.Eu, 7, t);
        store.AddIfChanged(Region.Eu, 7, t);
        Assert.Single(store.GetAll(Region.Eu, 7));
    }

    [Fact]
    public void Battle_history_upsert_keeps_one_row_per_battle()
    {
        var store = new BattleHistoryStore(NewDb());
        store.Upsert("abc", Region.Eu, DateTime.Now, "pvp", "Neighbors", "Me", "{}", null);
        store.Upsert("abc", Region.Eu, DateTime.Now, "pvp", "Neighbors", "Me", "{}", "{\"x\":1}");
        var rows = store.List();
        Assert.Single(rows);
        Assert.Equal("{\"x\":1}", rows[0].SummaryJson);
    }
}
