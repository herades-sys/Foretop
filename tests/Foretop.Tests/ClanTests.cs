using Foretop.Core.Api;
using Foretop.Core.Clans;
using Foretop.Core.Data;

namespace Foretop.Tests;

public class ClanTests
{
    private static ClanStore NewStore() =>
        new(new Database(Path.Combine(Path.GetTempPath(), "foretop-tests", Guid.NewGuid() + ".db")));

    private static ClanMember M(long id, string name, string role = "private") =>
        new(id, name, role, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    [Fact]
    public void First_check_has_no_departures_then_missing_members_are_left()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha"), M(2, "Bravo"), M(3, "Charlie")], t0);
        Assert.Empty(store.Left(Region.Eu, 7)); // the API lists current members only: nothing is known to have left yet

        var t1 = t0.AddDays(1);
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha"), M(3, "Charlie"), M(4, "Delta")], t1);
        var left = Assert.Single(store.Left(Region.Eu, 7));
        Assert.Equal("Bravo", left.Name);
        Assert.Equal(t0.ToUnixTimeSeconds(), left.LastSeen.ToUnixTimeSeconds());   // left between the two checks
        Assert.Equal(t1.ToUnixTimeSeconds(), left.Noticed.ToUnixTimeSeconds());

        // A later check does not move the departure, and other clans are separate.
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha"), M(3, "Charlie"), M(4, "Delta")], t1.AddDays(1));
        Assert.Equal(t1.ToUnixTimeSeconds(), Assert.Single(store.Left(Region.Eu, 7)).Noticed.ToUnixTimeSeconds());
        Assert.Empty(store.Left(Region.Na, 7));
    }

    [Fact]
    public void A_member_who_comes_back_is_current_again()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha"), M(2, "Bravo")], t0);
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha")], t0.AddDays(1));
        Assert.Single(store.Left(Region.Eu, 7));
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha"), M(2, "Bravo", "officer")], t0.AddDays(2));
        Assert.Empty(store.Left(Region.Eu, 7));
    }

    [Fact]
    public void Tracking_and_clan_record()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        store.Record(Region.Eu, 7, "TAG", "Clan", [M(1, "Alpha")], t0);
        store.Record(Region.Eu, 7, "NEW", "Renamed", [M(1, "Alpha")], t0.AddHours(1));
        var r = store.Get(Region.Eu, 7)!;
        Assert.Equal("NEW", r.Tag);
        Assert.Equal(t0.ToUnixTimeSeconds(), r.FirstChecked.ToUnixTimeSeconds());
        Assert.False(r.Tracked);
        Assert.Empty(store.Tracked());

        store.SetTracked(Region.Eu, 7, true);
        Assert.Equal(7, Assert.Single(store.Tracked()).ClanId);
    }

    [Fact]
    public void Description_is_plain_text_and_ranks_have_their_game_names()
    {
        Assert.Equal("We aren't the best", ClanService.Description("We aren&#x27;t the best"));
        Assert.Null(ClanService.Description("  "));
        Assert.Equal("Deputy Commander", ClanService.RoleLabel("executive_officer"));
        Assert.Equal("Midshipman", ClanService.RoleLabel("private"));
        Assert.True(ClanService.RoleOrder("commander") < ClanService.RoleOrder("private"));
        Assert.Equal("https://na.wows-numbers.com/clan/1000,A%20B/", ClanService.WowsNumbersUrl(Region.Na, 1000, "A B"));
    }
}
