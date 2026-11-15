using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Foretop.Core.Updates;

/// <summary>A file of a release. Sha256 is GitHub's digest of the upload (lower-case hex), when it has one.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

public sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, string PageUrl,
    DateTimeOffset? PublishedAt, ReleaseAsset? Msi, ReleaseAsset? Zip);

public enum UpdateCheckOutcome { UpToDate, Available, NotReachable, Failed }

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string Message);

/// <summary>
/// Asks GitHub for the latest published release of Foretop (drafts and pre-releases are never "latest").
/// Unauthenticated: works only while the repository is public; GitHub allows 60 such requests an hour per IP.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "herades-sys/Foretop";

    public string Repository { get; } = repository;
    public string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>For development: read the release JSON from this file instead of GitHub.</summary>
    public string? FeedFile { get; set; }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        string json;
        try
        {
            if (FeedFile is not null)
            {
                json = await File.ReadAllTextAsync(FeedFile, ct);
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var resp = await http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new(UpdateCheckOutcome.NotReachable, null,
                        "No public release found on GitHub (the repository is private or has no release yet).");
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    return new(UpdateCheckOutcome.Failed, null, "GitHub limits update checks for a while; Foretop tries again later.");
                if (!resp.IsSuccessStatusCode)
                    return new(UpdateCheckOutcome.Failed, null, $"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
                json = await resp.Content.ReadAsStringAsync(ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(UpdateCheckOutcome.Failed, null, "GitHub could not be reached: " + ex.Message);
        }

        var release = ParseRelease(json);
        if (release is null) return new(UpdateCheckOutcome.Failed, null, "GitHub's answer could not be read.");
        return IsNewer(release.Version, currentVersion)
            ? new(UpdateCheckOutcome.Available, release, $"Foretop {release.Version.ToString(3)} is available.")
            : new(UpdateCheckOutcome.UpToDate, release, "Foretop is up to date.");
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            var tag = S(r, "tag_name");
            if (ParseVersion(tag) is not { } version) return null;

            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    var digest = S(a, "digest");
                    assets.Add(new ReleaseAsset(S(a, "name"), S(a, "browser_download_url"),
                        a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : 0,
                        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
                }
            DateTimeOffset? published = DateTimeOffset.TryParse(S(r, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var pub) ? pub : null;
            var title = S(r, "name");
            return new ReleaseInfo(version, tag, title.Length > 0 ? title : "Foretop " + version.ToString(3), S(r, "body"), S(r, "html_url"),
                published,
                assets.FirstOrDefault(a => a.Name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase)),
                assets.FirstOrDefault(a => a.Name.EndsWith("-portable-x64.zip", StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.3.0", "1.3", "1.3.0+abc" or "1.3.0-beta" → 1.3.0 (build metadata and suffixes ignored).</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        var end = t.IndexOfAny(['+', '-', ' ']);
        if (end >= 0) t = t[..end];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Version candidate, string current) =>
        ParseVersion(current) is not { } now || candidate > now;
}
