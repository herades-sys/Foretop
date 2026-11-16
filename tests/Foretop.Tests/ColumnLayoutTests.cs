using Foretop.Core.Layout;

namespace Foretop.Tests;

public class ColumnLayoutTests
{
    // The Live Battle table: player and ship flex; BATTLES and WR stay; the rest hide 7 DAYS, XP, SHIP WR, PR, DMG.
    private static List<LayoutColumn> Table(double player = 180, double ship = 200, IReadOnlyList<double>? playerCells = null,
        IReadOnlyList<double>? shipCells = null, bool recent = true) =>
    [
        new(player, playerCells ?? [player], Flex: 1, MinWidth: 110),
        new(ship, shipCells ?? [ship], Flex: 1, MinWidth: 130),
        new(60, [60]),                      // BATTLES
        new(55, [55]),                      // WR
        new(60, [60], HidePriority: 3),     // DMG
        new(50, [50], HidePriority: 6),     // XP
        new(50, [50], HidePriority: 4),     // PR
        new(80, [80], HidePriority: 5),     // SHIP WR
        new(70, [70], HidePriority: 7, Enabled: recent), // 7 DAYS
    ];

    [Fact]
    public void Everything_fits_and_spare_width_goes_to_the_name_columns()
    {
        var cols = Table();
        var need = cols.Sum(c => c.Need);
        var r = ColumnLayout.Solve(cols, need + 100);
        Assert.All(r.Visible, Assert.True);
        Assert.Empty(r.Hidden);
        Assert.Equal(230, r.Widths[0]);
        Assert.Equal(250, r.Widths[1]);
        Assert.Equal(60, r.Widths[2]);
        Assert.Equal(need + 100, r.Widths.Sum(), 6);
    }

    [Fact]
    public void Columns_are_hidden_by_priority_and_only_as_many_as_needed()
    {
        var cols = Table();
        var all = cols.Sum(c => c.Need); // 805
        var r = ColumnLayout.Solve(cols, all - 100);   // dropping 7 DAYS (70) is not enough, XP (50) too: 120 ≥ 100
        Assert.Equal([5, 8], r.Hidden);
        Assert.False(r.Visible[5]);
        Assert.False(r.Visible[8]);
        Assert.True(r.Visible[7]);                      // SHIP WR stays
        Assert.True(r.Widths.Sum() <= all - 100 + 1e-6);
    }

    [Fact]
    public void Player_ship_battles_and_win_rate_are_never_hidden()
    {
        var r = ColumnLayout.Solve(Table(), 200);
        Assert.Equal([4, 5, 6, 7, 8], r.Hidden);
        Assert.True(r.Visible[0] && r.Visible[1] && r.Visible[2] && r.Visible[3]);
    }

    [Fact]
    public void A_column_the_view_turned_off_is_not_shown_and_not_reported_as_hidden()
    {
        var r = ColumnLayout.Solve(Table(recent: false), 5000);
        Assert.False(r.Visible[8]);
        Assert.Equal(0, r.Widths[8]);
        Assert.Empty(r.Hidden);
    }

    [Fact]
    public void Squeezed_names_are_split_so_the_fewest_are_cut()
    {
        // One extreme player name (300) and four ship names up to 200; 400 px left for both columns.
        double[] players = [100, 110, 120, 300], ships = [150, 160, 170, 200];
        var cols = Table(300, 200, players, ships);
        var available = 400 + 60 + 55;
        var r = ColumnLayout.Solve(cols, available);
        Assert.Equal(400, r.Widths[0] + r.Widths[1], 6);
        Assert.True(r.Widths[1] >= 200);                 // every ship name complete
        Assert.Equal(1, players.Count(w => w > r.Widths[0]) + ships.Count(w => w > r.Widths[1])); // only the extreme name
    }

    [Fact]
    public void A_tie_gives_the_player_column_more_room_and_minimum_widths_hold()
    {
        var r = ColumnLayout.Solve(Table(200, 200, [200], [200]), 300 + 60 + 55);
        Assert.True(r.Widths[0] >= r.Widths[1]);
        Assert.True(r.Widths[0] >= 110 && r.Widths[1] >= 130);

        var tiny = ColumnLayout.Solve(Table(), 100);
        Assert.Equal(110, tiny.Widths[0]);
        Assert.Equal(130, tiny.Widths[1]);
    }
}
