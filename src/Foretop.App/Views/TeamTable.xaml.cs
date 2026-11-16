using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Foretop.App.Services;
using Foretop.App.ViewModels;
using Foretop.Core.Stats;

namespace Foretop.App.Views;

public partial class TeamTable : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(TeamTable), new PropertyMetadata(""));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(TeamTable), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(nameof(Rows), typeof(IEnumerable), typeof(TeamTable));

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(TeamSummary), typeof(TeamTable),
            new PropertyMetadata(null, (d, _) => ((TeamTable)d).UpdateSummary()));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(TeamTable),
            new PropertyMetadata(false, (d, e) => ((TeamTable)d).ApplyCompact((bool)e.NewValue)));

    public static readonly DependencyProperty ShowRecentProperty =
        DependencyProperty.Register(nameof(ShowRecent), typeof(bool), typeof(TeamTable),
            new PropertyMetadata(true, (d, _) => ((TeamTable)d).ApplyCompact(((TeamTable)d).Compact)));

    public bool ShowRecent { get => (bool)GetValue(ShowRecentProperty); set => SetValue(ShowRecentProperty, value); }

    /// <summary>Which statistics the columns show ("ALL TIERS", "TIER VI+"); shown next to the team name.</summary>
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(TeamTable), new PropertyMetadata(null));
    public string? Badge { get => (string?)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }

    public static readonly DependencyProperty BadgeTipProperty =
        DependencyProperty.Register(nameof(BadgeTip), typeof(string), typeof(TeamTable), new PropertyMetadata(null));
    public string? BadgeTip { get => (string?)GetValue(BadgeTipProperty); set => SetValue(BadgeTipProperty, value); }

    /// <summary>Overall and filtered values in two lines per cell: taller rows.</summary>
    public static readonly DependencyProperty TwoLineProperty =
        DependencyProperty.Register(nameof(TwoLine), typeof(bool), typeof(TeamTable),
            new PropertyMetadata(false, (d, _) => ((TeamTable)d).ApplyCompact(((TeamTable)d).Compact)));
    public bool TwoLine { get => (bool)GetValue(TwoLineProperty); set => SetValue(TwoLineProperty, value); }

    /// <summary>Tables sharing column widths (both teams), so columns line up; see <see cref="ColumnFitGroup"/>.</summary>
    public static readonly DependencyProperty FitGroupProperty =
        DependencyProperty.Register(nameof(FitGroup), typeof(ColumnFitGroup), typeof(TeamTable), new PropertyMetadata(null));
    public ColumnFitGroup? FitGroup { get => (ColumnFitGroup?)GetValue(FitGroupProperty); set => SetValue(FitGroupProperty, value); }

    private readonly HashSet<INotifyPropertyChanged> _watched = [];

    public TeamTable()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Vm?.ReapplySort();
            if (FitGroup is { } g)
            {
                g.HiddenChanged += ShowHidden;
                g.Add(Grid);
                ShowHidden();
            }
            FontManager.Changed += RequestFit;
        };
        Unloaded += (_, _) =>
        {
            if (FitGroup is { } g)
            {
                g.HiddenChanged -= ShowHidden;
                g.Remove(Grid);
            }
            FontManager.Changed -= RequestFit;
            foreach (var w in _watched) w.PropertyChanged -= OnRowChanged;
            _watched.Clear();
        };
        Grid.SizeChanged += (_, e) => { if (e.WidthChanged) RequestFit(); };
    }

    private void RequestFit() => FitGroup?.Request();

    // Names, clan tags, ships and values arrive after the row is shown: fit the columns again when a row changes.
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is INotifyPropertyChanged n && _watched.Add(n)) n.PropertyChanged += OnRowChanged;
        RequestFit();
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is INotifyPropertyChanged n && _watched.Remove(n)) n.PropertyChanged -= OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => RequestFit();

    private void ShowHidden()
    {
        var hidden = FitGroup?.Hidden ?? [];
        HiddenText.Text = hidden.Count == 0 ? "" : string.Join(", ", hidden) + " hidden";
        HiddenText.Visibility = hidden.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IEnumerable? Rows { get => (IEnumerable?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public TeamSummary? Summary { get => (TeamSummary?)GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private LiveBattleViewModel? Vm => DataContext as LiveBattleViewModel;

    private void ApplyCompact(bool compact)
    {
        // The view decides which columns exist; the column fitter decides which of them fit.
        Enable(XpColumn, !compact);
        Enable(RecentColumn, !compact && ShowRecent);
        Grid.RowHeight = TwoLine ? (compact ? 34 : 38) : compact ? 24 : 28;
        RequestFit();
    }

    private static void Enable(DataGridColumn column, bool on)
    {
        ColumnFit.SetEnabled(column, on);
        column.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var s = Summary;
        if (s is null) { SummaryText.Text = ""; return; }
        var inv = CultureInfo.CurrentCulture;
        var parts = new List<string>
        {
            "WR " + (s.AvgWinRate is double wr ? wr.ToString("0.0", inv) + "%" : "—"),
            "PR " + (s.AvgPr is double pr ? pr.ToString("0", inv) : "—"),
            "Dmg " + (s.AvgDamage is double d ? d.ToString("N0", inv) : "—"),
            "Battles " + (s.AvgBattles is double b ? b.ToString("N0", inv) : "—"),
        };
        var extra = new List<string>();
        if (s.Hidden > 0) extra.Add($"{s.Hidden} hidden");
        if (s.Bots > 0) extra.Add($"{s.Bots} bots");
        SummaryText.Text = "avg  " + string.Join("  ·  ", parts) + (extra.Count > 0 ? "   (" + string.Join(", ", extra) + ")" : "");
        SummaryText.ToolTip = $"Averages over {s.WithStats} of {s.Players} players with public statistics (derived from official API values). PR average over {s.PrCount} players (third-party).";
    }

    private PlayerRowViewModel? RowFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as PlayerRowViewModel ?? Grid.SelectedItem as PlayerRowViewModel;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is PlayerRowViewModel row) Vm?.OpenProfileCommand.Execute(row);
    }

    private void OnOpenProfile(object sender, RoutedEventArgs e) => Vm?.OpenProfileCommand.Execute(RowFrom(sender));

    private void OnOpenOfficial(object sender, RoutedEventArgs e) => Vm?.OpenOfficialProfileCommand.Execute(RowFrom(sender));

    private void OnOpenNumbers(object sender, RoutedEventArgs e) => Vm?.OpenWowsNumbersCommand.Execute(RowFrom(sender));

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row) Clipboard.SetText(row.Name);
    }
}
