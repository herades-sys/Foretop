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

    private async Task RunSearchAsync()
    {
        _debounce.Stop();
        var text = Query.Trim();
        if (text.Length < 3) return;
        if (!_host.Api.HasApplicationId)
        {
            Message = "Add a Wargaming application ID in Settings to search players.";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var found = await _host.Api.SearchNicknamesAsync(_host.Region, text, 50, _cts.Token);
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
            IsSearching = false;
        }
    }

    private async Task LoadRecentAsync()
    {
        var rows = await Task.Run(() => HistoryViewModel.LoadRows(_host));
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
