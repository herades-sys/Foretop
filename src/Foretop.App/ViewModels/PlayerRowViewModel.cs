using System.Globalization;
using System.Text;
using Foretop.Core.Api;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Foretop.App.ViewModels;

/// <summary>One table row. Wraps the latest <see cref="PlayerSummary"/> plus ship encyclopedia data.</summary>
public sealed class PlayerRowViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.CurrentCulture;

    public PlayerRowViewModel(PlayerSummary summary, ShipInfo? ship, Region region)
    {
        Summary = summary;
        Ship = ship;
        Region = region;
    }

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

    // ---- identity
    public string Name => Summary.Name;
    public string? ClanTag => Summary.ClanTag is null ? null : $"[{Summary.ClanTag}]";
    public bool IsSelf => Summary.Relation == 0;
    public bool IsBot => Summary.IsBot;
    public bool IsFriendly => Summary.IsFriendly;
    public long? AccountId => Summary.AccountId;

    // ---- ship
    public string ShipName => Ship?.Name ?? $"Unknown ship ({Summary.ShipId})";
    public int Tier => Ship?.Tier ?? 0;
    public string TierText => Ship?.TierRoman ?? "?";
    public ShipClass ShipClass => Ship?.Class ?? ShipClass.Unknown;
    public string ClassShort => Ship?.ClassShort ?? "?";
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
    public int? Battles => Summary.Overall?.Battles;
    public double? WinRate => Summary.Overall?.WinRate;
    public double? AvgDamage => Summary.Overall?.AvgDamage;
    public double? AvgXp => Summary.Overall?.AvgXp;
    public double? AvgFrags => Summary.Overall?.AvgFrags;
    public double? SurvivalRate => Summary.Overall?.SurvivalRate;

    public string BattlesText => Battles is int b ? FormatCount(b) : "—";
    public string WinRateText => Pct(WinRate);
    public string AvgDamageText => Num(AvgDamage);
    public string AvgXpText => Num(AvgXp);

    // ---- third-party
    public double? Pr => Summary.Pr;
    public string PrText => Pr is double p ? p.ToString("0", Inv) : "—";
    public double? DamageRatio => Summary.DamageRatio;

    // ---- current ship
    public int? ShipBattles => Summary.CurrentShip?.Battles;
    public double? ShipWinRate => Summary.CurrentShip?.WinRate;
    public double? ShipAvgDamage => Summary.CurrentShip?.AvgDamage;
    public double? ShipDamageRatio => Summary.ShipDamageRatio;
    public double? ShipPr => Summary.ShipPr;
    public string ShipWinRateText => Pct(ShipWinRate);

    /// <summary>Win rate used for colouring: none below 10 battles in the ship, where it means little.</summary>
    public double? ShipWinRateRated => ShipBattles >= 10 ? ShipWinRate : null;

    /// <summary>Overall win rate colour needs at least 50 battles.</summary>
    public double? WinRateRated => Battles >= 50 ? WinRate : null;
    public string ShipBattlesText => ShipBattles is int b ? FormatCount(b) : (Summary.State == LoadState.Ready ? "0" : "—");
    public string ShipAvgDamageText => Num(ShipAvgDamage);

    // ---- recent
    public double? RecentWinRate => Summary.Recent7 is { Totals.Battles: > 0 } r ? r.Totals.WinRate : null;
    public double? RecentWinRateRated => Summary.Recent7 is { Totals.Battles: >= 5 } r ? r.Totals.WinRate : null;
    public int? RecentBattles => Summary.Recent7?.Totals.Battles;
    public string RecentText => Summary.Recent7 is { } r
        ? r.Totals.Battles > 0 ? $"{Pct(r.Totals.WinRate)} ({r.Totals.Battles})" : "0 battles"
        : "—";

    public string Tooltip
    {
        get
        {
            var s = Summary;
            var sb = new StringBuilder();
            sb.Append(ClanTag is null ? "" : ClanTag + " ").AppendLine(s.Name);
            if (s.StatusText is not null) sb.AppendLine(s.StatusText);
            if (s.CreatedAt is { } c) sb.AppendLine($"Account created {c.LocalDateTime:d}");
            if (s.LastBattleAt is { } l) sb.AppendLine($"Last battle {l.LocalDateTime:g}");
            if (s.Overall is { } o)
            {
                sb.AppendLine();
                sb.AppendLine("Random battles (official API; averages derived):");
                sb.AppendLine($"  {o.Battles:N0} battles, {Pct(o.WinRate)} wins, survival {Pct(o.SurvivalRate)}");
                sb.AppendLine($"  {Num(o.AvgDamage)} dmg, {Num(o.AvgXp)} XP, {o.AvgFrags:0.00} frags, {o.AvgPlanes:0.0} planes per battle");
            }
            if (s.Pr is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"Personal Rating {PrText} ({PersonalRating.BandLabel(PersonalRating.Band(s.Pr))}) — third-party, wows-numbers expected values");
                if (s.DamageRatio is { } dr) sb.AppendLine($"  Damage vs. expected: {dr:P0}");
            }
            if (s.CurrentShip is { } cs)
            {
                sb.AppendLine();
                sb.AppendLine($"{ShipName}: {cs.Battles:N0} battles, {Pct(cs.WinRate)}, {Num(cs.AvgDamage)} dmg"
                              + (s.ShipPr is double sp ? $", PR {sp:0}" : ""));
            }
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
