using System.Net;
using Foretop.Core.Updates;

namespace Foretop.Tests;

public class UpdateCheckerTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github_release_latest.json"));

    [Fact]
    public void Real_release_json_gives_version_assets_and_digests()
    {
        var r = UpdateChecker.ParseRelease(Fixture)!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal("v1.3.0", r.Tag);
        Assert.Equal("Foretop-1.3.0-x64.msi", r.Msi!.Name);
        Assert.Equal("a736e3e9927b2efeaba58f3e1b4c4d67edcdefd6a35b01b48b673c99deb63a43", r.Msi.Sha256);
        Assert.StartsWith("https://github.com/herades-sys/Foretop/releases/download/v1.3.0/", r.Msi.Url);
        Assert.Equal("Foretop-1.3.0-portable-x64.zip", r.Zip!.Name);
        Assert.Contains("Sidebar can collapse", r.Notes);
        Assert.Equal("https://github.com/herades-sys/Foretop/releases/tag/v1.3.0", r.PageUrl);
    }

    [Theory]
    [InlineData("v1.3.0", "1.3.0")]
    [InlineData("1.3", "1.3.0")]
    [InlineData("v2.0.1+abc", "2.0.1")]
    [InlineData("1.4.0-beta", "1.4.0")]
    public void Versions_parse(string text, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(text));

    [Theory]
    [InlineData("1.3.1", "1.3.0", true)]
    [InlineData("1.3.0", "1.3.0+75685a9", false)] // the app's own informational version carries the commit
    [InlineData("1.2.9", "1.3.0", false)]
    [InlineData("2.0.0", "1.10.0", true)]
    public void Newer_is_by_number(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(Version.Parse(candidate), current));

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(UpdateChecker.ParseRelease("""{"tag_name":"v9.0.0","draft":true,"assets":[]}"""));
        Assert.Null(UpdateChecker.ParseRelease("""{"tag_name":"v9.0.0","prerelease":true,"assets":[]}"""));
        Assert.Null(UpdateChecker.ParseRelease("not json"));
    }

    private sealed class Stub(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? Asked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Check_reports_available_up_to_date_and_private()
    {
        var ok = new Stub(HttpStatusCode.OK, Fixture);
        var checker = new UpdateChecker(new HttpClient(ok));
        Assert.Equal(UpdateCheckOutcome.Available, (await checker.CheckAsync("1.2.1")).Outcome);
        Assert.Equal("https://api.github.com/repos/herades-sys/Foretop/releases/latest", ok.Asked!.ToString());
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckAsync("1.3.0")).Outcome);

        var hidden = new UpdateChecker(new HttpClient(new Stub(HttpStatusCode.NotFound, "{}")));
        Assert.Equal(UpdateCheckOutcome.NotReachable, (await hidden.CheckAsync("1.3.0")).Outcome);
    }

    [Theory]
    [InlineData("https://github.com/herades-sys/Foretop/releases/download/v1.6.0/Foretop-1.6.0-x64.msi", "abc", false, null)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2", "abc", false, null)]
    [InlineData("https://github.com/x/y.msi", null, false, "no checksum")]
    [InlineData("https://github.com/x/y.msi", "", false, "no checksum")]
    [InlineData("http://github.com/x/y.msi", "abc", false, "not on GitHub")]
    [InlineData("https://github.com.evil.example/x.msi", "abc", false, "not on GitHub")]
    [InlineData("https://example.com/Foretop.msi", "abc", false, "not on GitHub")]
    [InlineData("file:///C:/temp/Foretop.msi", "abc", false, "local file")]
    [InlineData("file:///C:/temp/Foretop.msi", "abc", true, null)]
    [InlineData("not a url", "abc", false, "not valid")]
    public void Update_downloads_need_a_checksum_and_github(string url, string? sha, bool feed, string? refusal)
    {
        var asset = new ReleaseAsset("Foretop-9.9.9-x64.msi", url, 1, sha);
        if (refusal is null) Assert.NotNull(UpdateChecker.CheckDownload(asset, feed));
        else Assert.Contains(refusal, Assert.Throws<InvalidDataException>(() => UpdateChecker.CheckDownload(asset, feed)).Message);
    }
}
