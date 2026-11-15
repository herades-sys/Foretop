using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Game;
using Foretop.Core.Replays;

namespace Foretop.App.ViewModels;

public sealed record ReplayRow(ReplayEntry Entry, string WhenText, string Map, string Ship, string Type, string SizeText);

/// <summary>One line of the team lists beside the minimap, refreshed while the replay plays.</summary>
public sealed partial class ReplayShipRow(SceneShip ship) : ObservableObject
{
    public SceneShip Ship { get; } = ship;
    public string Name => Ship.Player.Name;
    public string ShipName => Ship.ShipName + (Ship.IsTestShip ? " (test ship)" : "");
    public bool IsSelf => Ship.IsSelf;
    public string Species => Ship.Species;

    [ObservableProperty] private double _healthFraction;
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private bool _isDead;
    [ObservableProperty] private bool _isHidden;

    public void Update(float t)
    {
        IsDead = Ship.IsDead(t);
        IsHidden = !IsDead && !Ship.VisibleAt(t);
        if (Ship.IsTestShip)
        {
            HealthFraction = 0;
            HealthText = IsDead ? "sunk" : "test ship"; // no hit points for ships in testing
            return;
        }
        var hp = Ship.HealthAt(t);
        HealthFraction = Ship.MaxHealth > 0 ? Math.Clamp(hp / Ship.MaxHealth, 0, 1) : 0;
        HealthText = IsDead ? "sunk" : Ship.MaxHealth > 0 ? hp.ToString("N0", CultureInfo.CurrentCulture) : "";
    }
}

/// <summary>A chat line; Channel is "Team" or "All". Clicking it jumps to its time.</summary>
public sealed record ChatRow(float T, string TimeText, string Sender, string SenderLevel, string Channel, string Text, string? SenderShip);

public sealed record KillRow(string TimeText, string Killer, string KillerLevel, string Victim, string VictimLevel, string VictimShip);

/// <summary>Replay list and minimap player.</summary>
public sealed partial class ReplaysViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _loaded;
    private double _lastListRefresh;

    public ReplaysViewModel(AppHost host)
    {
        _host = host;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<ReplayRow> Replays { get; } = [];
    public ObservableCollection<ReplayShipRow> Allies { get; } = [];
    public ObservableCollection<ReplayShipRow> Enemies { get; } = [];
    public ObservableCollection<KillRow> Kills { get; } = [];
    public ObservableCollection<ChatRow> Chat { get; } = [];
    /// <summary>The last few lines, shown over the minimap like the in-game chat.</summary>
    public ObservableCollection<ChatRow> RecentChat { get; } = [];
    private List<ChatRow> _allChat = [];
    public IReadOnlyList<double> Speeds { get; } = [1, 2, 5, 10, 20, 40];

    [ObservableProperty] private ReplayRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOpening;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private ReplayScene? _scene;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private double _time;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _speed = 10;
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private bool _showTrails = true;
    [ObservableProperty] private bool _showNames = true;
    [ObservableProperty] private bool _showChat = true;
    [ObservableProperty] private string _chatSummary = "";

    public bool HasScene => Scene is not null;

    partial void OnSceneChanged(ReplayScene? value) => OnPropertyChanged(nameof(HasScene));

    partial void OnTimeChanged(double value)
    {
        UpdateTexts();
        if (!IsPlaying) RefreshLists(); // scrubbing
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsLoading = true;
        Message = null;
        try
        {
            var list = await Task.Run(_host.Replays.List);
            Replays.Clear();
            foreach (var e in list) Replays.Add(Row(e));
            _loaded = true;
            Summary = _host.Replays.Folder is null
                ? "No game installation found, so there is no replays folder to read."
                : list.Count == 0
                    ? $"No replays in {_host.Replays.Folder}. The game saves them when replays are switched on in its settings."
                    : $"{list.Count} replay{(list.Count == 1 ? "" : "s")} in {_host.Replays.Folder}";
            if (Scene is null && Replays.Count > 0) Selected = Replays[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = "The replays folder could not be read: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private ReplayRow Row(ReplayEntry e)
    {
        var ship = ShipName(e.PlayerVehicle);
        var type = BattleTypes.Label(BattleTypes.FromMatchGroup(e.MatchGroup), e.MatchGroup);
        return new ReplayRow(e, e.When.ToString("g", CultureInfo.CurrentCulture), BattleTypes.PrettyMapName(e.MapDisplayName), ship, type,
            (e.SizeBytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB");
    }

    /// <summary>"PFSB599-Black-Jean-Bart" → the game's display name, found by the index part (PFSB599).</summary>
    private string ShipName(string vehicle)
    {
        var index = vehicle.Split('-', '_')[0];
        var data = _host.ShipData.Data?.Ships.FirstOrDefault(s => s.Name.StartsWith(index + "_", StringComparison.OrdinalIgnoreCase));
        return data?.DisplayName ?? vehicle.Replace('-', ' ');
    }

    [RelayCommand]
    private async Task OpenAsync(ReplayRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        Pause();
        IsOpening = true;
        Message = null;
        try
        {
            var (data, map) = await _host.Replays.OpenAsync(row.Entry.Path);
            var scene = ReplayScene.Build(data, map, id => _host.ShipData.FindById(id) is { } s ? (s.DisplayName, s.Species, s.IsTestShip) : null);
            Allies.Clear();
            Enemies.Clear();
            foreach (var s in scene.Ships.Where(s => s.Relation != 2).OrderByDescending(s => s.IsSelf).ThenBy(s => ClassOrder(s.Species)).ThenBy(s => s.ShipName))
                Allies.Add(new ReplayShipRow(s));
            foreach (var s in scene.Ships.Where(s => s.Relation == 2).OrderBy(s => ClassOrder(s.Species)).ThenBy(s => s.ShipName))
                Enemies.Add(new ReplayShipRow(s));
            _allChat = data.Chat.Select(c => ChatLine(c, scene)).ToList();
            ChatSummary = _allChat.Count == 0 ? "No chat in this replay." : "No messages yet.";
            Chat.Clear();
            RecentChat.Clear();
            Title = $"{row.Map} · {row.Type}";
            Subtitle = $"{data.PlayerName} in {row.Ship} · {row.WhenText} · {FormatTime(data.Duration)} recorded";
            Warning = data.Warnings.Count == 0 ? null : string.Join(" ", data.Warnings);
            Duration = Math.Max(1, data.Duration);
            Scene = scene;
            Time = 0;
            RefreshLists();
            UpdateTexts();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Message = "This replay could not be opened: " + ex.Message;
        }
        finally
        {
            IsOpening = false;
        }
    }

    private static ChatRow ChatLine(ReplayChat c, ReplayScene scene)
    {
        var ship = c.Sender is { } p ? scene.Ships.FirstOrDefault(s => s.Player == p) : null;
        return new ChatRow(c.T, FormatClock(scene.ClockAt(c.T)) ?? FormatTime(c.T),
            c.Sender?.Name ?? "Unknown player", c.Sender is { } pl ? Level(pl.Relation) : "",
            c.IsTeam ? "Team" : "All", c.Text, ship?.ShipName);
    }

    /// <summary>Jumps to a chat line (a little before it, so its context plays).</summary>
    public void JumpTo(float t)
    {
        if (Scene is null) return;
        Time = Math.Clamp(t - 3, 0, Duration);
        RefreshLists();
    }

    private static int ClassOrder(string species) => species switch
    {
        "AirCarrier" => 0, "Battleship" => 1, "Cruiser" => 2, "Destroyer" => 3, "Submarine" => 4, _ => 5,
    };

    [RelayCommand]
    private void PlayPause()
    {
        if (Scene is null) return;
        if (IsPlaying) { Pause(); return; }
        if (Time >= Duration - 0.5) Time = 0;
        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
    }

    private void Pause()
    {
        IsPlaying = false;
        _timer.Stop();
        RefreshLists();
    }

    [RelayCommand]
    private void SetSpeed(double speed) => Speed = speed;

    [RelayCommand]
    private void Skip(string seconds)
    {
        if (Scene is null || !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return;
        Time = Math.Clamp(Time + s, 0, Duration);
        RefreshLists();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_host.Replays.Folder is { } f && Directory.Exists(f)) Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
    }

    private void Tick()
    {
        var dt = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        var next = Time + dt * Speed;
        if (next >= Duration)
        {
            Time = Duration;
            Pause();
            return;
        }
        Time = next;
        _lastListRefresh += dt;
        if (_lastListRefresh > 0.25)
        {
            _lastListRefresh = 0;
            RefreshLists();
        }
    }

    private void RefreshLists()
    {
        if (Scene is not { } scene) return;
        var t = (float)Time;
        foreach (var r in Allies) r.Update(t);
        foreach (var r in Enemies) r.Update(t);
        var shown = scene.Kills.Where(k => k.T <= t).Reverse().ToList();
        if (shown.Count != Kills.Count)
        {
            Kills.Clear();
            foreach (var k in shown)
                Kills.Add(new KillRow(FormatClock(scene.ClockAt(k.T)) ?? FormatTime(k.T), k.Killer, Level(k.KillerRelation), k.Victim, Level(k.VictimRelation), k.VictimShip));
        }

        // Chat up to now, oldest first as in the game; the overlay keeps lines of the last 30 s (at most 6).
        var said = _allChat.Count(c => c.T <= t);
        if (said != Chat.Count)
        {
            Chat.Clear();
            foreach (var c in _allChat.Take(said)) Chat.Add(c);
        }
        var recent = _allChat.Take(said).Where(c => c.T > t - 30).TakeLast(6).ToList();
        if (!recent.SequenceEqual(RecentChat))
        {
            RecentChat.Clear();
            foreach (var c in recent) RecentChat.Add(c);
        }
    }

    private static string Level(int relation) => relation switch { 0 => "Self", 1 => "Ally", 2 => "Enemy", _ => "" };

    private void UpdateTexts()
    {
        var t = (float)Time;
        TimeText = $"{FormatTime(t)} / {FormatTime((float)Duration)}";
        ClockText = Scene?.ClockAt(t) is float c ? FormatClock(c)! : "";
    }

    private static string FormatTime(float s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private static string? FormatClock(float? left) => left is float l ? FormatTime(l) : null;
}
