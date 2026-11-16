using Foretop.Core.Replays;
using Xunit;

namespace Foretop.Tests;

public class MinimapProjectionTests
{
    private const double Eps = 1e-6;

    // The two forms found in the game files (build 13533378): child elements (5 maps, e.g. 28_naval_mission) and
    // attributes without chunkSize (53 maps, e.g. 15_NE_north, 01_solomon_islands).
    private const string Elements = "<space.settings><chunkSize>100.0000</chunkSize><bounds><minX>-9</minX><maxX>8</maxX><minY>-9</minY><maxY>8</maxY></bounds></space.settings>";
    private const string AttributesNorth = "<space.settings>\r\n\t<bounds\r\n\t\t\tminX=\"-10\"\r\n\t\t\tmaxX=\"9\"\r\n\t\t\tminY=\"-10\"\r\n\t\t\tmaxY=\"9\" />\r\n\t<terrain/></space.settings>";
    private const string AttributesSolomon = "<space.settings><bounds minX=\"-7\" maxX=\"6\" minY=\"-7\" maxY=\"6\" /></space.settings>";

    [Fact]
    public void Bounds_from_child_elements()
    {
        var b = MapBounds.FromSpaceSettings(Elements);
        Assert.Equal(new MapBounds(-700, 700, -700, 700), b);
        Assert.True(b.Known);
    }

    [Fact]
    public void Bounds_from_attributes_which_most_maps_use()
    {
        Assert.Equal(new MapBounds(-800, 800, -800, 800), MapBounds.FromSpaceSettings(AttributesNorth));
        Assert.Equal(new MapBounds(-500, 500, -500, 500), MapBounds.FromSpaceSettings(AttributesSolomon));
    }

    [Fact]
    public void Rectangular_and_offset_bounds_are_kept()
    {
        var b = MapBounds.FromSpaceSettings("<s><chunkSize>50</chunkSize><bounds minX=\"-6\" maxX=\"9\" minY=\"-4\" maxY=\"3\" /></s>");
        Assert.Equal(new MapBounds(-200, 400, -100, 100), b);
        Assert.Equal(100, b.CenterX);
        Assert.Equal(0, b.CenterZ);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not xml")]
    [InlineData("<space.settings><terrain/></space.settings>")]
    [InlineData("<space.settings><bounds minY=\"-9\" /></space.settings>")]
    public void Unreadable_bounds_fall_back_and_say_so(string? settings)
    {
        var b = MapBounds.FromSpaceSettings(settings);
        Assert.False(b.Known);
        Assert.Equal(1400, b.Width);
    }

    [Fact]
    public void Tiny_spaces_without_room_for_a_border_use_all_chunks()
    {
        var b = MapBounds.FromSpaceSettings("<s><bounds minX=\"-2\" maxX=\"1\" minY=\"-2\" maxY=\"1\" /></s>");
        Assert.Equal(new MapBounds(-200, 200, -200, 200), b);
    }

    private static readonly MapBounds Square = new(-700, 700, -700, 700);

    [Theory]
    // view 1400 × 1400 at (0,0): 1 unit per world unit
    [InlineData(1400, 1400, 0, 0, 700, 700)]
    [InlineData(1400, 1400, -700, 700, 0, 0)]       // north-west corner → top left
    [InlineData(1400, 1400, 700, -700, 1400, 1400)] // south-east corner → bottom right
    [InlineData(1400, 1400, 350, 0, 1050, 700)]     // east → right
    [InlineData(1400, 1400, 0, 350, 700, 350)]      // north → up
    // wide view 3440 × 1440: map 1440 high, centred horizontally (1000 px each side)
    [InlineData(3440, 1440, -700, 700, 1000, 0)]
    [InlineData(3440, 1440, 700, -700, 2440, 1440)]
    [InlineData(3440, 1440, 0, 0, 1720, 720)]
    // tall view 1080 × 1920: map 1080 wide, centred vertically
    [InlineData(1080, 1920, -700, 700, 0, 420)]
    [InlineData(1080, 1920, 700, -700, 1080, 1500)]
    public void World_to_screen_keeps_the_aspect_ratio_and_centres_the_map(double w, double h, double x, double z, double sx, double sy)
    {
        var p = new MinimapProjection(Square, 0, 0, w, h);
        var (ax, ay) = p.ToScreen(x, z);
        Assert.Equal(sx, ax, 6);
        Assert.Equal(sy, ay, 6);
        var (bx, bz) = p.ToWorld(ax, ay);
        Assert.Equal(x, bx, 6);
        Assert.Equal(z, bz, 6);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(613.5, 401.25)]
    public void Map_is_square_and_inside_the_view_at_common_resolutions(double w, double h)
    {
        var p = new MinimapProjection(Square, 10, 20, w, h);
        var r = p.MapRect;
        Assert.Equal(r.Width, r.Height, 6);                   // no stretching
        Assert.Equal(Math.Min(w, h), r.Width, 6);             // fills the short side
        Assert.Equal(10 + w / 2, r.Left + r.Width / 2, 6);    // centred
        Assert.Equal(20 + h / 2, r.Top + r.Height / 2, 6);
        Assert.Equal(r.Width / 1400, p.Scale, 9);
    }

    [Fact]
    public void Rectangular_map_is_not_stretched()
    {
        var p = new MinimapProjection(new MapBounds(-200, 400, -100, 100), 0, 0, 1000, 1000);
        var r = p.MapRect;
        Assert.Equal(1000, r.Width, 6);
        Assert.Equal(1000.0 / 3, r.Height, 6);
        Assert.Equal(p.Scale * 600, r.Width, 6);
        Assert.Equal(p.Scale * 200, r.Height, 6);
        var (sx, sy) = p.ToScreen(100, 0); // map centre
        Assert.Equal(500, sx, 6);
        Assert.Equal(500, sy, 6);
    }

    [Fact]
    public void Lengths_use_the_same_scale_as_positions()
    {
        var p = new MinimapProjection(Square, 0, 0, 760, 760, zoom: 3, cameraU: 0.3, cameraV: 0.6);
        var (ax, _) = p.ToScreen(100, 0);
        var (bx, _) = p.ToScreen(100 + 75, 0);
        Assert.Equal(p.Length(75), bx - ax, 6);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_cursor()
    {
        var p = new MinimapProjection(Square, 0, 0, 1000, 800);
        var (sx, sy) = p.ToScreen(-300, 250);
        foreach (var zoom in new[] { 2.0, 4.0, 8.0 })
        {
            var q = p.ZoomAt(zoom, sx, sy);
            Assert.Equal(zoom, q.Zoom);
            var (x, z) = q.ToWorld(sx, sy);
            Assert.Equal(-300, x, 6);
            Assert.Equal(250, z, 6);
        }
    }

    [Fact]
    public void Zoom_is_limited_and_back_to_fit_centres_the_map()
    {
        var p = new MinimapProjection(Square, 0, 0, 800, 800);
        Assert.Equal(MinimapProjection.MaxZoom, p.ZoomAt(100, 10, 10).Zoom);
        var back = p.ZoomAt(4, 50, 50).ZoomAt(0.1, 700, 700);
        Assert.Equal(1, back.Zoom);
        Assert.Equal((0.5, 0.5), (back.CameraU, back.CameraV));
    }

    [Fact]
    public void Panning_moves_everything_together_and_stops_at_the_map_edge()
    {
        var p = new MinimapProjection(Square, 0, 0, 800, 800, zoom: 2);
        var (ax, ay) = p.ToScreen(100, 100);
        var q = p.Pan(40, -30);
        var (bx, by) = q.ToScreen(100, 100);
        Assert.Equal(ax + 40, bx, 6);
        Assert.Equal(ay - 30, by, 6);

        var far = p.Pan(100_000, 100_000); // dragged far: the map's north-west corner stops at the view's corner
        var (cx, cy) = far.ToScreen(-700, 700);
        Assert.Equal(0, cx, 6);
        Assert.Equal(0, cy, 6);

        var fit = new MinimapProjection(Square, 0, 0, 800, 800);
        Assert.Equal((0.5, 0.5), (fit.Pan(300, 300).CameraU, fit.Pan(300, 300).CameraV)); // nothing to pan at zoom 1
    }

    [Fact]
    public void Resizing_keeps_the_same_map_point_in_the_centre()
    {
        var p = new MinimapProjection(Square, 0, 0, 800, 600).ZoomAt(4, 200, 150);
        var (x, z) = p.ToWorld(400, 300);
        var resized = new MinimapProjection(Square, 0, 0, 1600, 1000, p.Zoom, p.CameraU, p.CameraV);
        var (x2, z2) = resized.ToWorld(800, 500);
        Assert.Equal(x, x2, 6);
        Assert.Equal(z, z2, 6);
    }

    [Fact]
    public void Empty_view_is_not_valid_and_does_not_throw()
    {
        var p = new MinimapProjection(Square, 0, 0, 0, 0);
        Assert.False(p.IsValid);
        _ = p.ToWorld(1, 1);
        _ = p.ZoomAt(2, 0, 0).Pan(5, 5);
        Assert.True(Eps > 0);
    }
}
