using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Foretop.Core.GameData;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace Foretop.App.Views;

/// <summary>What the viewer shows for one armour key.</summary>
public sealed record PlateInfo(int Key, string Title, string Zone, double Thickness, Color Colour);

/// <summary>
/// Draws a hull armour model with an orthographic projection and the painter's algorithm (far triangles first), so the
/// nearest plate wins, as in the game. Side, Top and Bow are fixed views; 3D can be rotated by dragging.
/// Shift-drag moves, the wheel zooms, double-click resets, a click selects a plate.
/// </summary>
public sealed class ArmourCanvas : FrameworkElement
{
    public static readonly DependencyProperty MeshProperty = DependencyProperty.Register(nameof(Mesh), typeof(ArmourMesh), typeof(ArmourCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ArmourCanvas)d).ResetView()));

    public static readonly DependencyProperty PlatesProperty = DependencyProperty.Register(nameof(Plates), typeof(IReadOnlyDictionary<int, PlateInfo>),
        typeof(ArmourCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HiddenZonesProperty = DependencyProperty.Register(nameof(HiddenZones), typeof(IReadOnlyCollection<string>),
        typeof(ArmourCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(nameof(View), typeof(string), typeof(ArmourCanvas),
        new FrameworkPropertyMetadata("Side", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ArmourCanvas)d).ResetView()));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(int?), typeof(ArmourCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ArmourMesh? Mesh { get => (ArmourMesh?)GetValue(MeshProperty); set => SetValue(MeshProperty, value); }
    public IReadOnlyDictionary<int, PlateInfo>? Plates { get => (IReadOnlyDictionary<int, PlateInfo>?)GetValue(PlatesProperty); set => SetValue(PlatesProperty, value); }
    public IReadOnlyCollection<string>? HiddenZones { get => (IReadOnlyCollection<string>?)GetValue(HiddenZonesProperty); set => SetValue(HiddenZonesProperty, value); }
    public string View { get => (string)GetValue(ViewProperty); set => SetValue(ViewProperty, value); }
    public int? SelectedKey { get => (int?)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }

    private float _yaw, _pitch, _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Point _dragLast;
    private bool _dragged;

    // Last frame, nearest last, for hit testing.
    private (Point A, Point B, Point C, int Key)[] _drawn = [];

    public ArmourCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    private void ResetView()
    {
        (_yaw, _pitch) = View switch
        {
            "Top" => (0f, MathF.PI / 2),
            "Bow" => (-MathF.PI / 2, 0f),
            "3D" => (-0.62f, 0.42f),
            _ => (0f, 0f),
        };
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    private Matrix4x4 Rotation => Matrix4x4.CreateRotationY(_yaw) * Matrix4x4.CreateRotationX(_pitch);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable background
        var mesh = Mesh;
        var plates = Plates;
        if (mesh is null || plates is null || ActualWidth < 20 || ActualHeight < 20) return;
        var hidden = HiddenZones ?? [];
        var rot = Rotation;

        // Project every visible triangle.
        var list = new List<(Vector3 A, Vector3 B, Vector3 C, int Key, float Depth, float Light)>(mesh.Triangles.Count);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var t in mesh.Triangles)
        {
            var info = plates.TryGetValue(t.Key, out var p) ? p : null;
            if (info is not null && hidden.Contains(info.Zone)) continue;
            if (info is null && hidden.Contains(NoThickness)) continue;
            if (info is { Thickness: <= 0 } && hidden.Contains(NoThickness)) continue;
            var a = Vector3.Transform(t.A, rot);
            var b = Vector3.Transform(t.B, rot);
            var c = Vector3.Transform(t.C, rot);
            var n = Vector3.Cross(b - a, c - a);
            var len = n.Length();
            var light = len > 1e-6f ? 0.55f + 0.45f * MathF.Abs(n.Z / len) : 0.75f;
            list.Add((a, b, c, t.Key, (a.Z + b.Z + c.Z) / 3, light));
            minX = MathF.Min(minX, MathF.Min(a.X, MathF.Min(b.X, c.X)));
            maxX = MathF.Max(maxX, MathF.Max(a.X, MathF.Max(b.X, c.X)));
            minY = MathF.Min(minY, MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            maxY = MathF.Max(maxY, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
        }

        // Fit the whole hull (not just the visible part, so toggling layers does not move the picture).
        var fit = FitBox(mesh, rot);
        var margin = 28.0;
        var scale = Math.Min((ActualWidth - 2 * margin) / Math.Max(1, fit.Width), (ActualHeight - 2 * margin) / Math.Max(1, fit.Height)) * _zoom;
        ClampPan(fit, scale, margin);
        var cx = ActualWidth / 2 + _pan.X;
        var cy = ActualHeight / 2 + _pan.Y;
        var mx = fit.X + fit.Width / 2;
        var my = fit.Y + fit.Height / 2;
        Point S(Vector3 v) => new(cx + (v.X - mx) * scale, cy - (v.Y - my) * scale);

        list.Sort((x, y) => x.Depth.CompareTo(y.Depth));
        var drawn = new (Point, Point, Point, int)[list.Count];
        var brushes = new Dictionary<(int, int), Brush>();
        StreamGeometry? run = null;
        StreamGeometryContext? ctx = null;
        (int, int) runKey = (-1, -1);

        void Flush()
        {
            if (run is null) return;
            ctx!.Close();
            run.Freeze();
            dc.DrawGeometry(brushes[runKey], null, run);
            run = null;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var colour = plates.TryGetValue(t.Key, out var info) && info.Thickness > 0 ? info.Colour : NoneColour;
            var shade = (int)(t.Light * 12);
            var key = (ColourKey(colour), shade);
            if (!brushes.ContainsKey(key))
            {
                var f = shade / 12f;
                var b = new SolidColorBrush(Color.FromRgb((byte)(colour.R * f), (byte)(colour.G * f), (byte)(colour.B * f)));
                b.Freeze();
                brushes[key] = b;
            }
            if (key != runKey) { Flush(); runKey = key; }
            if (run is null)
            {
                run = new StreamGeometry();
                ctx = run.Open();
            }
            var pa = S(t.A); var pb = S(t.B); var pc = S(t.C);
            ctx!.BeginFigure(pa, true, true);
            ctx.LineTo(pb, false, false);
            ctx.LineTo(pc, false, false);
            drawn[i] = (pa, pb, pc, t.Key);
        }
        Flush();
        _drawn = drawn;

        // Selection on top.
        if (SelectedKey is int sel)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                foreach (var d in drawn.Where(d => d.Item4 == sel))
                {
                    c.BeginFigure(d.Item1, true, true);
                    c.LineTo(d.Item2, true, false);
                    c.LineTo(d.Item3, true, false);
                }
            }
            g.Freeze();
            var accent = TryFindResource("Accent") as SolidColorBrush ?? Brushes.DodgerBlue;
            var fill = new SolidColorBrush(Color.FromArgb(110, accent.Color.R, accent.Color.G, accent.Color.B));
            dc.DrawGeometry(fill, new Pen(accent, 1), g);
        }

        DrawAnnotations(dc, scale);
    }

    private void DrawAnnotations(DrawingContext dc, double scale)
    {
        var dim = TryFindResource("TextDim") as Brush ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, dim, dpi);

        if (View is "Side" or "Top")
        {
            var stern = Text("Stern");
            var bow = Text("Bow");
            dc.DrawText(stern, new Point(10, 8));
            dc.DrawText(bow, new Point(ActualWidth - bow.Width - 10, 8));
        }
        if (View != "3D")
        {
            // Scale bar: 50 m (or 10 m when zoomed far out on a small hull).
            var metres = scale * 50 > ActualWidth / 3 ? 10 : 50;
            var w = metres * scale;
            var y = ActualHeight - 12;
            var pen = new Pen(dim, 1);
            dc.DrawLine(pen, new Point(12, y), new Point(12 + w, y));
            dc.DrawLine(pen, new Point(12, y - 4), new Point(12, y + 1));
            dc.DrawLine(pen, new Point(12 + w, y - 4), new Point(12 + w, y + 1));
            var label = Text($"{metres} m");
            dc.DrawText(label, new Point(16 + w, y - label.Height + 3));
        }
        else
        {
            var hint = Text("Drag to rotate · Shift-drag to move · wheel to zoom · double-click to reset");
            dc.DrawText(hint, new Point(10, ActualHeight - hint.Height - 6));
        }
    }

    /// <summary>
    /// Keeps the hull from being dragged out of the panel: a hull smaller than the panel stays fully inside it, a hull
    /// zoomed larger than the panel always covers it, so there is never more than the margin of empty space at an edge.
    /// </summary>
    private void ClampPan(Rect fit, double scale, double margin)
    {
        var limX = Math.Abs(ActualWidth - 2 * margin - fit.Width * scale) / 2;
        var limY = Math.Abs(ActualHeight - 2 * margin - fit.Height * scale) / 2;
        _pan = new Vector(Math.Clamp(_pan.X, -limX, limX), Math.Clamp(_pan.Y, -limY, limY));
    }

    private static Rect FitBox(ArmourMesh mesh, Matrix4x4 rot)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? mesh.Min.X : mesh.Max.X, (i & 2) == 0 ? mesh.Min.Y : mesh.Max.Y, (i & 4) == 0 ? mesh.Min.Z : mesh.Max.Z);
            var p = Vector3.Transform(corner, rot);
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public const string NoThickness = "No thickness";
    private static readonly Color NoneColour = Color.FromRgb(0x55, 0x60, 0x6F);
    private static int ColourKey(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    // ------------------------------------------------------------------ interaction

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        _dragStart = _dragLast = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragged && (p - start).Length < 3) return;
        _dragged = true;
        var d = p - _dragLast;
        _dragLast = p;
        if (View == "3D" && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            _yaw += (float)(d.X * 0.01);
            _pitch = Math.Clamp(_pitch + (float)(d.Y * 0.01), -MathF.PI / 2, MathF.PI / 2);
        }
        else
        {
            _pan += d;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragStart is not null && !_dragged) SelectedKey = HitTest(e.GetPosition(this));
        _dragStart = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.2f : 1 / 1.2f;
        var newZoom = Math.Clamp(_zoom * factor, 0.5f, 12f);
        factor = newZoom / _zoom;
        // Zoom about the cursor.
        var centre = new Point(ActualWidth / 2 + _pan.X, ActualHeight / 2 + _pan.Y);
        _pan += (centre - p) * (factor - 1);
        _zoom = newZoom;
        InvalidateVisual();
        e.Handled = true;
    }

    private int? HitTest(Point p)
    {
        for (var i = _drawn.Length - 1; i >= 0; i--)
        {
            var (a, b, c, key) = _drawn[i];
            if (Inside(p, a, b, c)) return key;
        }
        return null;
    }

    private static bool Inside(Point p, Point a, Point b, Point c)
    {
        static double Sign(Point p1, Point p2, Point p3) => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        var d1 = Sign(p, a, b);
        var d2 = Sign(p, b, c);
        var d3 = Sign(p, c, a);
        var neg = d1 < 0 || d2 < 0 || d3 < 0;
        var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
