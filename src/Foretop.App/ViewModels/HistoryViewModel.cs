using System.Collections.ObjectModel;
using System.Globalization;
using Foretop.Core.Data;
using Foretop.Core.Game;
using Foretop.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

/// <summary>A recorded battle with its two team summaries, prepared off the UI thread.</summary>
public sealed class HistoryRow
{
    public required BattleRecord Record { get; init; }
    public required ArenaInfo Arena { get; init; }
    public required TeamSummary Friendly { get; init; }
    public required TeamSummary Enemy { get; init; }
    public required double? Share { get; init; }
    public required string OwnShip { get; init; }
    public required double? AvgTier { get; init; }
    public required IReadOnlyList<PlayerSummary> Players { get; init; }

    public DateTime When => Record.When.LocalDateTime;
    public string WhenText => When.ToString("g");
    public string Type => BattleTypes.Label(Arena.BattleType, Arena.MatchGroup);
    public string Map => Arena.MapName;
    public string Source => Record.Source == "live" ? "Live" : "Replay";
    public double? FriendlyWr => Friendly.AvgWinRate;
    public double? EnemyWr => Enemy.AvgWinRate;
    public double? FriendlyPr => Friendly.AvgPr;
    public double? EnemyPr => Enemy.AvgPr;
    public string FriendlyWrText => Fmt(Friendly.AvgWinRate, "0.0", "%");
    public string EnemyWrText => Fmt(Enemy.AvgWinRate, "0.0", "%");
    public string FriendlyPrText => Fmt(Friendly.AvgPr, "0", "");
    public string EnemyPrText => Fmt(Enemy.AvgPr, "0", "");
    public string ShareText => Share is double s ? s.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Fmt(double? v, string f, string suffix) => v is double d ? d.ToString(f, CultureInfo.CurrentCulture) + suffix : "—";
}

public sealed record HistoryCompareLine(string Label, string A, string B);

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _dirty = true;

    public HistoryViewModel(AppHost host) => _host = host;

    /// <summary>Raised with a battle id to show it on the Live Battle page.</summary>
    public event Action<BattleRecord>? OpenRequested;

    public ObservableCollection<HistoryRow> Battles { get; } = [];
    public ObservableCollection<HistoryCompareLine> CompareLines { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _compareHint = "Select two battles (Ctrl+click) to compare them.";
    [ObservableProperty] private HistoryRow? _selected;

    public void MarkDirty() => _dirty = true;

    /// <summary>Reloads when something was recorded since the last visit.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!_dirty || IsLoading) return;
        _dirty = false;
        IsLoading = true;
        try
        {
            var rows = await Task.Run(() => LoadRows(_host));
            Battles.Clear();
            foreach (var r in rows) Battles.Add(r);
            Summary = rows.Count == 0
                ? "No battles recorded yet. Battles are saved automatically when detected, and when you open a replay."
                : $"{rows.Count} recorded battles · {rows.Count(r => r.Record.Source == "live")} live, {rows.Count(r => r.Record.Source != "live")} from replays";
        }
        catch (Exception ex)
        {
            _host.Log.LogError(ex, "Loading battle history failed");
            Summary = "Could not load the history: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static readonly object CacheGate = new();

    /// <summary>Forget parsed rows (e.g. after the ship catalogue changed, so ship names are filled in again).</summary>
    internal static void ClearRowCache()
    {
        lock (CacheGate) RowCache.Clear();
    }

    private static readonly Dictionary<long, (long Version, HistoryRow? Row)> RowCache = [];

    /// <summary>
    /// All recorded battles as rows, newest first. Only battles that are new or changed since the last call are
    /// read and parsed again, so revisiting History or Statistics stays fast as the history grows.
    /// </summary>
    internal static List<HistoryRow> LoadRows(AppHost host)
    {
        lock (CacheGate)
        {
            var index = host.History.Index();
            foreach (var gone in RowCache.Keys.Where(k => !index.ContainsKey(k)).ToList()) RowCache.Remove(gone);
            var stale = index.Where(kv => !RowCache.TryGetValue(kv.Key, out var c) || c.Version != kv.Value).Select(kv => kv.Key).ToList();
            foreach (var chunk in stale.Chunk(200))
                foreach (var rec in host.History.Get(chunk))
                    RowCache[rec.Id] = (index[rec.Id], BuildRow(host, rec));
            return RowCache.Values.Select(v => v.Row).OfType<HistoryRow>().OrderByDescending(r => r.Record.When).ToList();
        }
    }

    private static HistoryRow? BuildRow(AppHost host, BattleRecord rec)
    {
        {
            if (!ArenaInfoParser.TryParse(rec.ArenaJson, out var arena, out _)) return null;
            var players = BattleSummaryCodec.Deserialize(rec.SummaryJson);
            var f = TeamSummary.From(players.Where(p => p.IsFriendly).ToList());
            var e = TeamSummary.From(players.Where(p => !p.IsFriendly).ToList());
            var tiers = arena!.Vehicles.Select(v => host.Ships.Get(v.ShipId)?.Tier ?? 0).Where(t => t > 0).ToList();
            return new HistoryRow
            {
                Record = rec,
                Arena = arena,
                Friendly = f,
                Enemy = e,
                Share = TeamComparison.FriendlyShare(f, e),
                OwnShip = arena.Self is { } self ? host.Ships.Get(self.ShipId)?.Name ?? "?" : "—",
                AvgTier = tiers.Count > 0 ? tiers.Average() : null,
                Players = players,
            };
        }
    }

    /// <summary>Called by the view with the grid's current selection.</summary>
    public void SetSelection(IReadOnlyList<HistoryRow> rows)
    {
        CompareLines.Clear();
        if (rows.Count != 2)
        {
            CompareHint = rows.Count < 2 ? "Select two battles (Ctrl+click) to compare them." : "Select exactly two battles to compare.";
            return;
        }
        var (a, b) = (rows[0].When <= rows[1].When ? rows[0] : rows[1], rows[0].When <= rows[1].When ? rows[1] : rows[0]);
        CompareHint = $"{a.WhenText}  vs  {b.WhenText}";
        void L(string label, string x, string y) => CompareLines.Add(new HistoryCompareLine(label, x, y));
        L("Battle", $"{a.Type} · {a.Map}", $"{b.Type} · {b.Map}");
        L("Your ship", a.OwnShip, b.OwnShip);
        L("Average tier", a.AvgTier?.ToString("0.0") ?? "—", b.AvgTier?.ToString("0.0") ?? "—");
        L("Friendly avg win rate", a.FriendlyWrText, b.FriendlyWrText);
        L("Enemy avg win rate", a.EnemyWrText, b.EnemyWrText);
        L("Friendly avg PR", a.FriendlyPrText, b.FriendlyPrText);
        L("Enemy avg PR", a.EnemyPrText, b.EnemyPrText);
        L("Friendly avg damage", a.Friendly.AvgDamage?.ToString("N0") ?? "—", b.Friendly.AvgDamage?.ToString("N0") ?? "—");
        L("Enemy avg damage", a.Enemy.AvgDamage?.ToString("N0") ?? "—", b.Enemy.AvgDamage?.ToString("N0") ?? "—");
        L("Strength estimate (friendly share)", a.ShareText, b.ShareText);
        L("Hidden profiles", $"{a.Friendly.Hidden} / {a.Enemy.Hidden}", $"{b.Friendly.Hidden} / {b.Enemy.Hidden}");
        var common = a.Players.Where(p => !p.IsBot && p.Relation != 0)
            .Select(p => p.Name).Intersect(b.Players.Where(p => !p.IsBot).Select(p => p.Name), StringComparer.OrdinalIgnoreCase).ToList();
        L("Players in both battles", common.Count == 0 ? "none" : string.Join(", ", common.Take(6)) + (common.Count > 6 ? $" +{common.Count - 6}" : ""), "");
    }

    [RelayCommand]
    private void Open(HistoryRow? row)
    {
        row ??= Selected;
        if (row is not null) OpenRequested?.Invoke(row.Record);
    }

    [RelayCommand]
    private async Task Reload()
    {
        _dirty = true;
        await EnsureLoadedAsync();
    }
}
