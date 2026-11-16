namespace Foretop.Core.Layout;

/// <summary>One table column as the layout sees it.</summary>
/// <param name="Need">Width of its widest cell or header (untrimmed).</param>
/// <param name="Cells">Width of each cell, for squeezing name columns so that as few as possible are cut short.</param>
/// <param name="Flex">Share of spare width it receives (0 = exactly <paramref name="Need"/>).</param>
/// <param name="HidePriority">Hidden first when space runs out: higher first; 0 = never hidden.</param>
/// <param name="Enabled">False when the view turned the column off; it is then never shown.</param>
/// <param name="MinWidth">Narrowest a flexible column may be squeezed to.</param>
public sealed record LayoutColumn(double Need, IReadOnlyList<double> Cells, double Flex = 0, int HidePriority = 0,
    bool Enabled = true, double MinWidth = 0);

/// <summary>Widths per column (0 for columns not shown) and which columns were hidden for lack of width.</summary>
public sealed record ColumnLayoutResult(IReadOnlyList<double> Widths, IReadOnlyList<bool> Visible, IReadOnlyList<int> Hidden);

/// <summary>
/// Column widths for a table whose name columns must stay readable (Live Battle teams). Every shown column gets the
/// width of its content. Spare width goes to the flexible columns by their flex share. When the content does not fit,
/// columns are hidden by priority (highest first); only when nothing more may be hidden are the flexible columns
/// narrowed, splitting what is left so that the fewest cells are cut short (ties favour the first flexible column).
/// </summary>
public static class ColumnLayout
{
    public static ColumnLayoutResult Solve(IReadOnlyList<LayoutColumn> columns, double available)
    {
        var n = columns.Count;
        var visible = Enumerable.Range(0, n).Where(c => columns[c].Enabled).ToList();
        double Total() => visible.Sum(c => columns[c].Need);
        var hidden = new List<int>();
        foreach (var c in visible.Where(c => columns[c].HidePriority > 0).OrderByDescending(c => columns[c].HidePriority).ToList())
        {
            if (Total() <= available) break;
            visible.Remove(c);
            hidden.Add(c);
        }

        var widths = new double[n];
        foreach (var c in visible) widths[c] = columns[c].Need;
        var flex = visible.Where(c => columns[c].Flex > 0).ToList();
        if (flex.Count > 0)
        {
            var spare = available - Total();
            if (spare >= 0)
            {
                var weights = flex.Sum(c => columns[c].Flex);
                foreach (var c in flex) widths[c] += spare * columns[c].Flex / weights;
            }
            else
            {
                var room = Math.Max(0, available - visible.Except(flex).Sum(c => columns[c].Need));
                foreach (var (c, w) in Squeeze(columns, flex, room)) widths[c] = w;
            }
        }
        hidden.Sort();
        return new ColumnLayoutResult(widths, Enumerable.Range(0, n).Select(visible.Contains).ToList(), hidden);
    }

    /// <summary>Splits <paramref name="room"/> between the flexible columns, cutting as few cells as possible.</summary>
    private static IEnumerable<(int Column, double Width)> Squeeze(IReadOnlyList<LayoutColumn> columns, List<int> flex, double room)
    {
        if (flex.Count != 2)
        {
            var total = flex.Sum(c => columns[c].Need);
            return flex.Select(c => (c, Math.Max(columns[c].MinWidth, total > 0 ? room * columns[c].Need / total : room / flex.Count)));
        }
        LayoutColumn a = columns[flex[0]], b = columns[flex[1]];
        IReadOnlyList<double> ca = a.Cells.Count > 0 ? a.Cells : [a.Need], cb = b.Cells.Count > 0 ? b.Cells : [b.Need];
        var upper = Math.Max(a.MinWidth, room - b.MinWidth);
        // Candidates for the first column: exactly one of its cells, or exactly the room one of the other's cells leaves.
        var candidates = ca.Concat(cb.Select(w => room - w)).Append(room / 2).Select(w => Math.Clamp(w, a.MinWidth, upper)).Distinct();
        int Cut(double wa) => ca.Count(w => w > wa + 0.5) + cb.Count(w => w > room - wa + 0.5);
        var best = candidates.OrderBy(Cut).ThenByDescending(w => w).First();
        return [(flex[0], best), (flex[1], Math.Max(b.MinWidth, room - best))];
    }
}
