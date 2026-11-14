using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Broadside.App.Charts;

public sealed record ChartBar(string Label, double Value, string ValueText, Brush? Fill = null, string? Tooltip = null);

public sealed record ChartPoint(double X, double Y, string? XLabel = null);

public sealed record ChartSeries(string Name, Brush Stroke, IReadOnlyList<ChartPoint> Points);

/// <summary>Base for the small charts: shared text and brush helpers, redraw on data change.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected Brush Res(string key) => (Brush)FindResource(key);

    protected FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static void Redraw(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ChartBase)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= chart.OnItemsChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += chart.OnItemsChanged;
        chart.InvalidateMeasure();
        chart.InvalidateVisual();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }
}

/// <summary>Horizontal bars with a label column and the value printed after each bar.</summary>
public sealed class BarChart : ChartBase
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(BarChart), new PropertyMetadata(null, Redraw));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(BarChart),
            new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    private const double RowHeight = 22;

    private List<ChartBar> Bars => Items?.OfType<ChartBar>().ToList() ?? [];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, Math.Max(RowHeight, Bars.Count * RowHeight));

    protected override void OnRender(DrawingContext dc)
    {
        var bars = Bars;
        if (bars.Count == 0)
        {
            dc.DrawText(Text("No data yet", 12, Res("TextDim")), new Point(0, 3));
            return;
        }
        var max = Math.Max(bars.Max(b => b.Value), 1e-9);
        var valueSpace = bars.Max(b => Text(b.ValueText, 11.5, Res("TextDim")).Width) + 12;
        var barArea = Math.Max(10, ActualWidth - LabelWidth - valueSpace);
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var y = i * RowHeight;
            var label = Text(b.Label, 12, Res("Text"));
            label.MaxTextWidth = LabelWidth - 8;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(0, y + (RowHeight - label.Height) / 2));

            var w = Math.Max(2, barArea * Math.Max(0, b.Value) / max);
            dc.DrawRoundedRectangle(b.Fill ?? Res("Accent"), null, new Rect(LabelWidth, y + 5, w, RowHeight - 10), 2, 2);
            var value = Text(b.ValueText, 11.5, Res("TextDim"));
            dc.DrawText(value, new Point(LabelWidth + w + 6, y + (RowHeight - value.Height) / 2));
        }
    }
}

/// <summary>Line chart for one or more series, with y-axis labels and an optional reference line.</summary>
public sealed class LineChart : ChartBase
{
    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(IEnumerable), typeof(LineChart), new PropertyMetadata(null, Redraw));

    public static readonly DependencyProperty ReferenceProperty =
        DependencyProperty.Register(nameof(Reference), typeof(double?), typeof(LineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(LineChart),
            new FrameworkPropertyMetadata("0.#", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty =
        DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(LineChart),
            new FrameworkPropertyMetadata("Not enough data yet", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Series { get => (IEnumerable?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public double? Reference { get => (double?)GetValue(ReferenceProperty); set => SetValue(ReferenceProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var series = Series?.OfType<ChartSeries>().Where(s => s.Points.Count > 0).ToList() ?? [];
        var w = ActualWidth;
        var h = ActualHeight;
        if (series.Count == 0 || series.All(s => s.Points.Count < 2))
        {
            var t = Text(EmptyText, 12, Res("TextDim"));
            t.MaxTextWidth = Math.Max(10, w - 8);
            dc.DrawText(t, new Point(4, Math.Max(0, h / 2 - t.Height / 2)));
            return;
        }

        const double left = 46, bottom = 20, top = 8, right = 10;
        var pts = series.SelectMany(s => s.Points).ToList();
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        if (Reference is double r) { minY = Math.Min(minY, r); maxY = Math.Max(maxY, r); }
        var pad = Math.Max((maxY - minY) * 0.1, Math.Abs(maxY) * 0.01 + 1e-6);
        minY -= pad;
        maxY += pad;
        if (maxX - minX < 1e-9) maxX = minX + 1;

        double X(double x) => left + (x - minX) / (maxX - minX) * (w - left - right);
        double Y(double y) => top + (maxY - y) / (maxY - minY) * (h - top - bottom);

        var grid = new Pen(Res("Border"), 1);
        for (var i = 0; i <= 3; i++)
        {
            var v = minY + (maxY - minY) * i / 3;
            var y = Y(v);
            dc.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = Text(v.ToString(Format, CultureInfo.CurrentCulture), 10.5, Res("TextDim"));
            dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
        }

        if (Reference is double refY)
        {
            var dash = new Pen(Res("TextDim"), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(dash, new Point(left, Y(refY)), new Point(w - right, Y(refY)));
        }

        foreach (var s in series)
        {
            var pen = new Pen(s.Stroke, 2) { LineJoin = PenLineJoin.Round };
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(s.Points[0].X), Y(s.Points[0].Y)), false, false);
                foreach (var p in s.Points.Skip(1)) ctx.LineTo(new Point(X(p.X), Y(p.Y)), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            if (s.Points.Count <= 40)
                foreach (var p in s.Points) dc.DrawEllipse(s.Stroke, null, new Point(X(p.X), Y(p.Y)), 2.5, 2.5);
        }

        var first = pts.OrderBy(p => p.X).First();
        var last = pts.OrderBy(p => p.X).Last();
        if (first.XLabel is not null)
            dc.DrawText(Text(first.XLabel, 10.5, Res("TextDim")), new Point(left, h - bottom + 4));
        if (last.XLabel is not null)
        {
            var t = Text(last.XLabel, 10.5, Res("TextDim"));
            dc.DrawText(t, new Point(w - right - t.Width, h - bottom + 4));
        }
    }
}

/// <summary>Two bars growing from the centre: friendly to the left, enemy to the right.</summary>
public sealed class DualBar : ChartBase
{
    public static readonly DependencyProperty LeftProperty =
        DependencyProperty.Register(nameof(Left), typeof(double?), typeof(DualBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RightProperty =
        DependencyProperty.Register(nameof(Right), typeof(double?), typeof(DualBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Left { get => (double?)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public double? Right { get => (double?)GetValue(RightProperty); set => SetValue(RightProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var mid = w / 2;
        dc.DrawRectangle(Res("Panel2"), null, new Rect(0, h / 2 - 3, w, 6));
        var max = Math.Max(Math.Abs(Left ?? 0), Math.Abs(Right ?? 0));
        if (max > 0)
        {
            if (Left is double l)
            {
                var lw = (mid - 2) * Math.Abs(l) / max;
                dc.DrawRoundedRectangle(Res("Friendly"), null, new Rect(mid - 1 - lw, h / 2 - 4, lw, 8), 2, 2);
            }
            if (Right is double r)
            {
                var rw = (mid - 2) * Math.Abs(r) / max;
                dc.DrawRoundedRectangle(Res("Enemy"), null, new Rect(mid + 1, h / 2 - 4, rw, 8), 2, 2);
            }
        }
        dc.DrawRectangle(Res("TextDim"), null, new Rect(mid - 0.5, 0, 1, h));
    }
}
