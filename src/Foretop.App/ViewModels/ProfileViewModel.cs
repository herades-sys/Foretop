using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Foretop.App.Charts;
using Foretop.Core.Api;
using Foretop.Core.Game;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

/// <summary>A labelled number on the profile. Origin says where it comes from.</summary>
public sealed record StatCard(string Label, string Value, string? Sub, string Origin, Brush? Accent = null);

public sealed class ShipStatRow : ObservableObject
{
    public ShipStatRow(ShipPerformance p, ShipInfo? ship)
    {
        Perf = p;
        Ship = ship;
    }

    public ShipPerformance Perf { get; }
    public ShipInfo? Ship { get; }
    public string ShipName => Ship?.Name ?? $"Unlisted ship {Perf.ShipId}";
    public int Tier => Ship?.Tier ?? 0;
    public string TierText => Ship?.TierRoman ?? "?";
    public ShipClass ShipClass => Ship?.Class ?? ShipClass.Unknown;
    public string ClassShort => Ship?.ClassShort ?? "?";
    public System.Windows.Media.ImageSource? Icon =>
        Services.ShipIconCache.Instance?.Get(Ship?.ImageSmall, () => OnPropertyChanged(nameof(Icon)));
    public int Battles => Perf.Totals.Battles;
    public double? WinRate => Perf.Totals.WinRate;
    public double? WinRateRated => Battles >= 10 ? WinRate : null;
    public double? AvgDamage => Perf.Totals.AvgDamage;
    public double? AvgXp => Perf.Totals.AvgXp;
    public double? AvgFrags => Perf.Totals.AvgFrags;
    public double? Pr => Perf.Pr;
    public double? PrRated => Battles >= 10 ? Pr : null;
    public DateTime? LastBattle => Perf.LastBattle?.LocalDateTime;
    public string WinRateText => WinRate is double w ? w.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "—";
    public string AvgDamageText => AvgDamage?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    public string AvgXpText => AvgXp?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    public string AvgFragsText => AvgFrags?.ToString("0.00", CultureInfo.CurrentCulture) ?? "—";
    public string PrText => Pr?.ToString("0", CultureInfo.CurrentCulture) ?? "—";
    public string LastBattleText => LastBattle?.ToString("d") ?? "—";
}

/// <summary>
/// A tier or class button above a player's ship list. None checked = no filter; checked ones are combined
/// (any checked tier and any checked class), like the ship carousel filters.
/// </summary>
public sealed partial class FilterChip(string label, int value, Action changed) : ObservableObject
{
    public string Label { get; } = label;
    public int Value { get; } = value;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _tip = "";
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>A minimum-battles choice for Best performing ships; Value null = custom.</summary>
public sealed record MinBattlesOption(int? Value)
{
    public override string ToString() => Value is int v ? $"{v}+ battles" : "Custom…";
}

public sealed record EncounterRow(long BattleId, DateTime When, string Battle, string Side, string Ship, string StatsAtTheTime);

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private bool _suppressSearch;

    public ProfileViewModel(AppHost host)
    {
        _host = host;
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, async (_, _) => await SearchAsync(),
            Dispatcher.CurrentDispatcher);
        _searchTimer.Stop();
        TierChips = Enumerable.Range(1, 11).Select(t => new FilterChip(t == 11 ? "★" : Roman(t), t, ApplyShipFilter)).ToList();
        ClassChips = new (string Label, ShipClass Cls)[]
            {
                ("DD", ShipClass.Destroyer), ("CA", ShipClass.Cruiser), ("BB", ShipClass.Battleship),
                ("CV", ShipClass.AirCarrier), ("SS", ShipClass.Submarine),
            }
            .Select(c => new FilterChip(c.Label, (int)c.Cls, ApplyShipFilter)).ToList();
        BestTierChips = ShipRanking.Tiers.Select(t => new FilterChip(ShipRanking.Roman(t), t, OnBestFilterChanged)).ToList();
        MinBattlesOptions = [new(1), new(5), new(10), new(DefaultMinBattles), new(50), new(100), new(null)];
        _selectedMinBattles = MinBattlesOptions[3];
    }

    // ---- Best performing ships: tier and minimum-battles filter (kept for the session, across players) ----

    private const int DefaultMinBattles = 20;
    private bool _resettingBest;
    private int _lastCustomMin = DefaultMinBattles;

    public IReadOnlyList<FilterChip> BestTierChips { get; }
    public IReadOnlyList<MinBattlesOption> MinBattlesOptions { get; }
    [ObservableProperty] private MinBattlesOption _selectedMinBattles;
    [ObservableProperty] private string _customMinBattles = DefaultMinBattles.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _bestShipsTitle = $"Best performing ships (PR, {DefaultMinBattles}+ battles)";
    [ObservableProperty] private string _bestShipsSummary = "";
    [ObservableProperty] private string? _bestShipsEmpty;
    [ObservableProperty] private string? _customMinError;
    [ObservableProperty] private bool _isBestFilterActive;

    public bool IsCustomMin => SelectedMinBattles.Value is null;

    /// <summary>The "All tiers" chip: checked when no tier is chosen; checking it clears the tiers.</summary>
    public bool AllBestTiers
    {
        get => !BestTierChips.Any(c => c.IsChecked);
        set
        {
            if (value) ClearBestTiers();
            OnPropertyChanged();
        }
    }

    /// <summary>The minimum in force: the chosen option, or the last valid custom number.</summary>
    public int MinBattles => SelectedMinBattles.Value ?? _lastCustomMin;

    partial void OnSelectedMinBattlesChanged(MinBattlesOption value)
    {
        OnPropertyChanged(nameof(IsCustomMin));
        if (value.Value is null) ParseCustomMin();
        OnBestFilterChanged();
    }

    partial void OnCustomMinBattlesChanged(string value)
    {
        if (!IsCustomMin) return;
        ParseCustomMin();
        OnBestFilterChanged();
    }

    private void ParseCustomMin()
    {
        if (int.TryParse(CustomMinBattles.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n >= 1)
        {
            _lastCustomMin = n;
            CustomMinError = null;
        }
        else CustomMinError = $"Enter a whole number of 1 or more (using {_lastCustomMin:N0}).";
    }

    [RelayCommand]
    private void ClearBestTiers()
    {
        _resettingBest = true;
        foreach (var c in BestTierChips) c.IsChecked = false;
        _resettingBest = false;
        OnBestFilterChanged();
    }

    [RelayCommand]
    private void ResetBestFilter()
    {
        _resettingBest = true;
        foreach (var c in BestTierChips) c.IsChecked = false;
        SelectedMinBattles = MinBattlesOptions.First(o => o.Value == DefaultMinBattles);
        CustomMinBattles = DefaultMinBattles.ToString(CultureInfo.InvariantCulture);
        CustomMinError = null;
        _resettingBest = false;
        OnBestFilterChanged();
    }

    private void OnBestFilterChanged()
    {
        if (_resettingBest) return;
        OnPropertyChanged(nameof(AllBestTiers));
        BuildBestShips();
    }

    /// <summary>Ranks the player's ships by PR within the chosen tiers and minimum battles.</summary>
    private void BuildBestShips()
    {
        var tiers = BestTierChips.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet();
        var min = MinBattles;
        var inv = CultureInfo.CurrentCulture;
        var result = ShipRanking.Rank(_allShips.Select(r => (r.Perf, r.Ship)), tiers, min);

        BestShips = result.Top
            .Select(r => new ChartBar($"{ShipRanking.Roman(r.Ship.Tier)} {r.Ship.Name}", r.Pr,
                $"PR {r.Pr.ToString("0", inv)} · {r.Battles:N0} battle{(r.Battles == 1 ? "" : "s")}", RatingBrushConverter.For(r.Pr, "pr")))
            .ToList();

        BestShipsTitle = $"Best performing ships (PR, {min:N0}+ battles)";
        IsBestFilterActive = tiers.Count > 0 || min != DefaultMinBattles;
        var filters = $"{ShipRanking.TierLabel(tiers)} · {min:N0}+ battles";
        var count = result.Matching == 0 ? "no ships match"
            : result.Matching > result.Top.Count ? $"top {result.Top.Count} of {result.Matching:N0} matching ships"
            : $"{result.Matching:N0} ship{(result.Matching == 1 ? "" : "s")} match{(result.Matching == 1 ? "es" : "")}";
        var left = new List<string>();
        if (result.WithoutPr > 0) left.Add($"{result.WithoutPr:N0} without a PR (no expected values)");
        if (result.UnknownShips > 0) left.Add($"{result.UnknownShips:N0} with an unknown tier");
        BestShipsSummary = $"{filters}: {count}" + (left.Count > 0 ? $". Not ranked: {string.Join(", ", left)}." : "");

        BestShipsEmpty = result.Top.Count > 0 ? null
            : _allShips.Count == 0 ? "No ship statistics for this player."
            : tiers.Count > 0 ? $"No ships in {ShipRanking.TierLabel(tiers)} with {min:N0}+ battles and a PR. Lower the minimum battles or choose other tiers."
            : $"No ships with {min:N0}+ battles and a PR. Lower the minimum battles.";

        foreach (var c in BestTierChips)
        {
            var ofTier = _allShips.Where(r => r.Tier == c.Value).ToList();
            var qualifying = ofTier.Count(r => r.Battles >= min && r.Pr is not null);
            c.IsAvailable = ofTier.Count > 0 || c.IsChecked;
            c.Tip = ofTier.Count == 0 ? $"Tier {c.Label}: no ships"
                : $"Tier {c.Label}: {qualifying:N0} of {ofTier.Count:N0} ship{(ofTier.Count == 1 ? "" : "s")} with {min:N0}+ battles and a PR";
        }
    }

    private static string Roman(int t) => t switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => "X",
    };

    [RelayCommand]
    private void ClearShipFilter()
    {
        _resettingFilter = true;
        foreach (var c in TierChips.Concat(ClassChips)) c.IsChecked = false;
        _resettingFilter = false;
        ApplyShipFilter();
    }

    /// <summary>Shows the ships matching the checked tiers and classes, with their combined results.</summary>
    private void ApplyShipFilter()
    {
        if (_resettingFilter) return;
        var tiers = TierChips.Where(c => c.IsChecked).Select(c => c.Value).ToHashSet();
        var classes = ClassChips.Where(c => c.IsChecked).Select(c => (ShipClass)c.Value).ToHashSet();
        var shown = _allShips.Where(r => (tiers.Count == 0 || tiers.Contains(r.Tier)) && (classes.Count == 0 || classes.Contains(r.ShipClass))).ToList();
        Ships.Clear();
        foreach (var r in shown) Ships.Add(r);

        IsShipFilterActive = tiers.Count + classes.Count > 0;
        var battles = shown.Sum(r => r.Battles);
        var wins = shown.Sum(r => r.Perf.Totals.Wins);
        var damage = shown.Sum(r => (double)r.Perf.Totals.Damage);
        var inv = CultureInfo.CurrentCulture;
        ShipFilterSummary = shown.Count == 0
            ? (IsShipFilterActive ? "No ships match this filter." : "")
            : $"{shown.Count:N0} ship{(shown.Count == 1 ? "" : "s")} · {battles:N0} battles"
              + (battles > 0 ? $" · {(100.0 * wins / battles).ToString("0.0", inv)}% wins · {(damage / battles).ToString("N0", inv)} avg damage" : "")
              + (IsShipFilterActive ? $" (of {_allShips.Count:N0} ships)" : "");
    }

    /// <summary>Greys out chips with no ships for this player and puts the counts in their tooltips.</summary>
    private void UpdateChipCounts()
    {
        foreach (var c in TierChips)
        {
            var rows = _allShips.Where(r => r.Tier == c.Value).ToList();
            c.IsAvailable = rows.Count > 0 || c.IsChecked;
            c.Tip = $"Tier {c.Label}: {rows.Count:N0} ships, {rows.Sum(r => r.Battles):N0} battles";
        }
        foreach (var c in ClassChips)
        {
            var rows = _allShips.Where(r => r.ShipClass == (ShipClass)c.Value).ToList();
            c.IsAvailable = rows.Count > 0 || c.IsChecked;
            c.Tip = $"{ClassName((ShipClass)c.Value)}: {rows.Count:N0} ships, {rows.Sum(r => r.Battles):N0} battles";
        }
    }

    /// <summary>Raised when the user asks to open a recorded battle from the encounters list.</summary>
    public event Action<long>? BattleRequested;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showSuggestions;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private PlayerSummary? _player;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string? _clanTag;
    [ObservableProperty] private IReadOnlyList<ChartBar> _mostPlayed = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _bestShips = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _classBreakdown = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _tierBreakdown = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> _winRateHistory = [];
    [ObservableProperty] private string _winRateHistoryNote = "";

    public ObservableCollection<string> Suggestions { get; } = [];
    public ObservableCollection<StatCard> Cards { get; } = [];
    public ObservableCollection<StatCard> RecentCards { get; } = [];
    public ObservableCollection<ShipStatRow> Ships { get; } = [];
    public IReadOnlyList<FilterChip> TierChips { get; }
    public IReadOnlyList<FilterChip> ClassChips { get; }
    [ObservableProperty] private string _shipFilterSummary = "";
    [ObservableProperty] private bool _isShipFilterActive;
    private List<ShipStatRow> _allShips = [];
    private bool _resettingFilter;
    public ObservableCollection<EncounterRow> Encounters { get; } = [];

    public bool HasPlayer => Player is not null;
    public bool HasEncounters => Encounters.Count > 0;

    partial void OnPlayerChanged(PlayerSummary? value) => OnPropertyChanged(nameof(HasPlayer));

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;
        _searchTimer.Stop();
        if (value.Trim().Length >= 3) _searchTimer.Start();
        else ShowSuggestions = false;
    }

    private async Task SearchAsync()
    {
        _searchTimer.Stop();
        var text = SearchText.Trim();
        if (text.Length < 3 || !_host.Api.HasApplicationId) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        try
        {
            var found = await _host.Api.SearchNicknamesAsync(_host.Region, text, 10, _searchCts.Token);
            if (text != SearchText.Trim()) return;
            Suggestions.Clear();
            foreach (var f in found) Suggestions.Add(f.Nickname);
            ShowSuggestions = Suggestions.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (WgApiException ex) { _host.Log.LogInformation("Search failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private Task Search() => OpenAsync(SearchText.Trim());

    [RelayCommand]
    private Task PickSuggestion(string? name) => name is null ? Task.CompletedTask : OpenAsync(name);

    [RelayCommand]
    private Task Refresh() => Player is null ? Task.CompletedTask : OpenAsync(Player.Name, force: true);

    public async Task OpenAsync(string name, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _suppressSearch = true;
        SearchText = name;
        _suppressSearch = false;
        ShowSuggestions = false;
        _searchTimer.Stop();

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (!_host.Api.HasApplicationId)
        {
            Message = "Add a Wargaming application ID in Settings to load profiles.";
            return;
        }

        IsLoading = true;
        Message = null;
        try
        {
            var region = _host.Region;
            var result = await _host.Stats.LoadAsync("profile:" + name, region, [new PlayerRequest(name, 1, 0, false)],
                RequestPriority.Normal, ct, force);
            if (ct.IsCancellationRequested) return;
            var p = result[0];
            Show(p, region);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Profile load failed for {Name}", name);
            Message = "Could not load the profile: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    private void Show(PlayerSummary p, Region region)
    {
        if (p.State is LoadState.NotFound or LoadState.Error)
        {
            Player = null;
            Message = p.StatusText ?? "Player not found.";
            return;
        }

        Player = p;
        _region = region;
        Title = p.Name;
        ClanTag = p.ClanTag is null ? null : $"[{p.ClanTag}]";
        var parts = new List<string> { Regions.Label(region) };
        if (p.CreatedAt is { } c) parts.Add($"account created {c.LocalDateTime:d}");
        if (p.LastBattleAt is { } l) parts.Add($"last battle {l.LocalDateTime:g}");
        if (p.ServiceLevel is int lvl) parts.Add($"service record level {lvl}");
        if (p.StatsFetchedAt is { } f) parts.Add($"stats fetched {f.LocalDateTime:t}");
        Subtitle = string.Join("  ·  ", parts);
        Message = p.Hidden ? "This player has hidden their profile. Wargaming only shares the name and account dates." : null;

        BuildCards(p);
        BuildShips(p);
        BuildHistory(p, region);
        BuildEncounters(p);
    }

    private Region _region;

    private void BuildCards(PlayerSummary p)
    {
        var inv = CultureInfo.CurrentCulture;
        Cards.Clear();
        RecentCards.Clear();
        if (p.Overall is { } o)
        {
            Cards.Add(new StatCard("Battles", o.Battles.ToString("N0", inv), $"{o.Wins:N0} wins · {o.Losses:N0} losses · {o.Draws:N0} draws", "official"));
            Cards.Add(new StatCard("Win rate", Pct(o.WinRate), "Random battles", "derived", RatingBrushConverter.For(o.Battles >= 50 ? o.WinRate : null, "wr")));
            Cards.Add(new StatCard("Personal Rating", p.Pr?.ToString("0", inv) ?? "—", PersonalRating.BandLabel(PersonalRating.Band(p.Pr)), "third-party",
                RatingBrushConverter.For(p.Pr, "pr")));
            Cards.Add(new StatCard("Average damage", o.AvgDamage?.ToString("N0", inv) ?? "—",
                p.DamageRatio is double r ? $"{r:P0} of expected" : "per battle", "derived", RatingBrushConverter.For(p.DamageRatio, "ratio")));
            Cards.Add(new StatCard("Average experience", o.AvgXp?.ToString("N0", inv) ?? "—", "per battle", "derived"));
            Cards.Add(new StatCard("Average frags", o.AvgFrags?.ToString("0.00", inv) ?? "—", "ships destroyed per battle", "derived"));
            Cards.Add(new StatCard("Survival rate", Pct(o.SurvivalRate), $"{o.Survived:N0} battles survived", "derived"));
            Cards.Add(new StatCard("Planes destroyed", o.AvgPlanes?.ToString("0.00", inv) ?? "—", "per battle", "derived"));
            if (p.Ranked is { Battles: > 0 } rk)
                Cards.Add(new StatCard("Ranked battles", rk.Battles.ToString("N0", inv), $"{Pct(rk.WinRate)} wins, {rk.AvgDamage:N0} dmg", "official / derived"));
        }

        void AddRecent(string label, RecentStats? r)
        {
            if (r is null)
            {
                RecentCards.Add(new StatCard(label, "—",
                    "Not seen by Foretop long enough ago. Wargaming has no recent-stats API, so this builds up over time.", "Foretop snapshots"));
                return;
            }
            var t = r.Totals;
            RecentCards.Add(new StatCard(label, $"{t.Battles:N0} battles",
                t.Battles > 0 ? $"{Pct(t.WinRate)} wins · {t.AvgDamage:N0} dmg · since {r.Since.LocalDateTime:d} ({r.Days} days)" : $"since {r.Since.LocalDateTime:d}",
                "Foretop snapshots", RatingBrushConverter.For(t.Battles >= 5 ? t.WinRate : null, "wr")));
        }
        if (p.Overall is not null)
        {
            AddRecent("Last 7 days", p.Recent7);
            AddRecent("Last 28 days", p.Recent28);
        }
    }

    /// <summary>Same fallback as the tier filter elsewhere: the installed game knows ships the API list omits.</summary>
    private ShipInfo? FromGameFiles(long shipId) =>
        _host.ShipData.FindById(shipId) is { Tier: > 0 } d
            ? new ShipInfo(shipId, d.DisplayName, d.Tier, ShipInfo.ParseClass(d.Species), d.Nation, false, false, null, null)
            : null;

    private void BuildShips(PlayerSummary p)
    {
        var rows = (p.Ships ?? []).Select(s => new ShipStatRow(s, _host.Ships.Get(s.ShipId) ?? FromGameFiles(s.ShipId))).ToList();
        _allShips = rows;
        UpdateChipCounts();
        ApplyShipFilter(); // the filter stays when switching players

        MostPlayed = rows.Where(r => r.Ship is not null).OrderByDescending(r => r.Battles).Take(10)
            .Select(r => new ChartBar($"{r.TierText} {r.ShipName}", r.Battles, $"{r.Battles:N0} · {r.WinRateText}", RatingBrushConverter.ForClass(r.ShipClass)))
            .ToList();

        BuildBestShips(); // tier and minimum-battles filter stay when switching players

        var total = Math.Max(1, rows.Sum(r => r.Battles));
        ClassBreakdown = rows.GroupBy(r => r.ShipClass).Where(g => g.Key != ShipClass.Unknown)
            .Select(g => (Cls: g.Key, Battles: g.Sum(r => r.Battles), Wins: g.Sum(r => r.Perf.Totals.Wins)))
            .OrderByDescending(x => x.Battles)
            .Select(x => new ChartBar(ClassName(x.Cls), x.Battles,
                $"{100.0 * x.Battles / total:0}% · {100.0 * x.Wins / Math.Max(1, x.Battles):0.0}% WR", RatingBrushConverter.ForClass(x.Cls)))
            .ToList();

        TierBreakdown = rows.Where(r => r.Tier > 0).GroupBy(r => r.Tier).OrderByDescending(g => g.Key)
            .Select(g => (Tier: g.Key, Battles: g.Sum(r => r.Battles), Wins: g.Sum(r => r.Perf.Totals.Wins), Text: g.First().TierText))
            .Select(x => new ChartBar("Tier " + x.Text, x.Battles,
                $"{x.Battles:N0} · {100.0 * x.Wins / Math.Max(1, x.Battles):0.0}% WR", RatingBrushConverter.Res("Accent")))
            .ToList();
    }

    private void BuildHistory(PlayerSummary p, Region region)
    {
        if (p.AccountId is not long id)
        {
            WinRateHistory = [];
            return;
        }
        var snaps = _host.Snapshots.GetAll(region, id);
        var pts = snaps.Where(s => s.Totals.Battles > 0)
            .Select(s => new ChartPoint(s.TakenAt.ToUnixTimeSeconds() / 86400.0, s.Totals.WinRate!.Value, s.TakenAt.LocalDateTime.ToString("d")))
            .ToList();
        WinRateHistory = pts.Count >= 2 ? [new ChartSeries("Win rate", RatingBrushConverter.Res("Accent"), pts)] : [];
        WinRateHistoryNote = pts.Count >= 2
            ? $"Overall win rate at each of {pts.Count} snapshots Foretop took of this player."
            : "Wargaming does not publish history, so this chart fills in as Foretop sees the player again over time.";
    }

    private int _encounterLoad;

    /// <summary>
    /// Recorded battles with this player. The search reads every stored roster, so it runs off the UI thread; only the
    /// newest request fills the list (profiles can be opened quickly one after another).
    /// </summary>
    private async void BuildEncounters(PlayerSummary p)
    {
        var mine = ++_encounterLoad;
        Encounters.Clear();
        OnPropertyChanged(nameof(HasEncounters));
        try
        {
            var name = p.Name;
            var rows = await Task.Run(() =>
            {
                var list = new List<(long Id, DateTime When, string Battle, string Side, long ShipId, string Stats)>();
                foreach (var b in _host.History.FindWithPlayer(name, 50))
                {
                    if (!ArenaInfoParser.TryParse(b.ArenaJson, out var info, out _)) continue;
                    var v = info!.Vehicles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (v is null) continue;
                    var side = v.IsSelf ? "You" : v.IsFriendly ? "With you" : "Against you";
                    var then = BattleSummaryCodec.Deserialize(b.SummaryJson).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    var stats = then?.Overall is { } o ? $"{Pct(o.WinRate)} WR · PR {then.Pr?.ToString("0") ?? "—"}" : "—";
                    list.Add((b.Id, b.When.LocalDateTime, $"{BattleTypes.Label(info.BattleType, info.MatchGroup)} · {info.MapName}", side, v.ShipId, stats));
                }
                return list;
            });
            if (mine != _encounterLoad) return;
            foreach (var r in rows) Encounters.Add(new EncounterRow(r.Id, r.When, r.Battle, r.Side, _host.Ships.Get(r.ShipId)?.Name ?? "?", r.Stats));
        }
        catch (Exception ex) when (ex is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _host.Log.LogWarning("Encounters for {Name} not read: {Error}", p.Name, ex.Message);
        }
        OnPropertyChanged(nameof(HasEncounters));
    }

    [RelayCommand]
    private void OpenEncounter(EncounterRow? row)
    {
        if (row is not null) BattleRequested?.Invoke(row.BattleId);
    }

    [RelayCommand]
    private void OpenOfficial()
    {
        if (Player?.AccountId is long id) OpenUrl(Regions.ProfileUrl(_region, id, Player.Name));
    }

    [RelayCommand]
    private void OpenNumbers()
    {
        if (Player?.AccountId is long id) OpenUrl(Regions.WowsNumbersUrl(_region, id, Player.Name));
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string Pct(double? v) => v is double d ? d.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "—";

    internal static string ClassName(ShipClass c) => c switch
    {
        ShipClass.Destroyer => "Destroyers",
        ShipClass.Cruiser => "Cruisers",
        ShipClass.Battleship => "Battleships",
        ShipClass.AirCarrier => "Aircraft carriers",
        ShipClass.Submarine => "Submarines",
        _ => "Other",
    };
}
