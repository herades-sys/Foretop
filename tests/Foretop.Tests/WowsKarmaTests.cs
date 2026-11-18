using System.Net;
using System.Net.Http;
using System.Text;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Karma;

namespace Foretop.Tests;

public class WowsKarmaTests
{
    private static Database NewDb() => new(Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db"));

    /// <summary>Answers karmas-full like the real API (unknown IDs left out) and records each request.</summary>
    private sealed class FakeKarmaApi(Func<HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            if (respond is not null) return respond();
            const string json = """
                [{"gameKarma":-3,"platformKarma":0,"performance":2,"teamplay":1,"courtesy":0,"id":500000001,"karma":7}]
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task One_request_for_the_battle_then_the_cache()
    {
        var api = new FakeKarmaApi();
        var client = new WowsKarmaClient(new HttpClient(api), new ApiCache(NewDb()));

        var first = await client.GetAsync(Region.Eu, [500000001, 500000002], CancellationToken.None);

        var (url, body) = Assert.Single(api.Requests);
        Assert.Equal("https://api.wows-karma.com/api/Player/karmas-full", url);
        Assert.Equal("[500000001,500000002]", body);
        Assert.Equal(new KarmaInfo(GameKarma: -3, SiteKarma: 7, Performance: 2, Teamplay: 1, Courtesy: 0), first[500000001]);
        Assert.False(first.ContainsKey(500000002)); // not on WoWS Karma: no karma, never a made-up 0

        var second = await client.GetAsync(Region.Eu, [500000001, 500000002], CancellationToken.None);
        Assert.Single(api.Requests);                // both answered from the cache, the unknown one too
        Assert.Equal(first[500000001], second[500000001]);
        Assert.False(second.ContainsKey(500000002));
    }

    [Fact]
    public async Task Region_picks_the_instance()
    {
        var api = new FakeKarmaApi();
        var client = new WowsKarmaClient(new HttpClient(api), new ApiCache(NewDb()));
        await client.GetAsync(Region.Na, [1000000001], CancellationToken.None);
        await client.GetAsync(Region.Asia, [2000000001], CancellationToken.None);
        Assert.Equal("https://api.na.wows-karma.com/api/Player/karmas-full", api.Requests[0].Url);
        Assert.Equal("https://api.asia.wows-karma.com/api/Player/karmas-full", api.Requests[1].Url);
        Assert.Equal("https://wows-karma.com/player/500000001,Some%20One", WowsKarmaClient.ProfileUrl(Region.Eu, 500000001, "Some One"));
    }

    [Fact]
    public async Task Site_down_gives_no_karma_and_no_error()
    {
        var api = new FakeKarmaApi(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new WowsKarmaClient(new HttpClient(api), new ApiCache(NewDb()));
        var result = await client.GetAsync(Region.Eu, [500000001], CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Site_down_falls_back_to_older_cached_karma()
    {
        var cache = new ApiCache(NewDb());
        var ok = new WowsKarmaClient(new HttpClient(new FakeKarmaApi()), cache);
        await ok.GetAsync(Region.Eu, [500000001], CancellationToken.None);
        cache.Invalidate(); // everything due for a refetch, as if older than six hours

        var down = new WowsKarmaClient(new HttpClient(new FakeKarmaApi(() => new HttpResponseMessage(HttpStatusCode.BadGateway))), cache);
        var result = await down.GetAsync(Region.Eu, [500000001], CancellationToken.None);
        Assert.Equal(-3, result[500000001].GameKarma);
    }
}
