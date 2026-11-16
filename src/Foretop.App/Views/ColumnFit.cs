using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Foretop.Core.Layout;

namespace Foretop.App.Views;

/// <summary>Per-column settings for <see cref="ColumnFitGroup"/>, set in XAML on DataGrid columns.</summary>
public static class ColumnFit
{
    /// <summary>Share of spare width this column receives (0 = exactly as wide as its content). Name columns flex.</summary>
    public static readonly DependencyProperty FlexProperty =
        DependencyProperty.RegisterAttached("Flex", typeof(double), typeof(ColumnFit), new PropertyMetadata(0.0));
    public static double GetFlex(DependencyObject o) => (double)o.GetValue(FlexProperty);
    public static void SetFlex(DependencyObject o, double v) => o.SetValue(FlexProperty, v);

    /// <summary>When the table is too narrow, columns with a higher number are hidden first; 0 = never hidden.</summary>
    public static readonly DependencyProperty HidePriorityProperty =
        DependencyProperty.RegisterAttached("HidePriority", typeof(int), typeof(ColumnFit), new PropertyMetadata(0));
    public static int GetHidePriority(DependencyObject o) => (int)o.GetValue(HidePriorityProperty);
    public static void SetHidePriority(DependencyObject o, int v) => o.SetValue(HidePriorityProperty, v);

    /// <summary>False when the view turns the column off (compact mode, no data for it); the fitter never shows it then.</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ColumnFit), new PropertyMetadata(true));
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
}

/// <summary>
/// Column widths shared by DataGrids with the same columns (the two Live Battle team tables), sized to what the cells
/// actually show. Each column's need is measured from the rendered content (widest cell or header), so fonts, themes,
/// two-line cells and tags are covered without fixed pixel values; <see cref="ColumnLayout"/> then decides the widths
/// and which optional columns are hidden. Every grid of the group gets the same widths, so columns line up.
/// </summary>
public sealed class ColumnFitGroup
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private readonly List<DataGrid> _grids = [];
    private readonly Dictionary<int, double> _measured = [];
    private readonly Dictionary<int, List<double>> _cells = [];
    private bool _pending;
    private int _settle;

    /// <summary>Headers of the columns hidden for lack of width (empty when everything fits).</summary>
    public IReadOnlyList<string> Hidden { get; private set; } = [];

    public event Action? HiddenChanged;

    public void Add(DataGrid grid)
    {
        if (!_grids.Contains(grid)) _grids.Add(grid);
        Request();
    }

    public void Remove(DataGrid grid) => _grids.Remove(grid);

    /// <summary>Fit again after the current layout pass (coalesced: many requests, one fit).</summary>
    public void Request()
    {
        if (_pending || _grids.Count == 0) return;
        _pending = true;
        _grids[0].Dispatcher.BeginInvoke(DispatcherPriority.Background, Fit);
    }

    private void Fit()
    {
        _pending = false;
        var grids = _grids.Where(g => g.IsLoaded && g.IsVisible && g.ActualWidth > 0).ToList();
        if (grids.Count == 0) return;
        var count = grids[0].Columns.Count;
        if (grids.Any(g => g.Columns.Count != count)) return;
        var available = grids.Min(Viewport);
        if (available <= 0) return;
        var cols = grids[0].Columns;

        // What each column needs: measured where it is shown; a hidden column keeps its last measurement.
        var need = new double[count];
        var unmeasured = false;
        for (var c = 0; c < count; c++)
        {
            var shown = grids.Where(g => g.Columns[c].Visibility == Visibility.Visible).ToList();
            if (shown.Count > 0)
            {
                var cells = new List<double>();
                _measured[c] = shown.Max(g => Measure(g, g.Columns[c], cells));
                _cells[c] = cells;
            }
            if (_measured.TryGetValue(c, out var n)) need[c] = n;
            else { need[c] = Math.Max(cols[c].MinWidth, 40); unmeasured = true; }
        }

        var layout = ColumnLayout.Solve(Enumerable.Range(0, count).Select(c => new LayoutColumn(need[c],
            _cells.GetValueOrDefault(c) ?? [], ColumnFit.GetFlex(cols[c]), ColumnFit.GetHidePriority(cols[c]),
            ColumnFit.GetEnabled(cols[c]), cols[c].MinWidth)).ToList(), available);

        var newlyShown = false;
        foreach (var g in grids)
            for (var c = 0; c < count; c++)
            {
                var col = g.Columns[c];
                var show = layout.Visible[c];
                if (show && col.Visibility != Visibility.Visible) newlyShown = true;
                col.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;
                var w = Math.Floor(layout.Widths[c]);
                if (!col.Width.IsAbsolute || Math.Abs(col.Width.Value - w) >= 1) col.Width = new DataGridLength(w);
            }

        var names = layout.Hidden.Select(c => cols[c].Header?.ToString() ?? "").ToList();
        if (!names.SequenceEqual(Hidden))
        {
            Hidden = names;
            HiddenChanged?.Invoke();
        }

        // A column shown for the first time (or again) is measured on the next pass; settle at most twice in a row.
        if ((newlyShown || unmeasured) && _settle < 2) { _settle++; Request(); }
        else _settle = 0;
    }

    /// <summary>Width the column needs: its widest realised cell or its header, at their natural (untrimmed) size. The
    /// width of every cell is added to <paramref name="cells"/>.</summary>
    private static double Measure(DataGrid grid, DataGridColumn col, List<double> cells)
    {
        double max = 0;
        foreach (var header in Descendants<DataGridColumnHeader>(grid).Where(h => h.Column == col))
        {
            header.Measure(Unbounded);
            max = Math.Max(max, header.DesiredSize.Width);
            header.InvalidateMeasure();
        }
        for (var i = 0; i < grid.Items.Count; i++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row) continue;
            if (col.GetCellContent(row) is not FrameworkElement content) continue;
            content.Measure(Unbounded);
            var w = content.DesiredSize.Width;
            content.InvalidateMeasure();
            if (Ancestor<DataGridCell>(content) is { } cell)
                w += cell.Padding.Left + cell.Padding.Right + cell.BorderThickness.Left + cell.BorderThickness.Right;
            cells.Add(Math.Ceiling(w) + 1);
            max = Math.Max(max, w);
        }
        return Math.Ceiling(max) + 1;
    }

    /// <summary>Width the columns can use: the scroll area without a vertical scroll bar.</summary>
    private static double Viewport(DataGrid grid) =>
        Descendants<ScrollContentPresenter>(grid).FirstOrDefault() is { ActualWidth: > 0 } p ? p.ActualWidth - 1 : grid.ActualWidth - 20;

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
