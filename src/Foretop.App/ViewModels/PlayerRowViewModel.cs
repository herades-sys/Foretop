using System.Globalization;
using System.Text;
using Foretop.Core.Api;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Foretop.App.ViewModels;

/// <summary>An entry of the Live Battle statistics filter.</summary>
public sealed record StatsFilterOption(int MinTier, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry of the Live Battle game-mode list; Mode null = the mode of the battle on screen.</summary>
public sealed record GameModeOption(GameMode? Mode, string Label)
{
    /// <summary>Saved in the settings: "Battle" or the mode's name.</summary>
    public string Key => Mode?.ToString() ?? "Battle";
    public override string ToString() => Label;
}

/// <summary>Which statistics the Live Battle columns show while a tier filter is set.</summary>
public enum StatsView { Overall, Filtered, Both }

/// <summary>One table row. Wraps the latest <see cref="PlayerSummary"/> plus ship encyclopedia data.</summary>
public sealed class PlayerRowViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.CurrentCulture;

    public PlayerRowViewModel(PlayerSummary summary, ShipInfo? ship, Region region, Core.GameData.ShipData? gameShip = null)
    {
        Summary = summary;
        Ship = ship;
        Region = region;
        GameShip = gameShip;
    }

    /// <summary>The ship as read from the game files (names ships the API does not list, marks test ships).</summary>
    public Core.GameData.ShipData? GameShip { get; private set; }

    /// <summary>The game-file ship once the ship data has loaded (it loads in the background, often after the battle opens).</summary>
    public void SetGameShip(Core.GameData.ShipData? gameShip)
    {
        if (gameShip is null || ReferenceEquals(gameShip, GameShip)) return;
        GameShip = gameShip;
        OnPropertyChanged(string.Empty); // test-ship marking, name, class and tier may all change
    }

    /// <summary>A ship in testing: no ship statistics are shown for it (the stats service drops them too).</summary>
    public bool IsTestShip => GameShip?.IsTestShip == true;

    public PlayerSummary Summary { get; private set; }
    public ShipInfo? Ship { get; private set; }
    public Region Region { get; }

    public void Update(PlayerSummary s)
    {
        Summary = s;
        OnPropertyChanged(string.Empty); // every derived property changes together
    }

    public void SetShip(ShipInfo? ship)
    {
        Ship = ship;
        OnPropertyChanged(string.Empty);
    }

    // ---- statistics filter (Live Battle toolbar)
    private int _minTier;
    private StatsView _view = StatsView.Filtered;
    private GameMode _mode = GameMode.Random;

    public void SetStatsFilter(int minTier, StatsView view, GameMode mode)
    {
        if (_minTier == minTier && _view == view && _mode == mode) return;
        _minTier = minTier;
        _view = view;
        _mode = mode;
        OnPropertyChanged(string.Empty);
    }

    private bool FilterActive => _minTier > 0;

    /// <summary>The player in the game mode picked in the toolbar (Random: the summary itself).</summary>
    private PlayerSummary Moded => GameModes.Project(Summary, _mode);

    /// <summary>Statistics of the picked mode are loaded (always for Random; not for battles saved before modes were loaded).</summary>
    private bool ModeLoaded => _mode == GameMode.Random || Summary.Modes is not null;

    /// <summary>The summary the main columns show: filtered when a filter is set and the view is "Filtered".</summary>
    private PlayerSummary Primary => FilterActive && _view == StatsView.Filtered ? TierFilter.Project(Moded, _minTier) : Moded;

    /// <summary>"Both": the filtered values as a second line under the overall ones.</summary>
    public bool ShowSecond => FilterActive && _view == StatsView.Both;
    private PlayerSummary Second => TierFilter.Project(Moded, _minTier);
    public string BattlesText2 => Second.Overall?.Battles is int b ? FormatCount(b) : "—";
    public string WinRateText2 => Pct(Second.Overall?.WinRate);
    public double? WinRateRated2 => WinRateColoured && Second.Overall is { Battles: >= 50 } o ? o.WinRate : null;
    public string AvgDamageText2 => Num(Second.Overall?.AvgDamage);
    public double? DamageRatio2 => Second.DamageRatio;
    public string AvgXpText2 => Num(Second.Overall?.AvgXp);
    public string PrText2 => Second.Pr is double p ? p.ToString("0", Inv) : "—";
    public double? Pr2 => Second.Pr;

    // ---- identity
    public string Name => Services.Anonymizer.Name(Summary.Name);
    public string? ClanTag => Services.Anonymizer.Clan(Summary.ClanTag) is { } tag ? $"[{tag}]" : null;
    public bool IsSelf => Summary.Relation == 0;
    public bool IsBot => Summary.IsBot;
    public bool IsFriendly => Summary.IsFriendly;
    public long? AccountId => Summary.AccountId;

    // ---- ship
    public string ShipName => (Ship?.Name ?? GameShip?.DisplayName ?? $"Unknown ship ({Summary.ShipId})") + (IsTestShip ? " (test ship)" : "");

    /// <summary>The name in the table, where a TEST tag marks ships in testing instead of the suffix.</summary>
    public string ShipNameShown => Ship?.Name ?? GameShip?.DisplayName ?? $"Unknown ship ({Summary.ShipId})";
    public int Tier => Ship?.Tier ?? GameShip?.Tier ?? 0;
    public string TierText => Ship?.TierRoman ?? (GameShip is { Tier: > 0 } g ? ShipFacts.TierRoman(g.Tier) : "?");
    public ShipClass ShipClass => Ship?.Class ?? GameShip?.Species switch
    {
        "Destroyer" => ShipClass.Destroyer, "Cruiser" => ShipClass.Cruiser, "Battleship" => ShipClass.Battleship,
        "AirCarrier" => ShipClass.AirCarrier, "Submarine" => ShipClass.Submarine, _ => ShipClass.Unknown,
    };
    public string ClassShort => Ship?.ClassShort ?? ShipClass switch
    {
        ShipClass.Destroyer => "DD", ShipClass.Cruiser => "CA", ShipClass.Battleship => "BB",
        ShipClass.AirCarrier => "CV", ShipClass.Submarine => "SS", _ => "?",
    };
    public int ClassOrder => ShipClass switch
    {
        ShipClass.AirCarrier => 0, ShipClass.Battleship => 1, ShipClass.Cruiser => 2,
        ShipClass.Destroyer => 3, ShipClass.Submarine => 4, _ => 5,
    };
    public string? ShipImage => Ship?.ImageSmall;
    public System.Windows.Media.ImageSource? ShipIcon =>
        Services.ShipIconCache.Instance?.Get(Ship?.ImageSmall, () => OnPropertyChanged(nameof(ShipIcon)));

    // ---- state
    public LoadState State => Summary.State;
    public bool IsLoading => Summary.State is LoadState.Resolving or LoadState.LoadingStats or LoadState.LoadingShips;
    public bool HasNotice => Summary.State is LoadState.Hidden or LoadState.NotFound or LoadState.Error or LoadState.Bot;
    public string StateText => Summary.State switch
    {
        LoadState.Pending => "",
        LoadState.Resolving => "Looking up…",
        LoadState.LoadingStats => "Loading…",
        LoadState.LoadingShips => "Ships…",
        LoadState.Hidden => "hidden", // short: it shares the cell with the name; the row tooltip says more
        LoadState.NotFound => "Not found",
        LoadState.Bot => "Bot",
        LoadState.Error => "Error",
        LoadState.Ready when Summary.StaleData => "offline data",
        _ => "",
    };

    // ---- overall (official totals, derived averages)
    public int? Battles => Primary.Overall?.Battles;
    public double? WinRate => Primary.Overall?.WinRate;
    public double? AvgDamage => Primary.Overall?.AvgDamage;
    public double? AvgXp => Primary.Overall?.AvgXp;
    public double? AvgFrags => Primary.Overall?.AvgFrags;
    public double? SurvivalRate => Primary.Overall?.SurvivalRate;

    public string BattlesText => Battles is int b ? FormatCount(b) : "—";
    public string WinRateText => Pct(WinRate);
    public string AvgDamageText => Num(AvgDamage);
    public string AvgXpText => Num(AvgXp);

    // ---- third-party
    public double? Pr => Primary.Pr;
    public string PrText => Pr is double p ? p.ToString("0", Inv) : "—";
    public double? DamageRatio => Primary.DamageRatio;

    // ---- current ship
    // Also blank for battles recorded before a ship was known to be in testing.
    private StatTotals? CurrentShip => IsTestShip ? null : Moded.CurrentShip;
    public int? ShipBattles => CurrentShip?.Battles;
    public double? ShipWinRate => CurrentShip?.WinRate;
    public double? ShipAvgDamage => CurrentShip?.AvgDamage;
    public double? ShipDamageRatio => IsTestShip ? null : Moded.ShipDamageRatio;
    public double? ShipPr => IsTestShip ? null : Moded.ShipPr;
    public string ShipPrText => ShipPr is double p ? p.ToString("0", Inv) : "—";
    public string ShipWinRateText => Pct(ShipWinRate);

    /// <summary>Win rate used for colouring: none below 10 battles in the ship, where it means little.</summary>
    public double? ShipWinRateRated => WinRateColoured && ShipBattles >= 10 ? ShipWinRate : null;

    /// <summary>Ship PR colour, likewise only from 10 battles in the ship.</summary>
    public double? ShipPrRated => ShipBattles >= 10 ? ShipPr : null;

    /// <summary>Overall win rate colour needs at least 50 battles.</summary>
    public double? WinRateRated => WinRateColoured && Battles >= 50 ? WinRate : null;

    /// <summary>
    /// The win-rate colours (47 / 53 / 60 %) describe player battles; in Co-op and Operations nearly everyone wins, so
    /// win rates there stay uncoloured.
    /// </summary>
    private bool WinRateColoured => _mode is GameMode.Random or GameMode.Ranked;
    public string ShipBattlesText => ShipBattles is int b ? FormatCount(b) : (Summary.State == LoadState.Ready && !IsTestShip && ModeLoaded ? "0" : "—");
    public string ShipAvgDamageText => Num(ShipAvgDamage);

    // ---- karma (third party: WoWS Karma)
    public int? Karma => Summary.Karma?.GameKarma;
    public string KarmaText => Karma is int k ? k.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "—";

    /// <summary>Negative karma is shown in red; otherwise the plain text colour.</summary>
    public bool KarmaNegative => Karma < 0;

    // ---- recent
    public double? RecentWinRate => Primary.Recent7 is { Totals.Battles: > 0 } r ? r.Totals.WinRate : null;
    public double? RecentWinRateRated => Primary.Recent7 is { Totals.Battles: >= 5 } r ? r.Totals.WinRate : null;
    public int? RecentBattles => Primary.Recent7?.Totals.Battles;
    public string RecentText => Primary.Recent7 is { } r
        ? r.Totals.Battles > 0 ? $"{Pct(r.Totals.WinRate)} ({r.Totals.Battles})" : "0 battles"
        : "—";

    public string Tooltip
    {
        get
        {
            var s = Moded; // the picked game mode; identity, dates and karma are the same in every mode
            var sb = new StringBuilder();
            sb.Append(ClanTag is null ? "" : ClanTag + " ").AppendLine(Name);
            sb.AppendLine($"{ShipName} · {ClassShort} {TierText}");   // in full, also when the table has to shorten it
            if (s.StatusText is not null) sb.AppendLine(s.StatusText);
            if (s.CreatedAt is { } c) sb.AppendLine($"Account created {c.LocalDateTime:d}");
            if (s.LastBattleAt is { } l) sb.AppendLine($"Last battle {l.LocalDateTime:g}");
            if (_mode != GameMode.Random && s.Overall is null && s.State is LoadState.Ready)
            {
                sb.AppendLine();
                sb.AppendLine(ModeLoaded ? $"No {GameModes.Label(_mode)} battles" : $"{GameModes.Label(_mode)} statistics were not loaded for this battle (Refresh loads them)");
            }
            if (s.Overall is { } o)
            {
                sb.AppendLine();
                sb.AppendLine($"{GameModes.Label(_mode)} battles (official API; averages derived):");
                sb.AppendLine($"  {o.Battles:N0} battles, {Pct(o.WinRate)} wins, survival {Pct(o.SurvivalRate)}");
                sb.AppendLine(o.CombatKnown
                    ? $"  {Num(o.AvgDamage)} dmg, {Num(o.AvgXp)} XP, {o.AvgFrags:0.00} frags, {o.AvgPlanes:0.0} planes per battle"
                    : $"  {Num(o.AvgXp)} XP per battle (the API reports no damage or kills for {GameModes.Label(_mode)})");
            }
            if (s.Pr is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"Personal Rating {s.Pr:0} ({PersonalRating.BandLabel(PersonalRating.Band(s.Pr))}) — third-party, wows-numbers expected values");
                if (s.DamageRatio is { } dr) sb.AppendLine($"  Damage vs. expected: {dr:P0}");
            }
            if (FilterActive)
            {
                sb.AppendLine();
                var label = TierFilter.Label(_minTier);
                if (TierFilter.For(s, _minTier) is not { } t)
                {
                    sb.AppendLine($"{label}: not available" + (s.State == LoadState.Ready || s.Overall is not null
                        ? " (ship statistics not loaded for this battle; Refresh loads them)" : ""));
                }
                else
                {
                    var ft = t.Totals;
                    sb.AppendLine($"{label} (sum of the official per-ship totals of {t.Ships} ships; calculated by Foretop):");
                    if (ft.Battles == 0)
                    {
                        sb.AppendLine("  no battles at these tiers");
                    }
                    else
                    {
                        sb.AppendLine($"  {ft.Battles:N0} battles, {ft.Wins:N0} wins, {ft.Losses:N0} defeats, {ft.Draws:N0} draws: {Pct(ft.WinRate)}");
                        sb.AppendLine($"  {Num(ft.AvgDamage)} dmg, {Num(ft.AvgXp)} XP, {ft.AvgFrags:0.00} kills per battle");
                        sb.AppendLine($"  survival {Pct(ft.SurvivalRate)}, kills/deaths {(ft.KillDeath is double kd ? kd.ToString("0.00", Inv) : "— (no deaths)")}");
                        sb.AppendLine(t.Pr is double fp
                            ? $"  PR {fp:0} ({PersonalRating.BandLabel(PersonalRating.Band(fp))}), same formula over these ships only"
                            : "  PR: not available (no expected values for these ships)");
                    }
                    if (t.UnknownTierShips > 0)
                        sb.AppendLine($"  {t.UnknownTierShips} ship(s) of unknown tier not included");
                    if (s.Overall is { Battles: > 0 } all && t.Totals.Battles > 0)
                        sb.AppendLine($"  = {100.0 * t.Totals.Battles / all.Battles:0}% of their {all.Battles:N0} Random battles");
                }
            }
            if (IsTestShip)
            {
                sb.AppendLine();
                sb.AppendLine($"{ShipName}: ship in testing, no ship statistics shown");
            }
            else if (s.CurrentShip is { } cs)
            {
                sb.AppendLine();
                sb.AppendLine($"{ShipName}: {cs.Battles:N0} battles, {Pct(cs.WinRate)}, {Num(cs.AvgDamage)} dmg"
                              + (s.ShipPr is double sp ? $", PR {sp:0}" : ""));
            }
            if (s.Karma is { } k)
            {
                sb.AppendLine();
                sb.AppendLine($"Karma {k.GameKarma:N0} in game — third-party, WoWS Karma (wows-karma.com)");
                sb.AppendLine($"  Site karma {k.SiteKarma:N0}; site ratings: performance {k.Performance}, teamplay {k.Teamplay}, courtesy {k.Courtesy}");
            }
            if (FilterActive && s.Recent7 is not null)
                sb.AppendLine("Recent activity below is for all tiers (Foretop's snapshots have no per-tier split).");
            if (s.Recent7 is { } r7)
                sb.AppendLine($"Last {r7.Days} days: {r7.Totals.Battles} battles, {Pct(r7.Totals.WinRate)}  (Foretop snapshots)");
            if (s.Recent28 is { } r28)
                sb.AppendLine($"Last {r28.Days} days: {r28.Totals.Battles} battles, {Pct(r28.Totals.WinRate)}  (Foretop snapshots)");
            else if (s.Recent7 is null && s.Overall is not null)
                sb.AppendLine("Recent activity: not seen 7+ days ago yet (Wargaming offers no recent-stats API)");
            if (s.StatsFetchedAt is { } f) sb.AppendLine().Append($"Stats fetched {f.LocalDateTime:t}");
            return sb.ToString().TrimEnd();
        }
    }

    private static string Pct(double? v) => v is double d ? d.ToString("0.0", Inv) + "%" : "—";

    private static string Num(double? v) => v is double d ? d.ToString("N0", Inv) : "—";

    private static string FormatCount(int n) => n >= 10_000 ? (n / 1000.0).ToString("0.#", Inv) + "k" : n.ToString("N0", Inv);
}
