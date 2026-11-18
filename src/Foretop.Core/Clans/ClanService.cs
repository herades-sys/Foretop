using System.Globalization;
using System.Text.Json;
using Foretop.Core.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Clans;

/// <summary>A search hit.</summary>
public sealed record ClanListing(long ClanId, string Tag, string Name, int Members, DateTimeOffset? CreatedAt);

/// <summary>The clan as the API describes it now.</summary>
public sealed record ClanInfo(long ClanId, string Tag, string Name, string? Description, DateTimeOffset? CreatedAt, int MembersCount,
    string? LeaderName, string? CreatorName, string? OldTag, string? OldName, DateTimeOffset? RenamedAt, bool Disbanded);

/// <summary>Random-battle activity of a member (official account totals); all null when the profile is hidden.</summary>
public sealed record MemberStats(int? Battles, double? WinRate, DateTimeOffset? LastBattle, bool Hidden);

/// <summary>Everything the clan page shows.</summary>
public sealed record ClanView(Region Region, ClanInfo Info, IReadOnlyList<ClanMember> Members,
    IReadOnlyDictionary<long, MemberStats> Stats, IReadOnlyList<LeftMember> Left, ClanRecord? Record);

/// <summary>Clan search, clan details with members, and the member history Foretop records (see <see cref="ClanStore"/>).</summary>
public sealed class ClanService(WgApiClient api, ClanStore store, ILogger<ClanService>? log = null)
{
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    /// <summary>How often tracked clans are checked again in the background.</summary>
    public static readonly TimeSpan TrackInterval = TimeSpan.FromHours(6);

    public ClanStore Store => store;

    /// <summary>Clans whose tag or name starts with the text (the API needs at least two characters).</summary>
    public async Task<List<ClanListing>> SearchAsync(Region region, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length < 2) return [];
        var r = await api.GetAsync(region, "clans/list", new()
        {
            ["search"] = text,
            ["limit"] = "30",
            ["fields"] = "clan_id,tag,name,members_count,created_at",
        }, RequestPriority.High, ct).ConfigureAwait(false);
        var list = new List<ClanListing>();
        if (r.Data.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in r.Data.EnumerateArray())
            list.Add(new ClanListing(Long(e, "clan_id") ?? 0, Str(e, "tag") ?? "", Str(e, "name") ?? "",
                (int)(Long(e, "members_count") ?? 0), Time(e, "created_at")));
        // An exact tag first: typing "BERS" should find [BERS] before clans that only start with it.
        return list.Where(c => c.ClanId > 0)
            .OrderByDescending(c => string.Equals(c.Tag, text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The clan with exactly this tag, if the server has one.</summary>
    public async Task<ClanListing?> FindByTagAsync(Region region, string tag, CancellationToken ct) =>
        (await SearchAsync(region, tag.Trim('[', ']', ' '), ct).ConfigureAwait(false))
        .FirstOrDefault(c => string.Equals(c.Tag, tag.Trim('[', ']', ' '), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The clan now, with its members. The member list is recorded, so members missing since the previous check show
    /// as left. With <paramref name="withStats"/>, each member's Random battles, win rate and last battle (one call).
    /// Null when the server has no such clan.
    /// </summary>
    public async Task<ClanView?> LoadAsync(Region region, long clanId, bool withStats, CancellationToken ct)
    {
        var r = await api.GetAsync(region, "clans/info", new()
        {
            ["clan_id"] = clanId.ToString(CultureInfo.InvariantCulture),
            ["extra"] = "members",
        }, RequestPriority.High, ct).ConfigureAwait(false);
        if (r.Data.ValueKind != JsonValueKind.Object || !r.Data.TryGetProperty(clanId.ToString(CultureInfo.InvariantCulture), out var c)
            || c.ValueKind != JsonValueKind.Object)
            return null;

        var info = new ClanInfo(clanId, Str(c, "tag") ?? "", Str(c, "name") ?? "", Description(Str(c, "description")), Time(c, "created_at"),
            (int)(Long(c, "members_count") ?? 0), Str(c, "leader_name"), Str(c, "creator_name"), Str(c, "old_tag"), Str(c, "old_name"),
            Time(c, "renamed_at"), c.TryGetProperty("is_clan_disbanded", out var d) && d.ValueKind == JsonValueKind.True);

        var members = new List<ClanMember>();
        if (c.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Object)
            foreach (var p in ms.EnumerateObject())
                if (Long(p.Value, "account_id") is long id)
                    members.Add(new ClanMember(id, Str(p.Value, "account_name") ?? "", Str(p.Value, "role") ?? "", Time(p.Value, "joined_at")));

        store.Record(region, clanId, info.Tag, info.Name, members);
        var stats = withStats ? await MemberStatsAsync(region, members.Select(m => m.AccountId).ToList(), ct).ConfigureAwait(false)
            : new Dictionary<long, MemberStats>();
        return new ClanView(region, info, members.OrderBy(m => RoleOrder(m.Role)).ThenBy(m => m.JoinedAt).ToList(), stats,
            store.Left(region, clanId), store.Get(region, clanId));
    }

    /// <summary>Checks every tracked clan whose last check is older than <see cref="TrackInterval"/>.</summary>
    public async Task<int> CheckTrackedAsync(CancellationToken ct)
    {
        var done = 0;
        foreach (var clan in store.Tracked().Where(t => DateTimeOffset.UtcNow - t.LastChecked >= TrackInterval))
        {
            try
            {
                await LoadAsync(clan.Region, clan.ClanId, withStats: false, ct).ConfigureAwait(false);
                done++;
            }
            catch (WgApiException ex)
            {
                _log.LogWarning("Tracked clan [{Tag}] not checked: {Error}", clan.Tag, ex.Message);
                if (ex.Kind == WgErrorKind.Configuration) break;
            }
        }
        if (done > 0) _log.LogInformation("Tracked clans checked: {Count}", done);
        return done;
    }

    private async Task<Dictionary<long, MemberStats>> MemberStatsAsync(Region region, List<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, MemberStats>();
        foreach (var chunk in ids.Chunk(100))
        {
            var r = await api.GetAsync(region, "account/info", new()
            {
                ["account_id"] = string.Join(',', chunk),
                ["fields"] = "account_id,hidden_profile,last_battle_time,statistics.pvp.battles,statistics.pvp.wins",
            }, RequestPriority.Normal, ct).ConfigureAwait(false);
            if (r.Data.ValueKind != JsonValueKind.Object) continue;
            foreach (var id in chunk)
            {
                if (!r.Data.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var a) || a.ValueKind != JsonValueKind.Object) continue;
                var hidden = a.TryGetProperty("hidden_profile", out var h) && h.ValueKind == JsonValueKind.True;
                int? battles = null, wins = null;
                if (a.TryGetProperty("statistics", out var s) && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("pvp", out var pvp) && pvp.ValueKind == JsonValueKind.Object)
                {
                    battles = (int?)Long(pvp, "battles");
                    wins = (int?)Long(pvp, "wins");
                }
                result[id] = new MemberStats(battles, battles > 0 && wins is int w ? 100.0 * w / battles.Value : null,
                    Time(a, "last_battle_time"), hidden);
            }
        }
        return result;
    }

    /// <summary>In-game rank names, highest first.</summary>
    public static string RoleLabel(string role) => role switch
    {
        "commander" => "Commander",
        "executive_officer" => "Deputy Commander",
        "recruitment_officer" => "Recruitment Officer",
        "commissioned_officer" => "Commissioned Officer",
        "officer" => "Line Officer",
        "private" => "Midshipman",
        _ => role,
    };

    public static int RoleOrder(string role) => role switch
    {
        "commander" => 0, "executive_officer" => 1, "recruitment_officer" => 2, "commissioned_officer" => 3, "officer" => 4,
        "private" => 5, _ => 6,
    };

    /// <summary>The clan on wows-numbers.com (same address pattern as its player pages).</summary>
    public static string WowsNumbersUrl(Region r, long clanId, string tag)
    {
        var sub = r switch { Region.Eu => "", Region.Na => "na.", Region.Asia => "asia.", _ => "" };
        return $"https://{sub}wows-numbers.com/clan/{clanId},{Uri.EscapeDataString(tag)}/";
    }

    /// <summary>The API sends the description HTML-encoded ("aren&amp;#x27;t"); shown as plain text.</summary>
    internal static string? Description(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : System.Net.WebUtility.HtmlDecode(raw).Trim();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Long(e, name) is long t and > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
}
