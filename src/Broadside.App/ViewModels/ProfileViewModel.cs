using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Broadside.App.Charts;
using Broadside.Core.Api;
using Broadside.Core.Game;
using Broadside.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Broadside.App.ViewModels;

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
                    "Not seen by Broadside long enough ago. Wargaming has no recent-stats API, so this builds up over time.", "Broadside snapshots"));
                return;
            }
            var t = r.Totals;
            RecentCards.Add(new StatCard(label, $"{t.Battles:N0} battles",
                t.Battles > 0 ? $"{Pct(t.WinRate)} wins · {t.AvgDamage:N0} dmg · since {r.Since.LocalDateTime:d} ({r.Days} days)" : $"since {r.Since.LocalDateTime:d}",
                "Broadside snapshots", RatingBrushConverter.For(t.Battles >= 5 ? t.WinRate : null, "wr")));
        }
        if (p.Overall is not null)
        {
            AddRecent("Last 7 days", p.Recent7);
            AddRecent("Last 28 days", p.Recent28);
        }
    }

    private void BuildShips(PlayerSummary p)
    {
        Ships.Clear();
        var rows = (p.Ships ?? []).Select(s => new ShipStatRow(s, _host.Ships.Get(s.ShipId))).ToList();
        foreach (var r in rows) Ships.Add(r);

        MostPlayed = rows.Where(r => r.Ship is not null).OrderByDescending(r => r.Battles).Take(10)
            .Select(r => new ChartBar($"{r.TierText} {r.ShipName}", r.Battles, $"{r.Battles:N0} · {r.WinRateText}", RatingBrushConverter.ForClass(r.ShipClass)))
            .ToList();

        BestShips = rows.Where(r => r.Ship is not null && r.Battles >= 20 && r.Pr is not null).OrderByDescending(r => r.Pr).Take(10)
            .Select(r => new ChartBar($"{r.TierText} {r.ShipName}", r.Pr!.Value, $"PR {r.PrText} · {r.Battles:N0} b", RatingBrushConverter.For(r.Pr, "pr")))
            .ToList();

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
            ? $"Overall win rate at each of {pts.Count} snapshots Broadside took of this player."
            : "Wargaming does not publish history, so this chart fills in as Broadside sees the player again over time.";
    }

    private void BuildEncounters(PlayerSummary p)
    {
        Encounters.Clear();
        foreach (var b in _host.History.FindWithPlayer(p.Name, 50))
        {
            if (!ArenaInfoParser.TryParse(b.ArenaJson, out var info, out _)) continue;
            var v = info!.Vehicles.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (v is null) continue;
            var side = v.IsSelf ? "You" : v.IsFriendly ? "With you" : "Against you";
            var then = BattleSummaryCodec.Deserialize(b.SummaryJson).FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            var stats = then?.Overall is { } o ? $"{Pct(o.WinRate)} WR · PR {then.Pr?.ToString("0") ?? "—"}" : "—";
            Encounters.Add(new EncounterRow(b.Id, b.When.LocalDateTime, $"{BattleTypes.Label(info.BattleType, info.MatchGroup)} · {info.MapName}", side,
                _host.Ships.Get(v.ShipId)?.Name ?? "?", stats));
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
