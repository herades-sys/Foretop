using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.GameData;

namespace Foretop.App.ViewModels;

public enum ShipSection
{
    Database,
    Armour,
    Statistics,
    Comparison,
    Equipment,
}

/// <summary>One row of the ship list (values of the fully upgraded ship).</summary>
public sealed partial class ShipRow(ShipData ship) : ObservableObject
{
    public ShipData Ship { get; } = ship;
    [ObservableProperty] private bool _isSelected;

    public string Name => Ship.DisplayName;
    public string Nation => Ship.NationName;
    public string Class => ShipFacts.ClassName(Ship.Species);
    public int Tier => Ship.Tier;
    public string TierText => ShipFacts.TierRoman(Ship.Tier);
    public string Availability => Ship.Availability;
    public double? HitPoints => Ship.Top.HitPoints;
    public string HitPointsText => ShipFacts.N0(Ship.Top.HitPoints);
    public string MainBattery => ShipFacts.MainSummary(Ship.Top);
    public double? Range => Ship.Top.Main?.Range;
    public string RangeText => ShipFacts.Km(Range);
    public double? Reload => Ship.Top.Main?.Reload;
    public string ReloadText => ShipFacts.Sec(Reload);
    public double? Speed => Ship.Top.MaxSpeed;
    public string SpeedText => ShipFacts.Kn(Speed);
    public double? Detect => Ship.Top.DetectSea;
    public string DetectText => ShipFacts.Km(Detect);
    public double? ArmourMax => Ship.Top.ArmourMax;
    public string ArmourText => Ship.Top.ArmourMin is double a && Ship.Top.ArmourMax is double b
        ? $"{a.ToString("0", CultureInfo.CurrentCulture)}–{b.ToString("0", CultureInfo.CurrentCulture)}" : ShipFacts.Missing;
}

/// <summary>A comparison line: one value per compared ship, with bar lengths relative to the row's largest value.</summary>
public sealed record CompareRow(string Label, string? Tag, IReadOnlyList<CompareCell> Cells, bool IsHeader = false, bool LowerIsBetter = false);

/// <summary>Ratio = value against the largest in the row (bar length); IsBest = best in the row, which is the smallest where lower is better.</summary>
public sealed record CompareCell(string Text, double Ratio, bool IsBest);

public sealed record ModuleRow(string Type, string Name, string Research, string Credits, bool Stock, bool Top);

public sealed record DiffRow(string Label, string Stock, string Top, string Change);

public sealed record UpgradeOption(string Name, string Effects, bool ShipSpecific);

public sealed record UpgradeColumn(string Title, IReadOnlyList<UpgradeOption> Options);

/// <summary>One of the player's own ships: results from the Wargaming API, name and class from the game files.</summary>
public sealed class OwnShipRow(Foretop.Core.Stats.ShipPerformance perf, ShipData? ship)
{
    public ShipData? Ship { get; } = ship;
    public string Name => Ship?.DisplayName ?? $"Ship {perf.ShipId} (not in the game files)";
    public int Tier => Ship?.Tier ?? 0;
    public string TierText => Ship is null ? "?" : ShipFacts.TierRoman(Ship.Tier);
    public string Class => Ship is null ? "" : ShipFacts.ClassName(Ship.Species);
    public string Nation => Ship?.NationName ?? "";
    public int Battles => perf.Totals.Battles;
    public double? WinRate => perf.Totals.WinRate;
    public string WinRateText => WinRate is double w ? w.ToString("0.0", CultureInfo.CurrentCulture) + "%" : ShipFacts.Missing;
    public double? AvgDamage => perf.Totals.AvgDamage;
    public string AvgDamageText => ShipFacts.N0(AvgDamage);
    public double? AvgXp => perf.Totals.AvgXp;
    public string AvgXpText => ShipFacts.N0(AvgXp);
    public double? AvgFrags => perf.Totals.AvgFrags;
    public string AvgFragsText => AvgFrags?.ToString("0.00", CultureInfo.CurrentCulture) ?? ShipFacts.Missing;
    public double? Survival => perf.Totals.SurvivalRate;
    public string SurvivalText => Survival is double s ? s.ToString("0", CultureInfo.CurrentCulture) + "%" : ShipFacts.Missing;
    public double? Pr => perf.Pr;
    public string PrText => Pr?.ToString("N0", CultureInfo.CurrentCulture) ?? ShipFacts.Missing;
    public DateTime? LastBattle => perf.LastBattle?.LocalDateTime;
    public string LastBattleText => LastBattle?.ToString("d", CultureInfo.CurrentCulture) ?? ShipFacts.Missing;
}

public sealed partial class ZoneToggle(string label, string zone, string range, bool visible) : ObservableObject
{
    public string Label { get; } = label;
    public string Zone { get; } = zone;
    public string Range { get; } = range;
    [ObservableProperty] private bool _isVisible = visible;
}

public sealed record ArmourRow(string Zone, string Plate, string Thickness, double ThicknessValue, string Layers, Brush Swatch);

/// <summary>
/// Ship Intelligence: data read from the installed game client (via <see cref="ShipDataStore"/>), shown in the
/// design's five sections. Values are for the selected configuration (stock or fully upgraded), before upgrades
/// and commander skills.
/// </summary>
public sealed partial class ShipIntelligenceViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private ShipDataSet? _loaded;

    public ShipIntelligenceViewModel(AppHost host)
    {
        _host = host;
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ShipRow r && PassesFilter(r);
        View.SortDescriptions.Add(new SortDescription(nameof(ShipRow.Tier), ListSortDirection.Descending));
        View.SortDescriptions.Add(new SortDescription(nameof(ShipRow.Name), ListSortDirection.Ascending));
        host.ShipData.Changed += () => _ui.BeginInvoke(Refresh);
        Refresh();
    }

    public event Action? SettingsRequested;

    // ------------------------------------------------------------------ section and state

    [ObservableProperty] private ShipSection _section = ShipSection.Database;
    [ObservableProperty] private string _sectionTitle = "Ship Database";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _stateKind = "Idle"; // NoGame, Reading, Ready, Failed
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private string? _failure;

    partial void OnSectionChanged(ShipSection value) => UpdateTitle();

    private void UpdateTitle() => SectionTitle = Section switch
    {
        ShipSection.Database when ShowProfile && Current is not null => Current.DisplayName,
        ShipSection.Database => "Ship Database",
        ShipSection.Armour => "Armour Viewer",
        ShipSection.Statistics => "Ship Statistics",
        ShipSection.Comparison => "Ship Comparison",
        ShipSection.Equipment => "Equipment & Upgrades",
        _ => "",
    };

    public void OnShown()
    {
        if (_host.ShipData.State is ShipDataState.Idle or ShipDataState.NoGame or ShipDataState.Failed) App.StartShipData(_host);
        Refresh();
    }

    private void Refresh()
    {
        var store = _host.ShipData;
        StateKind = store.State switch
        {
            ShipDataState.Ready => "Ready",
            ShipDataState.Reading when store.Data is not null => "Ready",
            ShipDataState.Reading => "Reading",
            ShipDataState.NoGame when store.Data is not null => "Ready",
            ShipDataState.NoGame => "NoGame",
            ShipDataState.Failed when store.Data is not null => "Ready",
            ShipDataState.Failed => "Failed",
            _ => "Reading",
        };
        var (done, total) = store.Progress;
        ProgressValue = total > 0 ? (double)done / total : 0;
        ProgressText = total > 0 ? $"Building ship records · {done:N0} of {total:N0}" : "Opening the game files";
        Banner = store.State == ShipDataState.Reading && store.ShowingOlderBuild
            ? "The game was updated. Ship data is being read again; the data from the previous build stays in use until it is done."
            : null;
        Failure = store.State == ShipDataState.Failed ? store.Message : null;
        SourceText = store.Data is { } d ? $"Read from your game client · build {d.Build} · {d.Ships.Count:N0} ships" : "";

        if (store.Data is not null && !ReferenceEquals(store.Data, _loaded)) Load(store.Data);
    }

    [RelayCommand]
    private void ChooseGameFolder() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void Retry() => App.StartShipData(_host);

    // ------------------------------------------------------------------ database

    public ObservableCollection<ShipRow> Rows { get; } = [];
    public ListCollectionView View { get; }
    public ObservableCollection<string> NationOptions { get; } = ["All nations"];
    public IReadOnlyList<string> TierOptions { get; } = ["All tiers", "★", "X", "IX", "VIII", "VII", "VI", "V", "IV", "III", "II", "I"];
    public IReadOnlyList<string> AvailabilityOptions { get; } =
        ["Obtainable ships", "All ships", "Tech tree", "Premium", "Special", "Supership", "Event", "Not obtainable"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _nation = "All nations";
    [ObservableProperty] private string _tier = "All tiers";
    [ObservableProperty] private string _availability = "Obtainable ships";
    [ObservableProperty] private bool _showDD = true;
    [ObservableProperty] private bool _showCA = true;
    [ObservableProperty] private bool _showBB = true;
    [ObservableProperty] private bool _showCV = true;
    [ObservableProperty] private bool _showSS = true;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private int _selectedCount;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnNationChanged(string value) => ApplyFilter();
    partial void OnTierChanged(string value) => ApplyFilter();
    partial void OnAvailabilityChanged(string value) => ApplyFilter();
    partial void OnShowDDChanged(bool value) => ApplyFilter();
    partial void OnShowCAChanged(bool value) => ApplyFilter();
    partial void OnShowBBChanged(bool value) => ApplyFilter();
    partial void OnShowCVChanged(bool value) => ApplyFilter();
    partial void OnShowSSChanged(bool value) => ApplyFilter();

    private void Load(ShipDataSet set)
    {
        _loaded = set;
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var s in set.Ships)
        {
            var row = new ShipRow(s);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        var nations = set.Ships.Where(s => s.Obtainable).Select(s => s.NationName).Distinct().OrderBy(n => n, StringComparer.CurrentCulture).ToList();
        NationOptions.Clear();
        NationOptions.Add("All nations");
        foreach (var n in nations) NationOptions.Add(n);
        ApplyFilter();
        if (Current is not null && _host.ShipData.Find(Current.Name) is { } again) Open(again);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShipRow.IsSelected)) return;
        SelectedCount = Rows.Count(r => r.IsSelected);
        CompareSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool PassesFilter(ShipRow r)
    {
        var s = r.Ship;
        if (Availability == "Obtainable ships" ? !s.Obtainable : Availability != "All ships" && s.Availability != Availability) return false;
        if (Nation != "All nations" && s.NationName != Nation) return false;
        if (Tier != "All tiers" && r.TierText != Tier) return false;
        var classOk = s.Species switch
        {
            "Destroyer" => ShowDD, "Cruiser" => ShowCA, "Battleship" => ShowBB, "AirCarrier" => ShowCV, "Submarine" => ShowSS, _ => true,
        };
        if (!classOk) return false;
        var q = SearchText.Trim();
        return q.Length == 0 || s.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               s.NationName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               ShipFacts.ClassName(s.Species).Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               string.Equals(r.TierText, q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        View.Refresh();
        CountText = $"{View.Count:N0} of {Rows.Count:N0} ships";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Rows.Where(r => r.IsSelected)) r.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void CompareSelected()
    {
        Compared.Clear();
        foreach (var r in Rows.Where(r => r.IsSelected).Take(4)) Compared.Add(r.Ship);
        BuildComparison();
        Section = ShipSection.Comparison;
    }

    private bool CanCompare() => SelectedCount >= 2;

    // ------------------------------------------------------------------ profile

    [ObservableProperty] private ShipData? _current;
    [ObservableProperty] private bool _showProfile;
    [ObservableProperty] private bool _useTop = true;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private IReadOnlyList<FactRow> _keyFigures = [];
    [ObservableProperty] private FactSection? _general;
    [ObservableProperty] private FactSection? _mainArmament;
    [ObservableProperty] private IReadOnlyList<ShellCard> _shells = [];
    [ObservableProperty] private IReadOnlyList<FactRow> _turretArmour = [];
    [ObservableProperty] private IReadOnlyList<FactRow> _summary = [];
    [ObservableProperty] private string _shipTags = "";

    partial void OnUseTopChanged(bool value) => BuildProfile();

    [RelayCommand]
    private void OpenRow(ShipRow? row)
    {
        if (row is not null) Open(row.Ship);
    }

    public void Open(ShipData ship)
    {
        Current = ship;
        ShowProfile = true;
        Section = ShipSection.Database;
        BuildProfile();
        BuildEquipment();
        BuildArmour();
        UpdateTitle();
    }

    /// <summary>Top-bar search: opens a ship by name when the data is loaded.</summary>
    public bool TryOpenShip(string name)
    {
        if (_host.ShipData.FindByDisplayName(name) is not { } ship) return false;
        Open(ship);
        return true;
    }

    [RelayCommand]
    private void BackToList()
    {
        ShowProfile = false;
        UpdateTitle();
    }

    [RelayCommand]
    private void AddCurrentToComparison()
    {
        if (Current is null) return;
        if (!Compared.Contains(Current))
        {
            if (Compared.Count >= 4) Compared.RemoveAt(0);
            Compared.Add(Current);
        }
        BuildComparison();
        Section = ShipSection.Comparison;
    }

    [RelayCommand]
    private void ShowProfileSection()
    {
        if (Current is null) return;
        ShowProfile = true;
        Section = ShipSection.Database;
        UpdateTitle();
    }

    [RelayCommand]
    private void ShowEquipment() => Section = ShipSection.Equipment;

    [RelayCommand]
    private void ShowArmour() => Section = ShipSection.Armour;

    private ShipConfig Config(ShipData s) => UseTop ? s.Top : s.Stock;

    private void BuildProfile()
    {
        if (Current is not { } s) return;
        var c = Config(s);
        KeyFigures = ShipFacts.KeyFigures(c);
        General = ShipFacts.General(c);
        MainArmament = ShipFacts.MainArmament(c);
        Shells = ShipFacts.Shells(c);
        TurretArmour = c.Main?.TurretArmour.Select(p => new FactRow(p.Title, ShipFacts.Mm(p.Thickness))).ToList() ?? [];
        Summary = ShipFacts.Summary(s, c, _host.ShipData.Data?.Upgrades ?? []);
        ShipTags = string.Join("  ·  ", new[]
        {
            s.NationName, "Tier " + ShipFacts.TierRoman(s.Tier), ShipFacts.ClassName(s.Species), s.Availability,
            s.Year is null ? null : "Commissioned " + s.Year, s.IsPaperShip ? "Paper ship" : null,
        }.Where(x => x is not null));
        Preview = LoadImage(_host.ShipData.PreviewPath(s));
        BuildArmour();
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (path is null) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ comparison

    public ObservableCollection<ShipData> Compared { get; } = [];
    [ObservableProperty] private IReadOnlyList<CompareRow> _comparisonRows = [];
    [ObservableProperty] private string _comparisonTitle = "";

    [RelayCommand]
    private void RemoveCompared(ShipData? ship)
    {
        if (ship is null) return;
        Compared.Remove(ship);
        BuildComparison();
    }

    private void BuildComparison()
    {
        ComparisonTitle = $"{Compared.Count} of 4 ships · any tier, nation and class";
        var rows = new List<CompareRow>();
        var configs = Compared.Select(Config).ToList();

        void Header(string t) => rows.Add(new CompareRow(t, null, [], IsHeader: true));
        void Row(string label, Func<ShipConfig, double?> value, Func<double?, string> fmt, string? tag = null, Func<ShipConfig, string?>? text = null,
            bool lowerIsBetter = false)
        {
            var values = configs.Select(value).ToList();
            var present = values.Where(v => v.HasValue).Select(v => Math.Abs(v!.Value)).ToList();
            var max = present.DefaultIfEmpty(0).Max();
            var best = present.Count < 2 ? double.NaN : lowerIsBetter ? present.Min() : present.Max();
            rows.Add(new CompareRow(label, tag, configs.Select((c, i) =>
            {
                var v = values[i];
                var t = text?.Invoke(c) ?? fmt(v);
                return new CompareCell(v is null && text is null ? "Not in the game data" : t,
                    max > 0 && v is double d ? Math.Abs(d) / max : 0, v is double dv && Math.Abs(Math.Abs(dv) - best) < 1e-9);
            }).ToList(), LowerIsBetter: lowerIsBetter));
        }

        ShellData? Ap(ShipConfig c) => c.Main?.Shells.FirstOrDefault(s => s.Type == "AP");
        ShellData? He(ShipConfig c) => c.Main?.Shells.FirstOrDefault(s => s.Type == "HE");

        Header("SURVIVABILITY");
        Row("Hit points", c => c.HitPoints, ShipFacts.N0);
        Row("Torpedo protection", c => c.TorpedoProtection, ShipFacts.Pct);
        Row("Hull armour", c => c.ArmourMax, ShipFacts.Mm, null, c => ShipFacts.ArmourRange(c));
        Header("MAIN BATTERY");
        Row("Guns", c => c.Main?.Guns, ShipFacts.N0, null, c => ShipFacts.MainSummary(c));
        Row("Reload time", c => c.Main?.Reload, ShipFacts.Sec, lowerIsBetter: true);
        Row("Maximum range", c => c.Main?.Range, ShipFacts.Km);
        Row("AP shell damage", c => Ap(c)?.Damage, ShipFacts.N0);
        Row("HE shell damage", c => He(c)?.Damage, ShipFacts.N0);
        Row("Chance to start a fire", c => He(c)?.FireChance * 100, ShipFacts.Pct);
        Row("HE penetration", c => He(c)?.HePenetration, ShipFacts.Mm);
        Row("AP penetration at 10 km", c => Ap(c) is { Krupp: > 0 } ap && c.Main!.Range >= 10 ? Ballistics.At(ap, 10)?.BeltPenetration : null,
            v => ShipFacts.Mm(v is double d ? Math.Round(d) : null), "calculated");
        Row("AP damage per minute", c => Ap(c) is { } ap && c.Main is { Reload: > 0 } m ? ap.Damage * m.Guns * 60 / m.Reload : null, ShipFacts.N0, "theoretical");
        Row("Dispersion at maximum range", c => c.Main?.Dispersion, v => v is double d ? d.ToString("0", CultureInfo.CurrentCulture) + " m" : ShipFacts.Missing,
            lowerIsBetter: true);
        Header("TORPEDOES");
        Row("Torpedo damage", c => c.Torpedoes?.Damage, ShipFacts.N0);
        Row("Torpedo range", c => c.Torpedoes?.Range, ShipFacts.Km);
        Row("Torpedo speed", c => c.Torpedoes?.Speed, ShipFacts.Kn);
        Row("Torpedo reload", c => c.Torpedoes?.Reload, ShipFacts.Sec, lowerIsBetter: true);
        Header("ANTI-AIRCRAFT");
        Row("Short range", c => c.AntiAir?.ShortDps, ShipFacts.N0);
        Row("Medium range", c => c.AntiAir?.MediumDps, ShipFacts.N0);
        Row("Long range", c => c.AntiAir?.LongDps, ShipFacts.N0);
        Header("MOBILITY AND CONCEALMENT");
        Row("Maximum speed", c => c.MaxSpeed, ShipFacts.Kn);
        Row("Turning circle radius", c => c.TurningRadius, v => v is double d ? d.ToString("N0", CultureInfo.CurrentCulture) + " m" : ShipFacts.Missing,
            lowerIsBetter: true);
        Row("Rudder shift time", c => c.RudderTime, ShipFacts.Sec, lowerIsBetter: true);
        Row("Surface detectability", c => c.DetectSea, ShipFacts.Km, lowerIsBetter: true);
        Row("Air detectability", c => c.DetectAir, ShipFacts.Km, lowerIsBetter: true);
        ComparisonRows = rows;
    }

    // ------------------------------------------------------------------ equipment

    [ObservableProperty] private IReadOnlyList<DiffRow> _differences = [];
    [ObservableProperty] private IReadOnlyList<ModuleRow> _modules = [];
    [ObservableProperty] private IReadOnlyList<UpgradeColumn> _upgradeColumns = [];
    [ObservableProperty] private IReadOnlyList<FactRow> _consumables = [];
    [ObservableProperty] private string _upgradesTitle = "";

    private void BuildEquipment()
    {
        if (Current is not { } s) return;
        var up = _host.ShipData.Data?.Upgrades ?? [];
        Modules = s.Modules.Select(m => new ModuleRow(ModuleType(m.Type), m.DisplayName,
            m.CostXP is > 0 ? m.CostXP.Value.ToString("N0", CultureInfo.CurrentCulture) + " XP" : "—",
            m.CostCR is > 0 ? m.CostCR.Value.ToString("N0", CultureInfo.CurrentCulture) : "—", m.InStock, m.InTop)).ToList();

        var diffs = new List<DiffRow>();
        void D(string label, double? a, double? b, Func<double?, string> fmt, string unit = "")
        {
            if (a is null && b is null) return;
            if (a is double x && b is double y && Math.Abs(x - y) < 1e-6) return;
            var change = a is double p && b is double q ? (q - p >= 0 ? "+" : "−") + Math.Abs(q - p).ToString("0.##", CultureInfo.CurrentCulture) + unit : "";
            diffs.Add(new DiffRow(label, fmt(a), fmt(b), change));
        }
        var st = s.Stock;
        var tp = s.Top;
        if (st.Torpedoes?.TorpedoName != tp.Torpedoes?.TorpedoName)
            diffs.Add(new DiffRow("Torpedo", st.Torpedoes?.TorpedoName ?? "—", tp.Torpedoes?.TorpedoName ?? "—", "module"));
        if (st.Main?.GunName != tp.Main?.GunName)
            diffs.Add(new DiffRow("Main battery", st.Main?.GunName ?? "—", tp.Main?.GunName ?? "—", "module"));
        D("Hit points", st.HitPoints, tp.HitPoints, ShipFacts.N0);
        D("Maximum speed", st.MaxSpeed, tp.MaxSpeed, ShipFacts.Kn, " kn");
        D("Rudder shift time", st.RudderTime, tp.RudderTime, ShipFacts.Sec, " s");
        D("Surface detectability", st.DetectSea, tp.DetectSea, ShipFacts.Km, " km");
        D("Main battery range", st.Main?.Range, tp.Main?.Range, ShipFacts.Km, " km");
        D("Main battery reload", st.Main?.Reload, tp.Main?.Reload, ShipFacts.Sec, " s");
        D("Torpedo damage", st.Torpedoes?.Damage, tp.Torpedoes?.Damage, ShipFacts.N0);
        D("Torpedo speed", st.Torpedoes?.Speed, tp.Torpedoes?.Speed, ShipFacts.Kn, " kn");
        D("Torpedo range", st.Torpedoes?.Range, tp.Torpedoes?.Range, ShipFacts.Km, " km");
        D("Torpedo detectability", st.Torpedoes?.Detectability, tp.Torpedoes?.Detectability, ShipFacts.Km, " km");
        D("Torpedo reload", st.Torpedoes?.Reload, tp.Torpedoes?.Reload, ShipFacts.Sec, " s");
        D("Flooding chance", st.Torpedoes?.FloodChance, tp.Torpedoes?.FloodChance, ShipFacts.Pct, " pts");
        D("AA short range", st.AntiAir?.ShortDps, tp.AntiAir?.ShortDps, ShipFacts.N0);
        D("AA long range", st.AntiAir?.LongDps, tp.AntiAir?.LongDps, ShipFacts.N0);
        Differences = diffs;

        UpgradeColumns = s.UpgradeSlots.Select(slot => new UpgradeColumn($"SLOT {slot.Slot + 1} · {slot.Options.Count} OPTIONS",
            slot.Options.Select(n => up.TryGetValue(n, out var u)
                ? new UpgradeOption(u.DisplayName.Replace('\n', ' '), u.Effects.Count == 0 ? "No effects listed in the game data" : string.Join(" · ", u.Effects), u.ShipSpecific)
                : new UpgradeOption(n, "", false)).ToList())).ToList();
        UpgradesTitle = $"{s.UpgradeSlots.Count} slots · {s.UpgradeSlots.Sum(x => x.Options.Count)} options · every upgrade this ship can mount, with the effects the game data gives it";
        Consumables = s.Consumables.Select(c => new FactRow($"Slot {c.Slot + 1}",
            string.Join("  or  ", c.Options.Select(o => o.DisplayName +
                (o.Charges is int n ? (n < 0 ? " · unlimited" : $" · {n} charges") : "") +
                (o.WorkTime is double w && w > 0 ? $" · {w:0.#} s" : "") +
                (o.Reload is double r ? $" · reload {r:0} s" : ""))))).ToList();
    }

    private static string ModuleType(string ucType) => ucType switch
    {
        "_Hull" => "Hull", "_Artillery" => "Main battery", "_Torpedoes" => "Torpedoes", "_Suo" => "Gun fire control", "_Engine" => "Engine",
        "_Fighter" => "Fighters", "_DiveBomber" => "Dive bombers", "_TorpedoBomber" => "Torpedo bombers", "_SkipBomber" => "Skip bombers",
        "_FlightControl" => "Flight control", "_Sonar" => "Sonar", _ => ucType.TrimStart('_'),
    };

    // ------------------------------------------------------------------ statistics (own results, Wargaming API)

    public ObservableCollection<OwnShipRow> OwnShips { get; } = [];
    [ObservableProperty] private string _statsTitle = "";
    [ObservableProperty] private string? _statsMessage;
    [ObservableProperty] private bool _statsLoading;
    [ObservableProperty] private IReadOnlyList<FactRow> _statsCards = [];
    private string? _statsFor;

    partial void OnSectionChanged(ShipSection oldValue, ShipSection newValue)
    {
        if (newValue == ShipSection.Statistics) _ = LoadOwnStatsAsync(false);
        if (newValue == ShipSection.Armour) _ = LoadArmourMeshAsync();
    }

    [RelayCommand]
    private Task ReloadStats() => LoadOwnStatsAsync(true);

    [RelayCommand]
    private void OpenOwnShip(OwnShipRow? row)
    {
        if (row?.Ship is { } s) Open(s);
    }

    private async Task LoadOwnStatsAsync(bool force)
    {
        var name = _host.Settings.Current.PlayerName;
        if (string.IsNullOrWhiteSpace(name))
        {
            StatsMessage = "Your account name is not known yet. It is picked up from the first battle Foretop sees, or you can enter it in Settings under Your account.";
            return;
        }
        if (!_host.Api.HasApplicationId)
        {
            StatsMessage = "Add a Wargaming application ID in Settings to load your ship results.";
            return;
        }
        if (!force && _statsFor == name && OwnShips.Count > 0) return;
        if (StatsLoading) return;
        StatsLoading = true;
        StatsMessage = null;
        try
        {
            var result = await _host.Stats.LoadAsync("ships:" + name, _host.Region, [new Core.Stats.PlayerRequest(name, 1, 0, false)],
                Core.Api.RequestPriority.Normal, CancellationToken.None, force);
            var p = result[0];
            _statsFor = name;
            OwnShips.Clear();
            if (p.Hidden) { StatsMessage = "The profile is hidden, so the Wargaming API returns no ship results."; return; }
            if (p.Ships is not { Count: > 0 } ships) { StatsMessage = p.StatusText ?? "No random-battle results on record for this account."; return; }
            foreach (var s in ships) OwnShips.Add(new OwnShipRow(s, _host.ShipData.Data?.Ships.FirstOrDefault(x => x.Id == s.ShipId)));
            var battles = ships.Sum(s => s.Totals.Battles);
            var wins = ships.Sum(s => s.Totals.Wins);
            var dmg = ships.Sum(s => s.Totals.Damage);
            StatsTitle = $"{p.Name} · random battles · {ships.Count:N0} ships played" +
                         (p.StatsFetchedAt is { } at ? $" · fetched {at.LocalDateTime:g}" : "") + (p.StaleData ? " · offline copy" : "");
            StatsCards =
            [
                new("Ships played", ships.Count.ToString("N0", CultureInfo.CurrentCulture)),
                new("Battles", battles.ToString("N0", CultureInfo.CurrentCulture)),
                new("Win rate", battles > 0 ? (100.0 * wins / battles).ToString("0.0", CultureInfo.CurrentCulture) + "%" : ShipFacts.Missing),
                new("Average damage", battles > 0 ? ((double)dmg / battles).ToString("N0", CultureInfo.CurrentCulture) : ShipFacts.Missing),
                new("Personal rating", p.Pr?.ToString("N0", CultureInfo.CurrentCulture) ?? ShipFacts.Missing),
            ];
        }
        catch (Exception ex)
        {
            StatsMessage = "Could not load your ship results: " + ex.Message;
        }
        finally
        {
            StatsLoading = false;
        }
    }

    // ------------------------------------------------------------------ armour

    [ObservableProperty] private IReadOnlyList<ArmourRow> _armourRows = [];
    [ObservableProperty] private string _armourTitle = "";

    private void BuildArmour()
    {
        if (Current is not { } s) return;
        var plates = s.Armour.Where(p => p.Thickness > 0).ToList();
        ArmourTitle = plates.Count == 0
            ? "The game data lists no armour plates for this ship."
            : $"{plates.Count} plates in {plates.Select(p => p.Zone).Distinct().Count()} zones · {ShipFacts.ArmourRange(s.Top)}";
        ArmourRows = plates.GroupBy(p => (p.Zone, p.Title))
            .Select(g =>
            {
                var layers = g.OrderBy(p => p.Layer).Select(p => p.Thickness.ToString("0", CultureInfo.CurrentCulture)).ToList();
                var max = g.Max(p => p.Thickness);
                return new ArmourRow(g.Key.Zone, g.Key.Title, layers.Count > 1 ? string.Join(" · ", layers) + " mm" : layers[0] + " mm", max,
                    layers.Count > 1 ? $"{layers.Count} layers" : "", ArmourBrush(max));
            })
            .OrderBy(r => r.Zone).ThenByDescending(r => r.ThicknessValue).ToList();

        // Viewer: one entry per armour key, thickness from the game data (0 = a key the data gives no thickness, e.g. section boundaries).
        var map = new Dictionary<int, Views.PlateInfo>();
        foreach (var p in s.Armour)
            map[p.Key] = new Views.PlateInfo(p.Key, p.Title, p.Thickness > 0 ? p.Zone : Views.ArmourCanvas.NoThickness, p.Thickness, ArmourColour(p.Thickness));
        PlateMap = map;
        SelectedPlateKey = null;
        if (Section == ShipSection.Armour) _ = LoadArmourMeshAsync();
    }

    // ------------------------------------------------------------------ armour viewer

    public IReadOnlyList<string> ArmourViews { get; } = ["Profile + plan", "Side", "Top", "Bow", "3D"];
    [ObservableProperty] private string _armourView = "Profile + plan";
    [ObservableProperty] private ArmourMesh? _armourMesh;
    [ObservableProperty] private string? _armourMeshMessage;
    [ObservableProperty] private bool _armourMeshLoading;
    [ObservableProperty] private IReadOnlyDictionary<int, Views.PlateInfo>? _plateMap;
    [ObservableProperty] private IReadOnlyCollection<string> _hiddenZones = new HashSet<string> { Views.ArmourCanvas.NoThickness };
    [ObservableProperty] private int? _selectedPlateKey;
    [ObservableProperty] private IReadOnlyList<FactRow> _selectedPlate = [];
    [ObservableProperty] private string _selectedPlateTitle = "";
    public ObservableCollection<ZoneToggle> Zones { get; } = [];
    public bool ShowTwoViews => ArmourView == "Profile + plan";
    public string SingleView => ArmourView == "Profile + plan" ? "Side" : ArmourView;
    private string? _meshFor;

    partial void OnArmourViewChanged(string value)
    {
        OnPropertyChanged(nameof(ShowTwoViews));
        OnPropertyChanged(nameof(SingleView));
    }

    partial void OnSelectedPlateKeyChanged(int? value) => BuildPlateCard();

    [RelayCommand]
    private void SetArmourView(string? view)
    {
        if (view is not null && ArmourViews.Contains(view)) ArmourView = view;
    }

    private async Task LoadArmourMeshAsync()
    {
        if (Current is not { } s || _meshFor == s.Name) return;
        _meshFor = s.Name;
        ArmourMeshLoading = true;
        ArmourMeshMessage = null;
        ArmourMesh = null;
        try
        {
            var mesh = await _host.ShipData.LoadArmourAsync(s);
            if (Current?.Name != s.Name) return;
            ArmourMesh = mesh;
            ArmourMeshMessage = mesh is null ? "No armour model was found for this hull in the game files. The armour table still lists every plate." : null;
            BuildZones();
            BuildPlateCard();
        }
        catch (Exception ex)
        {
            ArmourMeshMessage = "The armour model could not be read: " + ex.Message;
            _meshFor = null;
        }
        finally
        {
            ArmourMeshLoading = false;
        }
    }

    private void BuildZones()
    {
        foreach (var z in Zones) z.PropertyChanged -= OnZoneToggled;
        Zones.Clear();
        if (ArmourMesh is null || PlateMap is null) return;
        var keys = ArmourMesh.Triangles.Select(t => t.Key).ToHashSet();
        var byZone = keys.Select(k => PlateMap.TryGetValue(k, out var p) ? p : new Views.PlateInfo(k, "", Views.ArmourCanvas.NoThickness, 0, default))
            .GroupBy(p => p.Zone).OrderBy(g => g.Key == Views.ArmourCanvas.NoThickness).ThenBy(g => g.Key);
        foreach (var g in byZone)
        {
            var mm = g.Where(p => p.Thickness > 0).Select(p => p.Thickness).ToList();
            var range = mm.Count == 0 ? $"{g.Count()} plates · 0 mm"
                : mm.Min() == mm.Max() ? $"{mm.Max():0} mm" : $"{mm.Min():0}–{mm.Max():0} mm";
            var z = new ZoneToggle(g.Key == Views.ArmourCanvas.NoThickness ? "Section boundaries" : g.Key, g.Key, range, !HiddenZones.Contains(g.Key));
            z.PropertyChanged += OnZoneToggled;
            Zones.Add(z);
        }
    }

    private void OnZoneToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ZoneToggle.IsVisible)) return;
        HiddenZones = Zones.Where(z => !z.IsVisible).Select(z => z.Zone).ToHashSet();
    }

    [RelayCommand]
    private void ShowAllZones()
    {
        foreach (var z in Zones) z.IsVisible = true;
    }

    [RelayCommand]
    private void CitadelOnly()
    {
        foreach (var z in Zones) z.IsVisible = z.Zone is "Citadel" or "Turrets" or "Steering gear";
    }

    private void BuildPlateCard()
    {
        if (SelectedPlateKey is not int key || ArmourMesh is null || PlateMap is null)
        {
            SelectedPlate = [];
            SelectedPlateTitle = "";
            return;
        }
        var info = PlateMap.TryGetValue(key, out var p) ? p : null;
        var material = ArmourMaterials.Name(key & 0xFFFF);
        var tris = ArmourMesh.Triangles.Where(t => t.Key == key).ToList();
        var xs = tris.SelectMany(t => new[] { t.A.X, t.B.X, t.C.X }).ToList();
        var ys = tris.SelectMany(t => new[] { t.A.Y, t.B.Y, t.C.Y }).ToList();
        var bow = ArmourMesh.Max.X;
        static string M(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);
        static string Height(double y) => Math.Abs(y) < 0.05 ? "the waterline" : y < 0 ? $"{M(-y)} m below the waterline" : $"{M(y)} m above the waterline";
        var mm = info?.Thickness ?? 0;
        SelectedPlateTitle = info?.Title is { Length: > 0 } t ? t : material.Replace('_', ' ');
        SelectedPlate =
        [
            new("Thickness", mm > 0 ? $"{mm:0} mm" : "0 mm · no thickness in the game data"),
            new("Class", ThicknessClass(mm)),
            new("Zone", info is { Thickness: > 0 } ? info.Zone : ShipExtractor.Zone(material)),
            new("Layer", (key >> 16).ToString(CultureInfo.CurrentCulture)),
            new("Location", $"{M(bow - xs.Max())}–{M(bow - xs.Min())} m from the bow"),
            new("Height", $"from {Height(ys.Min())} to {Height(ys.Max())}"),
            new("Triangles", tris.Count.ToString("N0", CultureInfo.CurrentCulture)),
            new("Game ID", $"{material} · key {key}"),
        ];
    }

    private static string ThicknessClass(double mm) => mm switch
    {
        <= 0 => "None",
        < 20 => "Very light · under 20 mm",
        < 50 => "Light · 20–49 mm",
        < 150 => "Medium · 50–149 mm",
        < 300 => "Heavy · 150–299 mm",
        _ => "Very heavy · 300 mm and more",
    };

    private static Color ArmourColour(double mm) => ArmourBrush(mm) is SolidColorBrush b ? b.Color : Colors.Gray;

    /// <summary>Design colour scale: one hue, brighter (dark theme) = thicker.</summary>
    public static Brush ArmourBrush(double mm) => RatingBrushConverter.Res(mm switch
    {
        < 20 => "Armour1",
        < 50 => "Armour2",
        < 150 => "Armour3",
        < 300 => "Armour4",
        _ => "Armour5",
    });
}
