using System.Globalization;
using Foretop.Core.Game;
using Foretop.Core.Stats;

namespace Foretop.App.ViewModels;

/// <summary>One block of the Dashboard's "Your results": this session or today.</summary>
public sealed class ResultSummaryView
{
    public ResultSummaryView(string title, string subtitle, ResultTotals t)
    {
        var c = CultureInfo.CurrentCulture;
        Title = title;
        Subtitle = subtitle;
        HasBattles = t.Battles > 0;
        Battles = t.Battles.ToString("N0", c);
        Record = $"{t.Wins} W · {t.Losses} L" + (t.Draws > 0 ? $" · {t.Draws} D" : "");
        WinRate = t.WinRate;
        WinRateText = t.WinRate is { } wr ? wr.ToString("0.0", c) + " %" : "—";
        AvgDamage = t.AvgDamage is { } d ? d.ToString("N0", c) : "—";
        TotalDamage = t.Damage.ToString("N0", c);
        AvgXp = t.AvgXp is { } x ? x.ToString("N0", c) : "—";
        AvgFrags = t.AvgFrags is { } f ? f.ToString("0.00", c) : "—";
        Survival = t.SurvivalRate is { } s ? s.ToString("0", c) + " %" : "—";
        Pending = t.Pending switch
        {
            0 => "",
            1 => "1 battle waiting for its result",
            var n => $"{n} battles waiting for their results",
        };
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool HasBattles { get; }
    public string Battles { get; }
    public string Record { get; }
    public double? WinRate { get; }
    public string WinRateText { get; }
    public string AvgDamage { get; }
    public string TotalDamage { get; }
    public string AvgXp { get; }
    public string AvgFrags { get; }
    public string Survival { get; }
    public string Pending { get; }
}

/// <summary>One of your battles with its result, for the Dashboard list.</summary>
public sealed class OwnBattleRow
{
    public OwnBattleRow(BattleResult r, ShipInfo? ship, HistoryRow? history, Core.GameData.ShipData? gameShip = null)
    {
        var test = gameShip?.IsTestShip == true;
        var c = CultureInfo.CurrentCulture;
        Result = r;
        History = history;
        When = r.StartedAt.LocalDateTime.ToString("t", c);
        Day = r.StartedAt.LocalDateTime.ToString("d", c);
        Type = history?.Type ?? r.Mode switch
        {
            ResultModes.Random => "Random Battle",
            ResultModes.Ranked => "Ranked Battle",
            ResultModes.Coop => "Co-op Battle",
            var m => BattleTypes.Label(BattleTypes.FromMatchGroup(m), m),
        };
        Map = history?.Map ?? "";
        Ship = (ship?.Name ?? gameShip?.DisplayName ?? $"Ship {r.ShipId}") + (test ? " (test ship)" : "");
        var t = test ? null : r.Result; // no damage or other values for ships in testing
        (Outcome, OutcomeLevel) = r.Status switch
        {
            ResultStatus.Pending => (r.EndedAt is null ? "In battle" : "Waiting for result…", "Unknown"),
            ResultStatus.Unavailable => ("No result", "Unknown"),
            ResultStatus.Expired => ("Not counted", "Unknown"),
            ResultStatus.Merged => ("In the row above", "Unknown"),
            ResultStatus.Combined when t is not null => ($"{t.Battles} battles: {t.Wins} W", "Unknown"),
            _ when r.Result is { Battles: 1, Wins: 1 } => ("Victory", "Ok"),
            _ when r.Result is { Battles: 1, Losses: 1 } => ("Defeat", "Problem"),
            _ when r.Result is { Battles: 1, Draws: 1 } => ("Draw", "Warning"),
            _ => ("—", "Unknown"),
        };
        Damage = t is null ? "" : t.Damage.ToString("N0", c);
        Xp = t is null ? "" : t.Xp.ToString("N0", c);
        Frags = t is null ? "" : t.Frags.ToString(c);
        Survived = t is null ? "" : r.Status == ResultStatus.Combined ? $"{t.Survived}/{t.Battles}" : t.Survived > 0 ? "Yes" : "No";
        Tooltip = test ? "A ship in testing: no damage or other values are shown, and the battle is left out of the totals." : r.Status switch
        {
            ResultStatus.Unavailable => r.Baseline is null && r.Mode is ResultModes.Random or ResultModes.Ranked or ResultModes.Coop
                ? "The account totals could not be read at the start of this battle (hidden profile or API not reachable)."
                : "The Wargaming API keeps no totals for this battle type, so there is no result for it.",
            ResultStatus.Pending => "The Wargaming API counts a battle shortly after it ends; a battle you leave early is counted when it ends for everyone.",
            ResultStatus.Combined => "The API counted several battles at once (you left one early), so they are shown together.",
            ResultStatus.Merged => "Counted together with the battle above.",
            ResultStatus.Expired => "The API did not count this battle within a day.",
            _ => "From your account totals before and after the battle (Wargaming API). XP is as the API counts it: with Premium Account, flags and other bonuses (base XP is on Analytics → My performance, from the replay).",
        };
    }

    public BattleResult Result { get; }
    public HistoryRow? History { get; }
    public string When { get; }
    public string Day { get; }
    public string Type { get; }
    public string Map { get; }
    public string Ship { get; }
    public string Outcome { get; }
    public string OutcomeLevel { get; }
    public string Damage { get; }
    public string Xp { get; }
    public string Frags { get; }
    public string Survived { get; }
    public string Tooltip { get; }
}
