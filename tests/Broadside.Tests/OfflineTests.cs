using System.Net;
using Broadside.Core.Api;
using Broadside.Core.Data;
using Broadside.Core.Settings;
using Broadside.Core.Stats;

namespace Broadside.Tests;

public class OfflineTests
{
    /// <summary>Wargaming API answers 503 to everything; the PR feed works.</summary>
    private sealed class ApiDownHandler : HttpMessageHandler
    {
        public int ApiCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host.Contains("wows-numbers"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"time":1,"data":{"100":{"average_damage_dealt":50000,"average_frags":1.0,"win_rate":50.0}}}"""),
                });
            Interlocked.Increment(ref ApiCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Api_outage_falls_back_to_stale_cache()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "broadside-tests", Guid.NewGuid() + ".db"));
        var cache = new ApiCache(db);
        var accounts = new AccountStore(db);
        accounts.Put(Region.Eu, "Cached_Player", 42, "Cached_Player");
        cache.Put(Region.Eu, "account_info", "42", """
            {"account_id":42,"nickname":"Cached_Player","created_at":1500000000,"last_battle_time":1790000000,"hidden_profile":false,
             "leveling_tier":10,"statistics":{"battles":300,"pvp":{"battles":300,"wins":165,"losses":135,"damage_dealt":15000000,"xp":400000,
             "frags":300,"survived_battles":100,"planes_killed":20}}}
            """);
        cache.Put(Region.Eu, "ship_stats", "42", """
            [{"ship_id":100,"battles":300,"last_battle_time":1790000000,"pvp":{"battles":300,"wins":165,"damage_dealt":15000000,"frags":300,"xp":400000}}]
            """);
        cache.Invalidate(); // everything is stale, so the service must try the API first

        var handler = new ApiDownHandler();
        using var http = new HttpClient(handler);
        using var scheduler = new RequestScheduler(new RateLimiter(100));
        var api = new WgApiClient(http, scheduler, () => "test-app-id");
        var service = new PlayerStatsService(api, cache, accounts, new SnapshotStore(db),
            new ExpectedValuesProvider(http, new KvStore(db)), () => new AppSettings());

        var result = await service.LoadAsync("s", Region.Eu,
            [new PlayerRequest("Cached_Player", 1, 100, false), new PlayerRequest("Never_Seen", 2, 100, false)],
            RequestPriority.High, CancellationToken.None);

        var cached = result.Single(p => p.Name == "Cached_Player");
        Assert.Equal(LoadState.Ready, cached.State);
        Assert.True(cached.StaleData);
        Assert.Equal(300, cached.Overall!.Battles);
        Assert.NotNull(cached.Pr);
        Assert.Contains("unreachable", cached.StatusText);

        var unknown = result.Single(p => p.Name == "Never_Seen");
        Assert.Equal(LoadState.Error, unknown.State);
        Assert.Contains("unreachable", unknown.StatusText);
        Assert.True(handler.ApiCalls >= 4); // it did retry before giving up
    }

    [Fact]
    public void Compressed_cache_round_trips_and_prunes()
    {
        var db = new Database(Path.Combine(Path.GetTempPath(), "broadside-tests", Guid.NewGuid() + ".db"));
        var cache = new ApiCache(db);
        var big = "[" + string.Join(",", Enumerable.Range(0, 300).Select(i =>
            "{\"ship_id\":" + i + ",\"battles\":" + i * 3 + ",\"pvp\":{\"wins\":" + i + "}}")) + "]";
        cache.Put(Region.Eu, "ship_stats", "1", big);
        Assert.Equal(big, cache.Get(Region.Eu, "ship_stats", "1")!.Json);
        Assert.True(cache.SizeBytes() < big.Length / 3, $"{cache.SizeBytes()} bytes stored for {big.Length}");

        cache.Invalidate();
        Assert.Equal(0, cache.Prune(TimeSpan.FromDays(30))); // invalidated is not old
        Assert.Equal(1, cache.Prune(TimeSpan.FromSeconds(-5))); // everything older than "5 s in the future"
        Assert.Null(cache.Get(Region.Eu, "ship_stats", "1"));
    }
}
