using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Foretop.App.ViewModels;
using Foretop.Core.Replays;

namespace Foretop.App.Views;

/// <summary>
/// The replay minimap: map image, 10 × 10 grid, capture points, smoke, ship trails and markers at <see cref="Time"/>.
/// Ships the recording client could not see are drawn hollow at their last known position, sunk ships as a cross.
/// </summary>
/// <remarks>
/// Every position and distance goes through one <see cref="MinimapProjection"/> per frame (map bounds from the
/// game's <c>space.settings</c>, fitted without distortion, zoom and camera), so the images, grid, capture points,
/// smoke, trails and ships cannot drift apart. Mouse wheel zooms at the cursor, dragging pans, double-click fits.
/// Markers and labels keep their size on screen at every zoom.
/// </remarks>
public sealed class ReplayCanvas : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(ReplayScene), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ReplayCanvas)d).ResetView()));

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(nameof(Time), typeof(double), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTrailsProperty = DependencyProperty.Register(nameof(ShowTrails), typeof(bool), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowNamesProperty = DependencyProperty.Register(nameof(ShowNames), typeof(bool), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowDebugProperty = DependencyProperty.Register(nameof(ShowDebug), typeof(bool), typeof(ReplayCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey CurrentZoomKey = DependencyProperty.RegisterReadOnly(nameof(CurrentZoom), typeof(double), typeof(ReplayCanvas),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty CurrentZoomProperty = CurrentZoomKey.DependencyProperty;

    public ReplayScene? Scene { get => (ReplayScene?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public double Time { get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }
    public bool ShowTrails { get => (bool)GetValue(ShowTrailsProperty); set => SetValue(ShowTrailsProperty, value); }
    public bool ShowNames { get => (bool)GetValue(ShowNamesProperty); set => SetValue(ShowNamesProperty, value); }

    /// <summary>Draws map bounds, world origin, a world-unit grid, scale, zoom, camera and ship coordinates.</summary>
    public bool ShowDebug { get => (bool)GetValue(ShowDebugProperty); set => SetValue(ShowDebugProperty, value); }

    /// <summary>Zoom relative to the fitted map (1 = whole map).</summary>
    public double CurrentZoom => (double)GetValue(CurrentZoomProperty);

    private const double Gutter = 18; // grid labels
    private const float TrailSeconds = 60;
    private const double WheelStep = 1.25;

    // Camera: zoom relative to the fitted map and the map point (fractions) at the centre of the view.
    private double _zoom = 1, _camU = 0.5, _camV = 0.5;
    private Point? _dragFrom;
    private Point? _cursor;
    private double _renderMs;
    private readonly System.Diagnostics.Stopwatch _renderClock = new();

    public ReplayCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    // ---- camera ----

    /// <summary>The projection for the current size and camera (null without a scene or room).</summary>
    public MinimapProjection? Projection()
    {
        if (Scene is not { } scene) return null;
        var w = ActualWidth - 2 * Gutter;
        var h = ActualHeight - 2 * Gutter;
        if (w < 40 || h < 40) return null;
        return new MinimapProjection(scene.Bounds, Gutter, Gutter, w, h, _zoom, _camU, _camV);
    }

    private void Apply(MinimapProjection p)
    {
        (_zoom, _camU, _camV) = (p.Zoom, p.CameraU, p.CameraV);
        SetValue(CurrentZoomKey, _zoom);
        Cursor = _zoom > 1.0001 ? Cursors.SizeAll : null;
        InvalidateVisual();
    }

    public void ResetView()
    {
        (_zoom, _camU, _camV) = (1, 0.5, 0.5);
        SetValue(CurrentZoomKey, 1.0);
        Cursor = null;
        InvalidateVisual();
    }

    /// <summary>Zooms to <paramref name="zoom"/>, centred on a world point (or keeping the current centre).</summary>
    public void ZoomTo(double zoom, double? worldX = null, double? worldZ = null)
    {
        if (Projection() is not { } p) return;
        var (u, v) = worldX is double x && worldZ is double z ? p.ToMap(x, z) : (p.CameraU, p.CameraV);
        Apply(p.With(zoom, u, v));
    }

    public void ZoomIn() => ZoomBy(WheelStep * WheelStep, null);
    public void ZoomOut() => ZoomBy(1 / (WheelStep * WheelStep), null);

    private void ZoomBy(double factor, Point? at)
    {
        if (Projection() is not { } p) return;
        var c = at ?? new Point(p.View.Left + p.View.Width / 2, p.View.Top + p.View.Height / 2);
        Apply(p.ZoomAt(_zoom * factor, c.X, c.Y));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Scene is null) return;
        ZoomBy(Math.Pow(WheelStep, e.Delta / 120.0), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Scene is null) return;
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        _cursor = pos;
        if (_dragFrom is { } from && Projection() is { } p)
        {
            _dragFrom = pos;
            Apply(p.Pan(pos.X - from.X, pos.Y - from.Y));
        }
        else if (ShowDebug) InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _cursor = null;
        if (ShowDebug) InvalidateVisual();
    }

    // ---- drawing ----

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _renderClock.Restart();
        RenderScene(dc);
        _renderMs = _renderClock.Elapsed.TotalMilliseconds;
    }

    private void RenderScene(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit testing for wheel and drag
        var scene = Scene;
        var proj = Projection();
        if (scene is null || proj is not { IsValid: true }) return;

        var b = scene.Bounds;
        Point P(double x, double z) { var (sx, sy) = proj.ToScreen(x, z); return new Point(sx, sy); }
        var mr = proj.MapRect;
        var map = new Rect(mr.Left, mr.Top, mr.Width, mr.Height);
        var view = new Rect(proj.View.Left, proj.View.Top, proj.View.Width, proj.View.Height);
        var shown = Rect.Intersect(map, view); // the part of the map on screen
        if (shown.IsEmpty) return;
        var t = (float)Time;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var dim = Res("TextDim", Brushes.Gray);
        var font = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        FormattedText Label(string s, double em, Brush br, FontWeight? w = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, w ?? FontWeights.Normal, FontStretches.Normal), em, br, dpi);

        // Grid labels A–J × 1–10 in the gutter, for the columns and rows on screen.
        for (var i = 0; i < 10; i++)
        {
            var cx = map.Left + (i + 0.5) * map.Width / 10;
            if (cx >= shown.Left && cx <= shown.Right)
            {
                var num = Label((i + 1).ToString(CultureInfo.InvariantCulture), 10, dim);
                dc.DrawText(num, new Point(cx - num.Width / 2, shown.Top - Gutter + 2));
            }
            var cy = map.Top + (i + 0.5) * map.Height / 10;
            if (cy >= shown.Top && cy <= shown.Bottom)
            {
                var letter = Label(((char)('A' + i)).ToString(), 10, dim);
                dc.DrawText(letter, new Point(shown.Left - Gutter / 2 - letter.Width / 2 - 2, cy - letter.Height / 2));
            }
        }

        dc.PushClip(new RectangleGeometry(shown));

        // Map: water, then land, both covering exactly the playable area.
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x52)), null, map);
        if (scene.Water is not null) dc.DrawImage(scene.Water, map);
        if (scene.Land is not null) dc.DrawImage(scene.Land, map);

        // Grid, as on the in-game minimap: the map's width and height in tenths.
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 1);
        gridPen.Freeze();
        for (var i = 0; i <= 10; i++)
        {
            dc.DrawLine(gridPen, P(b.MinX + i * b.Width / 10, b.MaxZ), P(b.MinX + i * b.Width / 10, b.MinZ));
            dc.DrawLine(gridPen, P(b.MinX, b.MaxZ - i * b.Height / 10), P(b.MaxX, b.MaxZ - i * b.Height / 10));
        }

        // Marks on the map sit on the game's dark sea, whatever the page theme: a light theme's ink colours would vanish
        // there, so they keep the dark palette's team colours.
        var darkTheme = TryFindResource("IsDarkTheme") is not false;
        var ally = darkTheme ? Res("Friendly", Brushes.SeaGreen) : Frozen(0x3F, 0xB6, 0xA8);
        var enemy = darkTheme ? Res("Enemy", Brushes.IndianRed) : Frozen(0xE8, 0x61, 0x5A);
        var mapDim = darkTheme ? dim : Frozen(0x92, 0x9E, 0xAE);
        var self = new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xE6));
        self.Freeze();
        Brush Team(int relation) => relation == 0 ? self : relation == 1 ? ally : enemy;
        Color C(Brush br) => br is SolidColorBrush s ? s.Color : Colors.Gray;

        // Capture points.
        foreach (var z in scene.Data.Zones)
        {
            if (t < z.From || t > z.To) continue;
            var owner = z.OwnerAt(t);
            var rel = owner < 0 || scene.Data.SelfTeam < 0 ? -1 : owner == scene.Data.SelfTeam ? 1 : 2;
            var col = rel == -1 ? Colors.White : C(Team(rel));
            var r = proj.Length(z.Radius);
            var c = P(z.X, z.Z);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, col.R, col.G, col.B)), new Pen(new SolidColorBrush(Color.FromArgb(200, col.R, col.G, col.B)), 1.5), c, r, r);
            if (z.Label.Length > 0)
            {
                var l = Label(z.Label, Math.Clamp(r * 0.7, 11, 28), new SolidColorBrush(Color.FromArgb(230, col.R, col.G, col.B)), FontWeights.SemiBold);
                dc.DrawText(l, new Point(c.X - l.Width / 2, c.Y - l.Height / 2));
            }
        }

        // Smoke.
        var smokeFill = new SolidColorBrush(Color.FromArgb(120, 225, 228, 232));
        smokeFill.Freeze();
        foreach (var s in scene.Data.Smokes)
        {
            if (t < s.From || t > s.To) continue;
            var r = Math.Max(2, proj.Length(s.Radius));
            foreach (var (x, z) in s.Points) dc.DrawEllipse(smokeFill, null, P(x, z), r, r);
        }

        // Trails (visible part of the last minute), drawn under every marker.
        if (ShowTrails)
        {
            foreach (var ship in scene.Ships)
            {
                if (ship.Track.Length < 2) continue;
                var col = C(Team(ship.Relation));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(ship.IsDead(t) ? (byte)40 : (byte)120, col.R, col.G, col.B)), ship.IsSelf ? 1.8 : 1.2);
                pen.Freeze();
                var from = t - TrailSeconds;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var open = false;
                    var prevT = 0f;
                    foreach (var p in ship.Track)
                    {
                        if (p.T < from) continue;
                        if (p.T > t) break;
                        if (open && p.T - prevT > SceneShip.MaxGap) open = false; // out of view: break the line
                        if (!open) { g.BeginFigure(P(p.X, p.Z), false, false); open = true; }
                        else g.LineTo(P(p.X, p.Z), true, false);
                        prevT = p.T;
                    }
                    // End the trail at the interpolated position, so it meets the marker.
                    if (open && !ship.IsDead(t) && ship.VisibleAt(t) && ship.TryPosition(t, out var ex, out var ez, out _)) g.LineTo(P(ex, ez), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        // Markers: hidden and sunk first, then visible ships, own ship last.
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 10, 14, 20)), 1);
        outline.Freeze();
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
            if (!shown.Contains(c)) continue;
            var brush = Team(ship.Relation);
            var col = C(brush);
            if (dead)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)), 1.6);
                dc.DrawLine(pen, new Point(c.X - 4, c.Y - 4), new Point(c.X + 4, c.Y + 4));
                dc.DrawLine(pen, new Point(c.X - 4, c.Y + 4), new Point(c.X + 4, c.Y - 4));
                if (ShowDebug) DebugCoords(dc, Label, c, x, z, 6);
                continue;
            }
            var len = ship.Species switch { "Battleship" or "AirCarrier" => 9.5, "Destroyer" or "Submarine" => 6.5, _ => 8.0 };
            if (ship.IsSelf) len += 1.5;
            var marker = Marker(c, yaw, len);
            if (visible) dc.DrawGeometry(brush, outline, marker);
            else dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B)), 1.2), marker);
            var below = len + 1;
            if (ShowNames)
            {
                var name = Label(ship.ShipName, 10, visible ? new SolidColorBrush(Color.FromArgb(235, col.R, col.G, col.B)) : mapDim);
                dc.DrawText(name, new Point(c.X - name.Width / 2, c.Y + below));
                below += name.Height;
                if (visible && ship.MaxHealth > 0 && !ship.IsTestShip)
                {
                    var frac = Math.Clamp(ship.HealthAt(t) / ship.MaxHealth, 0, 1);
                    const double w = 22.0;
                    var bar = new Rect(c.X - w / 2, c.Y - len - 5, w, 2.5);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, bar);
                    dc.DrawRectangle(brush, null, new Rect(bar.X, bar.Y, w * frac, bar.Height));
                }
            }
            if (ShowDebug) DebugCoords(dc, Label, c, x, z, below);
        }

        if (ShowDebug) DrawDebugGeometry(dc, proj, P, Label);
        dc.Pop();

        if (_zoom > 1.0001) DrawOverview(dc, scene, proj, shown);
        if (!b.Known)
        {
            var warn = Label("Map size unknown: positions may be off", 11, Res("StatusAmber", Brushes.Orange));
            dc.DrawText(warn, new Point(shown.Right - warn.Width - 6, shown.Bottom - warn.Height - 4));
        }
        if (ShowDebug) DrawDebugPanel(dc, scene, proj, shown, Label);
    }

    /// <summary>A small whole-map view with the visible part outlined, while zoomed in.</summary>
    private void DrawOverview(DrawingContext dc, ReplayScene scene, MinimapProjection proj, Rect shown)
    {
        var size = Math.Clamp(Math.Min(shown.Width, shown.Height) * 0.22, 70, 150);
        var aspect = scene.Bounds.Width / scene.Bounds.Height;
        var box = aspect >= 1 ? new Rect(shown.Left + 8, shown.Top + 8, size, size / aspect) : new Rect(shown.Left + 8, shown.Top + 8, size * aspect, size);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x52)), new Pen(Res("Border2", Brushes.Gray), 1), box);
        if (scene.Water is not null) dc.DrawImage(scene.Water, box);
        if (scene.Land is not null) dc.DrawImage(scene.Land, box);
        // The visible part: view corners back to map fractions.
        var (x0, z0) = proj.ToWorld(shown.Left, shown.Top);
        var (x1, z1) = proj.ToWorld(shown.Right, shown.Bottom);
        var (u0, v0) = proj.ToMap(x0, z0);
        var (u1, v1) = proj.ToMap(x1, z1);
        var r = new Rect(new Point(box.Left + u0 * box.Width, box.Top + v0 * box.Height), new Point(box.Left + u1 * box.Width, box.Top + v1 * box.Height));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(Res("Accent", Brushes.White), 1.5), r);
    }

    private void DebugCoords(DrawingContext dc, Func<string, double, Brush, FontWeight?, FormattedText> label, Point c, float x, float z, double below)
    {
        var s = label($"{x:0},{z:0}", 9, Brushes.Yellow, null);
        dc.DrawText(s, new Point(c.X - s.Width / 2, c.Y + below));
    }

    /// <summary>Debug: map bounds, world origin, map centre and a grid every 100 world units.</summary>
    private void DrawDebugGeometry(DrawingContext dc, MinimapProjection proj, Func<double, double, Point> P, Func<string, double, Brush, FontWeight?, FormattedText> label)
    {
        var b = proj.Bounds;
        var unitPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 255, 255)), 0.8) { DashStyle = DashStyles.Dot };
        unitPen.Freeze();
        const double step = 100;
        for (var x = Math.Ceiling(b.MinX / step) * step; x <= b.MaxX; x += step) dc.DrawLine(unitPen, P(x, b.MinZ), P(x, b.MaxZ));
        for (var z = Math.Ceiling(b.MinZ / step) * step; z <= b.MaxZ; z += step) dc.DrawLine(unitPen, P(b.MinX, z), P(b.MaxX, z));
        var tick = new SolidColorBrush(Color.FromArgb(200, 0, 255, 255));
        for (var x = Math.Ceiling(b.MinX / 200) * 200; x <= b.MaxX; x += 200)
            dc.DrawText(label(x.ToString("0", CultureInfo.InvariantCulture), 9, tick, null), P(x, b.MaxZ) + new Vector(2, 1));
        for (var z = Math.Ceiling(b.MinZ / 200) * 200; z <= b.MaxZ; z += 200)
            dc.DrawText(label(z.ToString("0", CultureInfo.InvariantCulture), 9, tick, null), P(b.MinX, z) + new Vector(2, 1));

        var boundsPen = new Pen(Brushes.Yellow, 1.5) { DashStyle = DashStyles.Dash };
        boundsPen.Freeze();
        dc.DrawRectangle(null, boundsPen, new Rect(P(b.MinX, b.MaxZ), P(b.MaxX, b.MinZ)));
        var o = P(0, 0);
        var originPen = new Pen(Brushes.Magenta, 1.5);
        originPen.Freeze();
        dc.DrawLine(originPen, o + new Vector(-10, 0), o + new Vector(10, 0));
        dc.DrawLine(originPen, o + new Vector(0, -10), o + new Vector(0, 10));
        dc.DrawText(label("0,0", 9, Brushes.Magenta, null), o + new Vector(4, 2));
        var mc = P(b.CenterX, b.CenterZ);
        dc.DrawEllipse(null, new Pen(Brushes.Yellow, 1), mc, 5, 5);
    }

    private void DrawDebugPanel(DrawingContext dc, ReplayScene scene, MinimapProjection proj, Rect shown, Func<string, double, Brush, FontWeight?, FormattedText> label)
    {
        var b = scene.Bounds;
        var inv = CultureInfo.InvariantCulture;
        var mr = proj.MapRect;
        var lines = new List<string>
        {
            scene.Space.Length > 0 ? scene.Space : "space unknown",
            $"bounds X {b.MinX.ToString("0", inv)}…{b.MaxX.ToString("0", inv)}  Z {b.MinZ.ToString("0", inv)}…{b.MaxZ.ToString("0", inv)}  ({(b.Known ? "space.settings" : "DEFAULT, not readable")})",
            $"centre {b.CenterX.ToString("0", inv)},{b.CenterZ.ToString("0", inv)}  size {b.Width.ToString("0", inv)} × {b.Height.ToString("0", inv)}",
            $"view {proj.View.Width.ToString("0", inv)} × {proj.View.Height.ToString("0", inv)}  map {mr.Width.ToString("0", inv)} × {mr.Height.ToString("0", inv)} at {mr.Left.ToString("0.0", inv)},{mr.Top.ToString("0.0", inv)}",
            $"scale {proj.Scale.ToString("0.0000", inv)} px/unit (fit {proj.FitScale.ToString("0.0000", inv)})  zoom {proj.Zoom.ToString("0.00", inv)}×",
            $"camera u {proj.CameraU.ToString("0.000", inv)}  v {proj.CameraV.ToString("0.000", inv)}",
            $"DPI {VisualTreeHelper.GetDpi(this).PixelsPerDip.ToString("0.00", inv)}  canvas {ActualWidth.ToString("0", inv)} × {ActualHeight.ToString("0", inv)}  last frame {_renderMs.ToString("0.0", inv)} ms",
        };
        if (_cursor is { } cur && shown.Contains(cur))
        {
            var (x, z) = proj.ToWorld(cur.X, cur.Y);
            var (u, v) = proj.ToMap(x, z);
            lines.Add($"cursor x {x.ToString("0", inv)}  z {z.ToString("0", inv)}  (u {u.ToString("0.000", inv)}, v {v.ToString("0.000", inv)})");
        }
        var texts = lines.Select(l => label(l, 10.5, Brushes.White, null)).ToList();
        var w = texts.Max(x => x.Width) + 12;
        var h = texts.Sum(x => x.Height) + 8;
        var at = new Point(shown.Right - w - 6, shown.Top + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null, new Rect(at, new Size(w, h)), 4, 4);
        var y = at.Y + 4;
        foreach (var x in texts)
        {
            dc.DrawText(x, new Point(at.X + 6, y));
            y += x.Height;
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
}
