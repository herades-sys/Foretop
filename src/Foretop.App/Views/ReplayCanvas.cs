using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

/// <summary>
/// The replay minimap: map image, 10 × 10 grid, capture points, smoke, ship trails and markers at <see cref="Time"/>.
/// Ships the recording client could not see are drawn hollow at their last known position, sunk ships as a cross.
/// </summary>
public sealed class ReplayCanvas : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(ReplayScene), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(nameof(Time), typeof(double), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTrailsProperty = DependencyProperty.Register(nameof(ShowTrails), typeof(bool), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowNamesProperty = DependencyProperty.Register(nameof(ShowNames), typeof(bool), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public ReplayScene? Scene { get => (ReplayScene?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public double Time { get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }
    public bool ShowTrails { get => (bool)GetValue(ShowTrailsProperty); set => SetValue(ShowTrailsProperty, value); }
    public bool ShowNames { get => (bool)GetValue(ShowNamesProperty); set => SetValue(ShowNamesProperty, value); }

    private const double Gutter = 18; // grid labels
    private const float TrailSeconds = 60;

    public ReplayCanvas() => ClipToBounds = true;

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var scene = Scene;
        var size = Math.Min(ActualWidth, ActualHeight) - 2 * Gutter;
        if (scene is null || size < 60) return;
        var ox = (ActualWidth - size) / 2;
        var oy = (ActualHeight - size) / 2;
        var map = new Rect(ox, oy, size, size);
        var half = scene.Half;
        var k = size / (2 * half);
        Point P(float x, float z) => new(ox + (x + half) * k, oy + (half - z) * k);
        var t = (float)Time;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var dim = Res("TextDim", Brushes.Gray);
        var text = Res("Text", Brushes.White);
        FormattedText Label(string s, double em, Brush b, FontWeight? w = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, w ?? FontWeights.Normal, FontStretches.Normal), em, b, dpi);

        // Map.
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x52)), null, map);
        if (scene.Water is not null) dc.DrawImage(scene.Water, map);
        if (scene.Land is not null) dc.DrawImage(scene.Land, map);

        // Grid A–J × 1–10, as on the in-game minimap.
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 1);
        gridPen.Freeze();
        for (var i = 0; i <= 10; i++)
        {
            var d = i * size / 10;
            dc.DrawLine(gridPen, new Point(ox + d, oy), new Point(ox + d, oy + size));
            dc.DrawLine(gridPen, new Point(ox, oy + d), new Point(ox + size, oy + d));
            if (i == 10) continue;
            var num = Label((i + 1).ToString(CultureInfo.InvariantCulture), 10, dim);
            dc.DrawText(num, new Point(ox + d + size / 20 - num.Width / 2, oy - Gutter + 2));
            var letter = Label(((char)('A' + i)).ToString(), 10, dim);
            dc.DrawText(letter, new Point(ox - Gutter / 2 - letter.Width / 2 - 2, oy + d + size / 20 - letter.Height / 2));
        }
        dc.DrawRectangle(null, new Pen(Res("Border2", Brushes.Gray), 1), map);

        var ally = Res("Friendly", Brushes.SeaGreen);
        var enemy = Res("Enemy", Brushes.IndianRed);
        var self = new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xE6));
        self.Freeze();
        Brush Team(int relation) => relation == 0 ? self : relation == 1 ? ally : enemy;
        Color C(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

        // Capture points.
        foreach (var z in scene.Data.Zones)
        {
            if (t < z.From || t > z.To) continue;
            var owner = z.OwnerAt(t);
            var rel = owner < 0 || scene.Data.SelfTeam < 0 ? -1 : owner == scene.Data.SelfTeam ? 1 : 2;
            var col = rel == -1 ? Colors.White : C(Team(rel));
            var r = z.Radius * k;
            var c = P(z.X, z.Z);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, col.R, col.G, col.B)), new Pen(new SolidColorBrush(Color.FromArgb(200, col.R, col.G, col.B)), 1.5), c, r, r);
            if (z.Label.Length > 0)
            {
                var l = Label(z.Label, Math.Max(11, r * 0.7), new SolidColorBrush(Color.FromArgb(230, col.R, col.G, col.B)), FontWeights.SemiBold);
                dc.DrawText(l, new Point(c.X - l.Width / 2, c.Y - l.Height / 2));
            }
        }

        // Smoke.
        var smokeFill = new SolidColorBrush(Color.FromArgb(120, 225, 228, 232));
        smokeFill.Freeze();
        foreach (var s in scene.Data.Smokes)
        {
            if (t < s.From || t > s.To) continue;
            var r = Math.Max(2, s.Radius * k);
            foreach (var (x, z) in s.Points) dc.DrawEllipse(smokeFill, null, P(x, z), r, r);
        }

        // Trails (visible part of the last minute), drawn under every marker.
        if (ShowTrails)
        {
            foreach (var ship in scene.Ships)
            {
                if (ship.Track.Length < 2) continue;
                var col = C(Team(ship.Relation));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(ship.IsDead(t) ? (byte)40 : (byte)110, col.R, col.G, col.B)), ship.IsSelf ? 1.6 : 1.1);
                pen.Freeze();
                var from = t - TrailSeconds;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var open = false;
                    TrackPointHolder prev = default;
                    foreach (var p in ship.Track)
                    {
                        if (p.T < from) continue;
                        if (p.T > t) break;
                        if (open && p.T - prev.T > 6) open = false; // out of view: break the line
                        if (!open) { g.BeginFigure(P(p.X, p.Z), false, false); open = true; }
                        else g.LineTo(P(p.X, p.Z), true, false);
                        prev = new TrackPointHolder(p.T);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        // Markers: hidden and sunk first, then visible ships, own ship last.
        foreach (var ship in scene.Ships.OrderBy(s => s.IsDead(t) ? 0 : s.VisibleAt(t) ? (s.IsSelf ? 3 : 2) : 1))
        {
            var dead = ship.IsDead(t);
            var visible = ship.VisibleAt(t);
            float when;
            if (dead) when = ship.DeathAt!.Value;
            else if (visible) when = t;
            else if (ship.LastSeenBefore(t) is float seen) when = seen;
            else continue; // not spotted yet
            if (!ship.TryPosition(when, out var x, out var z, out var yaw)) continue;
            var c = P(x, z);
            var brush = Team(ship.Relation);
            var col = C(brush);
            if (dead)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)), 1.6);
                dc.DrawLine(pen, new Point(c.X - 4, c.Y - 4), new Point(c.X + 4, c.Y + 4));
                dc.DrawLine(pen, new Point(c.X - 4, c.Y + 4), new Point(c.X + 4, c.Y - 4));
                continue;
            }
            var len = ship.Species switch { "Battleship" or "AirCarrier" => 9.5, "Destroyer" or "Submarine" => 6.5, _ => 8.0 };
            if (ship.IsSelf) len += 1.5;
            var marker = Marker(c, yaw, len);
            if (visible)
            {
                dc.DrawGeometry(brush, new Pen(new SolidColorBrush(Color.FromArgb(200, 10, 14, 20)), 1), marker);
            }
            else
            {
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)), 1.2), marker);
            }
            if (!ShowNames) continue;
            var name = Label(ship.IsSelf ? ship.ShipName : ship.ShipName, 10, visible ? new SolidColorBrush(Color.FromArgb(235, col.R, col.G, col.B)) : dim);
            dc.DrawText(name, new Point(c.X - name.Width / 2, c.Y + len + 1));
            if (visible && ship.MaxHealth > 0 && !ship.IsTestShip)
            {
                var frac = Math.Clamp(ship.HealthAt(t) / ship.MaxHealth, 0, 1);
                var w = 22.0;
                var bar = new Rect(c.X - w / 2, c.Y - len - 5, w, 2.5);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, bar);
                dc.DrawRectangle(brush, null, new Rect(bar.X, bar.Y, w * frac, bar.Height));
            }
        }
    }

    /// <summary>An arrow head pointing along the heading (yaw 0 = north, clockwise positive).</summary>
    private static Geometry Marker(Point c, float yaw, double len)
    {
        var dx = Math.Sin(yaw);
        var dy = -Math.Cos(yaw);
        var px = -dy;
        var py = dx;
        var tip = new Point(c.X + dx * len, c.Y + dy * len);
        var back = len * 0.75;
        var wide = len * 0.55;
        var l = new Point(c.X - dx * back + px * wide, c.Y - dy * back + py * wide);
        var r = new Point(c.X - dx * back - px * wide, c.Y - dy * back - py * wide);
        var notch = new Point(c.X - dx * back * 0.45, c.Y - dy * back * 0.45);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(l, true, false);
            ctx.LineTo(notch, true, false);
            ctx.LineTo(r, true, false);
        }
        g.Freeze();
        return g;
    }

    private readonly record struct TrackPointHolder(float T);
}
