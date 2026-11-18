using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Api;
using Foretop.Core.Clans;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

/// <summary>One member in the clan table.</summary>
public sealed record ClanMemberRow(long AccountId, string Name, string Role, int RoleOrder, DateTimeOffset? JoinedAt, int? DaysInClan,
    int? Battles, double? WinRate, DateTimeOffset? LastBattle, bool Hidden)
{
    public string JoinedText => JoinedAt is { } j ? j.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "—";
    public string DaysText => DaysInClan is int d ? d.ToString("N0", CultureInfo.CurrentCulture) : "—";
    public string BattlesText => Hidden ? "hidden" : Battles is int b ? b.ToString("N0", CultureInfo.CurrentCulture) : "—";
    public string WinRateText => WinRate is double w ? w.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "—";

    /// <summary>Win-rate colour from 50 battles, as in Live Battle.</summary>
    public double? WinRateRated => Battles >= 50 ? WinRate : null;
    public string LastBattleText => LastBattle is { } l ? l.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : "—";
}

/// <summary>A change in the member list: who joined (official join date) or left (seen by Foretop).</summary>
public sealed record ClanChangeRow(string Name, string When, string Detail);

/// <summary>Clan search: find a clan, see its members, who joined recently and who left since Foretop's first check.</summary>
public sealed partial class ClansViewModel : ObservableObject
{
    /// <summary>"Joined recently" covers this many days.</summary>
    public const int RecentDays = 30;

    private readonly AppHost _host;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _trackTimer;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _loadCts;
    private bool _settingTracked;

    public ClansViewModel(AppHost host)
    {
        _host = host;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) => await RunSearchAsync(),
            Dispatcher.CurrentDispatcher);
        _debounce.Stop();
        // Tracked clans: a first check a minute after start, then every half hour (each clan at most every 6 hours).
        _trackTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) => await CheckTrackedAsync(),
            Dispatcher.CurrentDispatcher);
    }

    /// <summary>Starts the background checks of tracked clans (not in development snapshot runs).</summary>
    public void StartTracking() => _trackTimer.Start();

    public event Action<string, Region>? ProfileRequested;

    public ObservableCollection<ClanListing> Results { get; } = [];
    public ObservableCollection<ClanRecord> TrackedClans { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _message = "Type at least two letters of a clan tag or name.";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClan), nameof(Title), nameof(Facts), nameof(RenameText), nameof(HistoryNote))]
    private ClanView? _clan;

    [ObservableProperty] private IReadOnlyList<ClanMemberRow> _members = [];
    [ObservableProperty] private IReadOnlyList<ClanChangeRow> _joined = [];
    [ObservableProperty] private IReadOnlyList<ClanChangeRow> _left = [];
    [ObservableProperty] private bool _isTracked;
    [ObservableProperty] private string? _error;

    public bool HasClan => Clan is not null;
    public string Title => Clan is { } c ? $"[{c.Info.Tag}] {c.Info.Name}" : "";

    public string Facts => Clan is not { Info: var i } ? "" : string.Join("  ·  ", new[]
    {
        $"{i.MembersCount} members",
        i.CreatedAt is { } created ? $"created {created.LocalDateTime:d}" : null,
        i.LeaderName is { } leader ? $"commander {leader}" : null,
        i.CreatorName is { } creator && creator != i.LeaderName ? $"founded by {creator}" : null,
        i.Disbanded ? "disbanded" : null,
        Regions.Label(Clan!.Region),
    }.Where(s => s is not null));

    public string? RenameText => Clan?.Info is { RenamedAt: { } at } i && (i.OldTag is not null || i.OldName is not null)
        ? $"Renamed {at.LocalDateTime:d} from [{i.OldTag ?? i.Tag}] {i.OldName ?? i.Name}"
        : null;

    public string HistoryNote => Clan?.Record is not { } r ? ""
        : r.FirstChecked >= DateTimeOffset.UtcNow.AddMinutes(-1) && Left.Count == 0
            ? "First look at this clan: from now on Foretop notices who leaves, each time it checks the clan. The Wargaming API lists only current members, so earlier departures are unknown. Track the clan to have it checked every 6 hours while Foretop runs."
            : $"Foretop has checked this clan since {r.FirstChecked.LocalDateTime:g} (last check {r.LastChecked.LocalDateTime:g}); departures before the first check are unknown."
              + (r.Tracked ? " Tracked: checked every 6 hours while Foretop runs." : " Track the clan to have it checked every 6 hours while Foretop runs.");

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        if (value.Trim().Length >= 2) _debounce.Start();
        else
        {
            Results.Clear();
            Message = "Type at least two letters of a clan tag or name.";
        }
    }

    partial void OnIsTrackedChanged(bool value)
    {
        if (_settingTracked || Clan is not { } c) return;
        _host.Clans.Store.SetTracked(c.Region, c.Info.ClanId, value);
        Clan = c with { Record = _host.Clans.Store.Get(c.Region, c.Info.ClanId) };
        LoadTracked();
    }

    public void OnShown() => LoadTracked();

    private void LoadTracked()
    {
        TrackedClans.Clear();
        foreach (var t in _host.Clans.Store.Tracked()) TrackedClans.Add(t);
    }

    private async Task RunSearchAsync()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        var mine = _searchCts = new CancellationTokenSource();
        var text = Query.Trim();
        IsSearching = true;
        try
        {
            var found = await _host.Clans.SearchAsync(_host.Region, text, mine.Token);
            if (mine.IsCancellationRequested) return;
            Results.Clear();
            foreach (var c in found) Results.Add(c);
            Message = found.Count switch
            {
                0 => $"No clan found for \"{text}\" on {Regions.Label(_host.Region)}.",
                >= 30 => "The first 30 clans; type more of the tag or name to narrow it down.",
                1 => "1 clan",
                var n => $"{n} clans",
            };
        }
        catch (WgApiException ex) { Message = ex.Message; }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(mine, _searchCts)) IsSearching = false; }
    }

    [RelayCommand]
    private Task Open(ClanListing? clan) => clan is null ? Task.CompletedTask : LoadAsync(_host.Region, clan.ClanId);

    [RelayCommand]
    private Task OpenTracked(ClanRecord? clan) => clan is null ? Task.CompletedTask : LoadAsync(clan.Region, clan.ClanId);

    [RelayCommand]
    private Task Refresh() => Clan is { } c ? LoadAsync(c.Region, c.Info.ClanId) : Task.CompletedTask;

    [RelayCommand]
    private void OpenMember(ClanMemberRow? row)
    {
        if (row is not null && Clan is { } c) ProfileRequested?.Invoke(row.Name, c.Region);
    }

    [RelayCommand]
    private void OpenWowsNumbers()
    {
        if (Clan is { } c) Process.Start(new ProcessStartInfo(ClanService.WowsNumbersUrl(c.Region, c.Info.ClanId, c.Info.Tag)) { UseShellExecute = true });
    }

    /// <summary>Opens the clan with this tag (Live Battle → right-click → Open clan).</summary>
    public async Task OpenTagAsync(string tag, Region region)
    {
        Query = tag;
        _debounce.Stop();
        Results.Clear();
        Message = $"Clan [{tag}] from the battle";
        try
        {
            if (await _host.Clans.FindByTagAsync(region, tag, CancellationToken.None) is { } c) await LoadAsync(region, c.ClanId);
            else Error = $"No clan [{tag}] found on {Regions.Label(region)}.";
        }
        catch (WgApiException ex) { Error = ex.Message; }
    }

    public async Task LoadAsync(Region region, long clanId)
    {
        _loadCts?.Cancel();
        var mine = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        try
        {
            var view = await _host.Clans.LoadAsync(region, clanId, withStats: true, mine.Token);
            if (mine.IsCancellationRequested) return;
            if (view is null)
            {
                Error = "The server has no clan with this ID any more.";
                return;
            }
            Show(view);
            LoadTracked();
        }
        catch (WgApiException ex) { Error = ex.Message; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _host.Log.LogWarning(ex, "Clan {Id} could not be shown", clanId);
            Error = "The clan could not be loaded: " + ex.Message;
        }
        finally { if (ReferenceEquals(mine, _loadCts)) IsLoading = false; }
    }

    private void Show(ClanView view)
    {
        var now = DateTimeOffset.UtcNow;
        Members = view.Members.Select(m =>
        {
            view.Stats.TryGetValue(m.AccountId, out var s);
            return new ClanMemberRow(m.AccountId, m.Name, ClanService.RoleLabel(m.Role), ClanService.RoleOrder(m.Role), m.JoinedAt,
                m.JoinedAt is { } j ? (int)(now - j).TotalDays : null, s?.Battles, s?.WinRate, s?.LastBattle, s?.Hidden == true);
        }).ToList();
        Joined = view.Members.Where(m => m.JoinedAt is { } j && now - j <= TimeSpan.FromDays(RecentDays))
            .OrderByDescending(m => m.JoinedAt)
            .Select(m => new ClanChangeRow(m.Name, m.JoinedAt!.Value.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
                ClanService.RoleLabel(m.Role)))
            .ToList();
        Left = view.Left.Select(l => new ClanChangeRow(l.Name,
                l.LastSeen.LocalDateTime.Date == l.Noticed.LocalDateTime.Date
                    ? l.Noticed.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)
                    : $"{l.LastSeen.LocalDateTime:d} – {l.Noticed.LocalDateTime:d}",
                ClanService.RoleLabel(l.Role) + (l.JoinedAt is { } j ? $", member since {j.LocalDateTime:d}" : "")))
            .ToList();
        _settingTracked = true;
        IsTracked = view.Record?.Tracked == true;
        _settingTracked = false;
        Clan = view;
    }

    private async Task CheckTrackedAsync()
    {
        _trackTimer.Interval = TimeSpan.FromMinutes(30);
        try
        {
            if (await _host.Clans.CheckTrackedAsync(CancellationToken.None) > 0)
            {
                LoadTracked();
                if (Clan is { } c && !IsLoading) // the clan on screen may have changed
                    Show(c with { Left = _host.Clans.Store.Left(c.Region, c.Info.ClanId), Record = _host.Clans.Store.Get(c.Region, c.Info.ClanId) });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Log.LogWarning(ex, "Tracked clans not checked");
        }
    }
}
