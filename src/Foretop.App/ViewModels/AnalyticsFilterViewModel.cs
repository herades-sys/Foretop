using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Personal;
using Foretop.Core.Stats;

namespace Foretop.App.ViewModels;

/// <summary>A period choice: newest N battles, a number of days back, a calendar month, everything or a custom range.</summary>
public sealed record PeriodChoice(string Label, int? LastBattles = null, int? Days = null, int? MonthsBack = null, bool Custom = false)
{
    public override string ToString() => Label;
    public bool IsAll => LastBattles is null && Days is null && MonthsBack is null && !Custom;
}

/// <summary>A tier shortcut of the toolbar (VI+, VI–VIII…).</summary>
public sealed record TierPresetOption(string Label, int[] Tiers);

/// <summary>An active filter shown as a small removable label.</summary>
public sealed record ActiveFilter(string Text, IRelayCommand Remove);

/// <summary>A ship in the ship picker.</summary>
public sealed partial class ShipPick(ShipMeta ship, int battles, Action changed) : ObservableObject
{
    public ShipMeta Ship { get; } = ship;
    public string Label { get; } = $"{(ship.Tier is int t ? ShipRanking.Roman(t) : "?")} {ship.Name}";
    public int Battles { get; } = battles;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// The Analytics filter toolbar: data source, tiers (single, several, range, minimum), minimum battles per ship, period,
/// class, nation, ships and battle type. <see cref="Changed"/> fires once per change (coalesced); <see cref="Build"/>
/// gives the filter for the current choices. Kept for the session.
/// </summary>
public sealed partial class AnalyticsFilterViewModel : ObservableObject
{
    private bool _quiet;
    private int? _lastTierClicked;

    public AnalyticsFilterViewModel()
    {
        TierChips = Enumerable.Range(1, 11).Select(t => new FilterChip(ShipRanking.Roman(t), t, OnTierChipChanged)).ToList();
        ClassChips = new (string Label, string Key)[]
            {
                ("DD", "Destroyer"), ("CA", "Cruiser"), ("BB", "Battleship"), ("CV", "AirCarrier"), ("SS", "Submarine"),
            }
            .Select((c, i) => new FilterChip(c.Label, i, Raise) { Tip = AnalyticsGroups.ClassName(c.Key) }).ToList();
        _classKeys = ["Destroyer", "Cruiser", "Battleship", "AirCarrier", "Submarine"];
        _minBattles = MinBattlesOptions[0];
        _period = Periods[0];
        _mode = Modes[0];
        _nation = NationAll;
    }

    public event Action? Changed;

    private readonly string[] _classKeys;

    // ---- source ----
    [ObservableProperty] private AnalyticsSource _source = AnalyticsSource.Account;
    public bool IsRecorded => Source == AnalyticsSource.Recorded;
    partial void OnSourceChanged(AnalyticsSource value)
    {
        OnPropertyChanged(nameof(IsRecorded));
        Raise();
    }

    // ---- tiers ----
    public IReadOnlyList<FilterChip> TierChips { get; }
    public bool AllTiers
    {
        get => !TierChips.Any(c => c.IsChecked);
        set
        {
            if (value) SetTiers([]);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<TierPresetOption> TierPresets { get; } =
    [
        new("VI+", [6, 7, 8, 9, 10, 11]), new("VIII+", [8, 9, 10, 11]), new("X+", [10, 11]), new("VI–VIII", [6, 7, 8]), new("IX–XI", [9, 10, 11]),
    ];

    [RelayCommand]
    private void TierPreset(string? label)
    {
        if (TierPresets.FirstOrDefault(p => p.Label == label) is { Tiers: { } tiers }) SetTiers(tiers);
    }

    /// <summary>Selects exactly these tiers (empty = all).</summary>
    public void SetTiers(IEnumerable<int> tiers)
    {
        var set = tiers.ToHashSet();
        _quiet = true;
        foreach (var c in TierChips) c.IsChecked = set.Contains(c.Value);
        _quiet = false;
        OnPropertyChanged(nameof(AllTiers));
        Raise();
    }

    /// <summary>
    /// A click on a tier chip (from the view). With Shift: every tier from the last clicked one to this one, and true
    /// (the view then cancels the normal toggle). Without: remembered as the start of the next range.
    /// </summary>
    public bool TierClicked(int tier, bool shift)
    {
        if (shift && _lastTierClicked is int from)
        {
            var (a, b) = from <= tier ? (from, tier) : (tier, from);
            SetTiers(Enumerable.Range(a, b - a + 1));
            _lastTierClicked = tier;
            return true;
        }
        _lastTierClicked = tier;
        return false;
    }

    private void OnTierChipChanged()
    {
        if (_quiet) return;
        OnPropertyChanged(nameof(AllTiers));
        Raise();
    }

    // ---- minimum battles per ship ----
    public IReadOnlyList<MinBattlesOption> MinBattlesOptions { get; } = [new(1), new(5), new(10), new(20), new(50), new(100), new(null)];
    [ObservableProperty] private MinBattlesOption _minBattles;
    [ObservableProperty] private string _customMinBattles = "20";
    public bool IsCustomMin => MinBattles.Value is null;
    public int MinBattlesValue => MinBattles.Value ?? (int.TryParse(CustomMinBattles, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n >= 1 ? n : 1);
    partial void OnMinBattlesChanged(MinBattlesOption value) { OnPropertyChanged(nameof(IsCustomMin)); Raise(); }
    partial void OnCustomMinBattlesChanged(string value) { if (IsCustomMin) Raise(); }

    // ---- period (recorded battles only) ----
    public IReadOnlyList<PeriodChoice> Periods { get; } =
    [
        new("All time"), new("Last 7 days", Days: 7), new("Last 30 days", Days: 30), new("Last 90 days", Days: 90),
        new("This month", MonthsBack: 0), new("Last month", MonthsBack: 1),
        new("Last 10 battles", LastBattles: 10), new("Last 20 battles", LastBattles: 20), new("Last 50 battles", LastBattles: 50),
        new("Last 100 battles", LastBattles: 100), new("Date range…", Custom: true),
    ];
    [ObservableProperty] private PeriodChoice _period;
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    public bool IsCustomPeriod => Period.Custom;
    partial void OnPeriodChanged(PeriodChoice value) { OnPropertyChanged(nameof(IsCustomPeriod)); Raise(); }
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustomPeriod) Raise(); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustomPeriod) Raise(); }

    public string CustomFromText
    {
        get => CustomFrom?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        set { if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)) CustomFrom = d; }
    }

    public string CustomToText
    {
        get => CustomTo?.ToString("d", CultureInfo.CurrentCulture) ?? "";
        set { if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)) CustomTo = d; }
    }

    // ---- class ----
    public IReadOnlyList<FilterChip> ClassChips { get; }

    // ---- nation ----
    public static readonly FilterOption<string?> NationAll = new("All nations", null);
    public ObservableCollection<FilterOption<string?>> Nations { get; } = [NationAll];
    [ObservableProperty] private FilterOption<string?> _nation;
    partial void OnNationChanged(FilterOption<string?> value) => Raise();

    /// <summary>Fills the nation list with the nations present in the data, keeping the choice.</summary>
    public void SetNations(IEnumerable<string> keys)
    {
        var current = Nation?.Value;
        _quiet = true;
        Nations.Clear();
        Nations.Add(NationAll);
        foreach (var k in keys.Distinct().OrderBy(NationName)) Nations.Add(new FilterOption<string?>(NationName(k), k));
        Nation = Nations.FirstOrDefault(n => n.Value == current) ?? NationAll;
        _quiet = false;
    }

    public static string NationName(string key) => key switch
    {
        "usa" => "U.S.A.", "uk" => "U.K.", "ussr" => "U.S.S.R.", "pan_asia" => "Pan-Asia", "pan_america" => "Pan-America",
        "commonwealth" => "Commonwealth", "europe" => "Europe", "netherlands" => "The Netherlands", "italy" => "Italy",
        "japan" => "Japan", "germany" => "Germany", "france" => "France", "spain" => "Spain",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ')),
    };

    // ---- ships ----
    public ObservableCollection<ShipPick> ShipPicks { get; } = [];
    [ObservableProperty] private string _shipSearch = "";
    [ObservableProperty] private bool _isShipPickerOpen;
    public string ShipButtonText => ShipPicks.Count(p => p.IsChecked) is var n && n == 0 ? "All ships" : n == 1 ? ShipPicks.First(p => p.IsChecked).Label : $"{n} ships";

    partial void OnShipSearchChanged(string value)
    {
        foreach (var p in ShipPicks) p.IsVisible = value.Length == 0 || p.Label.Contains(value, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>The ships to pick from (those in the data), keeping the checked ones.</summary>
    public void SetShips(IEnumerable<(ShipMeta Ship, int Battles)> ships)
    {
        var chosen = ShipPicks.Where(p => p.IsChecked).Select(p => p.Ship.Id).ToHashSet();
        _quiet = true;
        ShipPicks.Clear();
        foreach (var (ship, battles) in ships.OrderBy(s => s.Ship.Tier ?? 0).ThenBy(s => s.Ship.Name))
            ShipPicks.Add(new ShipPick(ship, battles, OnShipPicked) { IsChecked = chosen.Contains(ship.Id) });
        _quiet = false;
        OnPropertyChanged(nameof(ShipButtonText));
    }

    private void OnShipPicked()
    {
        if (_quiet) return;
        OnPropertyChanged(nameof(ShipButtonText));
        Raise();
    }

    [RelayCommand]
    private void ClearShips()
    {
        _quiet = true;
        foreach (var p in ShipPicks) p.IsChecked = false;
        _quiet = false;
        OnPropertyChanged(nameof(ShipButtonText));
        Raise();
    }

    // ---- battle type (recorded battles only) ----
    public IReadOnlyList<FilterOption<string?>> Modes { get; } =
        [new("All battle types", null), new("Random", ResultModes.Random), new("Ranked", ResultModes.Ranked), new("Co-op", ResultModes.Coop)];
    [ObservableProperty] private FilterOption<string?> _mode;
    partial void OnModeChanged(FilterOption<string?> value) => Raise();

    // ---- result ----

    public ObservableCollection<ActiveFilter> Active { get; } = [];
    public bool HasActive => Active.Count > 0;

    /// <summary>The filter for the current choices. Date range and battle type only for recorded battles.</summary>
    public AnalyticsFilter Build()
    {
        var f = new AnalyticsFilter
        {
            Tiers = TierChips.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet(),
            MinBattles = MinBattlesValue,
            Classes = ClassChips.Where(c => c.IsChecked).Select(c => _classKeys[c.Value]).ToHashSet(),
            Nations = Nation?.Value is { } n ? new HashSet<string> { n } : new HashSet<string>(),
            Ships = ShipPicks.Where(p => p.IsChecked).Select(p => p.Ship.Id).ToHashSet(),
        };
        if (!IsRecorded) return f;
        var now = DateTimeOffset.Now;
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        return f with
        {
            Mode = Mode.Value,
            LastBattles = Period.LastBattles,
            From = Period.Custom && CustomFrom is DateTime cf ? new DateTimeOffset(cf.Date)
                : Period.Days is int d ? now.AddDays(-d)
                : Period.MonthsBack is int m ? month.AddMonths(-m) : null,
            To = Period.Custom && CustomTo is DateTime ct ? new DateTimeOffset(ct.Date.AddDays(1))
                : Period.MonthsBack is int m2 ? month.AddMonths(-m2 + 1) : null,
        };
    }

    [RelayCommand]
    private void ClearAll()
    {
        _quiet = true;
        foreach (var c in TierChips.Concat(ClassChips)) c.IsChecked = false;
        foreach (var p in ShipPicks) p.IsChecked = false;
        MinBattles = MinBattlesOptions[0];
        Period = Periods[0];
        Mode = Modes[0];
        Nation = NationAll;
        _quiet = false;
        OnPropertyChanged(nameof(AllTiers));
        OnPropertyChanged(nameof(ShipButtonText));
        Raise();
    }

    private void Raise()
    {
        if (_quiet) return;
        UpdateActive();
        Changed?.Invoke();
    }

    private void UpdateActive()
    {
        Active.Clear();
        var tiers = TierChips.Where(c => c.IsChecked).Select(c => c.Value).ToList();
        if (tiers.Count > 0) Active.Add(new ActiveFilter(ShipRanking.TierLabel(tiers), new RelayCommand(() => SetTiers([]))));
        if (MinBattlesValue > 1) Active.Add(new ActiveFilter($"{MinBattlesValue:N0}+ battles per ship", new RelayCommand(() => MinBattles = MinBattlesOptions[0])));
        if (IsRecorded && !Period.IsAll)
            Active.Add(new ActiveFilter(Period.Custom ? $"{CustomFromText} – {CustomToText}" : Period.Label, new RelayCommand(() => Period = Periods[0])));
        var classes = ClassChips.Where(c => c.IsChecked).ToList();
        if (classes.Count > 0)
            Active.Add(new ActiveFilter(string.Join(", ", classes.Select(c => AnalyticsGroups.ClassName(_classKeys[c.Value]))), new RelayCommand(() =>
            {
                _quiet = true;
                foreach (var c in ClassChips) c.IsChecked = false;
                _quiet = false;
                Raise();
            })));
        if (Nation?.Value is not null) Active.Add(new ActiveFilter(Nation.Label, new RelayCommand(() => Nation = NationAll)));
        if (ShipPicks.Any(p => p.IsChecked)) Active.Add(new ActiveFilter(ShipButtonText, new RelayCommand(ClearShips)));
        if (IsRecorded && Mode.Value is not null) Active.Add(new ActiveFilter(Mode.Label, new RelayCommand(() => Mode = Modes[0])));
        OnPropertyChanged(nameof(HasActive));
    }
}

/// <summary>
/// A chart's own filter (class and tier) on top of the global one. The chart uses the intersection; it can only
/// narrow the global selection. <see cref="IsActive"/> drives the "chart filter" label and its reset button.
/// </summary>
public sealed partial class ChartFilter : ObservableObject
{
    private static readonly string[] ClassKeys = ["Destroyer", "Cruiser", "Battleship", "AirCarrier", "Submarine"];
    private readonly Action _changed;
    private bool _quiet;

    public ChartFilter(Action changed, bool withTiers = false)
    {
        _changed = changed;
        ClassChips = new[] { "DD", "CA", "BB", "CV", "SS" }.Select((l, i) => new FilterChip(l, i, Raise) { Tip = AnalyticsGroups.ClassName(ClassKeys[i]) }).ToList();
        TierChips = withTiers ? Enumerable.Range(1, 11).Select(t => new FilterChip(ShipRanking.Roman(t), t, Raise)).ToList() : [];
    }

    public IReadOnlyList<FilterChip> ClassChips { get; }
    public IReadOnlyList<FilterChip> TierChips { get; }
    public bool HasTierChips => TierChips.Count > 0;

    public bool IsActive => ClassChips.Any(c => c.IsChecked) || TierChips.Any(c => c.IsChecked);

    /// <summary>"Chart filter: Battleships, Tier VIII" (empty when none).</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();
            var classes = ClassChips.Where(c => c.IsChecked).Select(c => AnalyticsGroups.ClassName(ClassKeys[c.Value])).ToList();
            if (classes.Count > 0) parts.Add(string.Join(", ", classes));
            var tiers = TierChips.Where(c => c.IsChecked).Select(c => c.Value).ToList();
            if (tiers.Count > 0) parts.Add(ShipRanking.TierLabel(tiers));
            return parts.Count == 0 ? "" : "Chart filter on top of the toolbar: " + string.Join(" · ", parts);
        }
    }

    public AnalyticsFilter Filter => new()
    {
        Classes = ClassChips.Where(c => c.IsChecked).Select(c => ClassKeys[c.Value]).ToHashSet(),
        Tiers = TierChips.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet(),
    };

    [RelayCommand]
    private void Reset()
    {
        _quiet = true;
        foreach (var c in ClassChips.Concat(TierChips)) c.IsChecked = false;
        _quiet = false;
        Raise();
    }

    private void Raise()
    {
        if (_quiet) return;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Description));
        _changed();
    }
}
