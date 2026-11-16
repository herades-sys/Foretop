using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Api;
using Microsoft.Extensions.Logging;

namespace Foretop.App.ViewModels;

public sealed record PlayerSearchResult(string Name, long AccountId);

/// <summary>Find players by name on the selected server, plus the players from your recent battles.</summary>
public sealed partial class PlayerSearchViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;

    public PlayerSearchViewModel(AppHost host)
    {
        _host = host;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, async (_, _) => await RunSearchAsync(),
            Dispatcher.CurrentDispatcher);
        _debounce.Stop();
    }

    public event Action<string>? ProfileRequested;

    public ObservableCollection<PlayerSearchResult> Results { get; } = [];
    public ObservableCollection<EncounteredPlayer> RecentPlayers { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _message = "Type at least three letters of a player name.";
    [ObservableProperty] private bool _isSearching;

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        if (value.Trim().Length >= 3) _debounce.Start();
        else
        {
            Results.Clear();
            Message = "Type at least three letters of a player name.";
        }
    }

    public void OnShown()
    {
        _ = LoadRecentAsync();
    }

    /// <summary>An account id: the player with that id on the selected server (a name search would find nothing).</summary>
    private async Task FindByIdAsync(long id, string text)
    {
        _cts?.Cancel();
        var mine = _cts = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var raw = await _host.Api.GetAccountInfoRawAsync(_host.Region, [id], RequestPriority.Normal, mine.Token);
            if (text != Query.Trim()) return;
            Results.Clear();
            var name = raw.TryGetValue(id, out var el) && el is { } e && e.ValueKind == System.Text.Json.JsonValueKind.Object
                       && e.TryGetProperty("nickname", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
            if (name is not null) Results.Add(new PlayerSearchResult(name, id));
            Message = name is null
                ? $"No account {id} on {Regions.Label(_host.Region)}."
                : $"Account {id} on {Regions.Label(_host.Region)}: {name}";
        }
        catch (OperationCanceledException) { }
        catch (WgApiException ex)
        {
            Message = ex.Message;
            _host.Log.LogInformation("Account id lookup failed: {Error}", ex.Message);
        }
        finally
        {
            if (_cts == mine) IsSearching = false;
        }
    }

    private async Task RunSearchAsync()
    {
        _debounce.Stop();
        var text = Query.Trim();
        if (text.Length < 3) return;
        // Digits only (from 6): an account id, as in profile links (worldofwarships.eu/…/549692505-Name).
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9]{6,12}$") && long.TryParse(text, out var accountId) && _host.Api.HasApplicationId)
        {
            await FindByIdAsync(accountId, text);
            return;
        }
        // Game names are 3–24 letters, digits and underscores: anything else would only return an API error.
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z0-9_]{3,24}$"))
        {
            Results.Clear();
            Message = "Player names have 3 to 24 letters (A–Z), digits and underscores.";
            return;
        }
        if (!_host.Api.HasApplicationId)
        {
            Message = "Add a Wargaming application ID in Settings to search players.";
            return;
        }
        _cts?.Cancel();
        var mine = _cts = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var found = await _host.Api.SearchNicknamesAsync(_host.Region, text, 50, mine.Token);
            if (text != Query.Trim()) return;
            Results.Clear();
            foreach (var f in found) Results.Add(new PlayerSearchResult(f.Nickname, f.AccountId));
            Message = found.Count == 0
                ? $"No player on {Regions.Label(_host.Region)} starts with “{text}”."
                : $"{found.Count}{(found.Count == 50 ? "+" : "")} players on {Regions.Label(_host.Region)} starting with “{text}”";
        }
        catch (OperationCanceledException) { }
        catch (WgApiException ex)
        {
            Message = ex.Message;
            _host.Log.LogInformation("Player search failed: {Error}", ex.Message);
        }
        finally
        {
            if (_cts == mine) IsSearching = false; // an older, cancelled search must not hide the newer one's progress
        }
    }

    private async Task LoadRecentAsync()
    {
        List<HistoryRow> rows;
        try
        {
            rows = await Task.Run(() => HistoryViewModel.LoadRows(_host));
        }
        catch (Exception ex)
        {
            _host.Log.LogWarning(ex, "Recent players not loaded");
            return;
        }
        RecentPlayers.Clear();
        var recent = rows.Take(30)
            .SelectMany(r => r.Arena.Vehicles.Where(v => !v.IsBot && !v.IsSelf).Select(v => (r, v)))
            .GroupBy(x => x.v.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(x => x.r.When)).ThenByDescending(g => g.Count())
            .Take(24);
        foreach (var g in recent)
        {
            var last = g.OrderByDescending(x => x.r.When).First();
            RecentPlayers.Add(new EncounteredPlayer(last.v.Name, g.Count(), g.Count(x => x.v.IsFriendly), g.Count(x => !x.v.IsFriendly), last.r.When, ""));
        }
    }

    [RelayCommand]
    private void Open(object? item)
    {
        var name = item switch
        {
            PlayerSearchResult r => r.Name,
            EncounteredPlayer p => p.Name,
            string s when s.Trim().Length >= 3 => s.Trim(),
            _ => null,
        };
        if (name is not null) ProfileRequested?.Invoke(name);
    }

    [RelayCommand]
    private void OpenTyped() => Open(Results.FirstOrDefault(r => string.Equals(r.Name, Query.Trim(), StringComparison.OrdinalIgnoreCase))
                                     ?? (object)Query);
}
