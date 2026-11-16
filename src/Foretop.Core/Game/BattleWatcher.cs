using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.Game;

public enum WatcherState
{
    Stopped,
    GameNotFound,
    GameNotRunning,
    WaitingForBattle,
    InBattle,
}

public sealed record WatcherStatus(WatcherState State, string? GameRoot, string Message);

/// <summary>
/// Watches for tempArenaInfo.json. A FileSystemWatcher gives fast notice; a 2 s poll covers
/// setups where watcher events get lost. Files older than the running game process are
/// stale leftovers and are ignored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BattleWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CandidateScanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _rootOverride;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FileSystemWatcher? _fsw;
    private string? _watchedRoot;

    private DateTime _lastFileWriteUtc = DateTime.MinValue;
    private string? _currentKey;
    private WatcherStatus _status = new(WatcherState.Stopped, null, "Stopped");

    public BattleWatcher(Func<string?> rootOverride, ILogger<BattleWatcher>? log = null)
    {
        _rootOverride = rootOverride;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>Raised on a background thread when a new battle roster is read.</summary>
    public event Action<ArenaInfo>? BattleStarted;

    /// <summary>Raised when the arena file disappears or the game closes.</summary>
    public event Action? BattleEnded;

    public event Action<WatcherStatus>? StatusChanged;

    public WatcherStatus Status => _status;
    public ArenaInfo? Current { get; private set; }

    public void Start()
    {
        if (_loop is { IsCompleted: false } running)
        {
            if (_cts is { IsCancellationRequested: false }) return; // already running
            try { running.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            if (!running.IsCompleted) return; // still stopping: the next Start will try again
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        var finished = true;
        try { finished = _loop?.Wait(TimeSpan.FromSeconds(3)) ?? true; } catch (AggregateException) { }
        // Only forget the loop once it has ended; otherwise Start would run a second one beside it. Its token is
        // cancelled, so it ends on its own and Start waits for it.
        if (finished)
        {
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }
        DisposeWatcher();
        SetStatus(new WatcherStatus(WatcherState.Stopped, null, "Stopped"));
    }

    /// <summary>Check right away (e.g. after the user changed the game folder).</summary>
    public void Poke()
    {
        _nextCandidateScan = DateTime.MinValue;
        GameLocator.InvalidateCache();
        _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var nextProcessCheck = DateTime.MinValue;
        GameProcessInfo? game = null;
        System.Diagnostics.Process? gameProcess = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (gameProcess is not null)
                {
                    // Cheap: ask the process we already hold instead of listing all processes.
                    bool exited;
                    try { exited = gameProcess.HasExited; } catch (InvalidOperationException) { exited = true; }
                    if (exited)
                    {
                        gameProcess.Dispose();
                        gameProcess = null;
                        game = null;
                        nextProcessCheck = DateTime.MinValue;
                    }
                }
                if (gameProcess is null && DateTime.UtcNow >= nextProcessCheck)
                {
                    var found = GameLocator.FindRunningGameProcess();
                    game = found?.Info;
                    gameProcess = found?.Process;
                    nextProcessCheck = DateTime.UtcNow + ProcessCheckInterval;
                }

                var root = ResolveRoot(game);
                if (root is null)
                {
                    EndBattleIfAny();
                    SetStatus(new WatcherStatus(WatcherState.GameNotFound, null,
                        "World of Warships folder not found. Set it in Settings."));
                }
                else
                {
                    EnsureWatcher(root);
                    if (game is null)
                    {
                        EndBattleIfAny();
                        SetStatus(new WatcherStatus(WatcherState.GameNotRunning, root, "Game is not running."));
                    }
                    else
                    {
                        CheckArenaFile(root, game);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Battle watcher iteration failed");
            }

            try { await _wake.WaitAsync(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        gameProcess?.Dispose();
    }

    private string? ResolveRoot(GameProcessInfo? game)
    {
        var manual = _rootOverride();
        if (!string.IsNullOrWhiteSpace(manual) && Directory.Exists(manual)) return manual;
        if (game?.Root is not null) return game.Root;
        if (_watchedRoot is not null && GameLocator.LooksLikeGameRoot(_watchedRoot)) return _watchedRoot;

        // Scanning drives is not free: at most every 5 minutes (a running game is found through its process anyway).
        if (DateTime.UtcNow < _nextCandidateScan) return null;
        _nextCandidateScan = DateTime.UtcNow + CandidateScanInterval;
        return GameLocator.CandidateRoots().FirstOrDefault();
    }

    private DateTime _nextCandidateScan = DateTime.MinValue;

    private void CheckArenaFile(string root, GameProcessInfo game)
    {
        var file = GameLocator.FindArenaFiles(root).FirstOrDefault();
        var processStartUtc = game.StartTime.ToUniversalTime();

        if (file is null || file.LastWriteTimeUtc < processStartUtc - StaleGrace)
        {
            EndBattleIfAny();
            SetStatus(new WatcherStatus(WatcherState.WaitingForBattle, root, "Waiting for a battle…"));
            return;
        }

        if (file.LastWriteTimeUtc == _lastFileWriteUtc && _currentKey is not null)
            return; // same battle, nothing new

        var info = ReadWithRetry(file.FullName);
        if (info is null) return; // try again next tick (partial write)

        _lastFileWriteUtc = file.LastWriteTimeUtc;
        if (info.BattleKey == _currentKey) return;

        _currentKey = info.BattleKey;
        Current = info;
        _log.LogInformation("Battle detected: {Key} {Type} on {Map}, {Count} vehicles, file {File}",
            info.BattleKey, info.MatchGroup, info.MapName, info.Vehicles.Count, file.FullName);
        SetStatus(new WatcherStatus(WatcherState.InBattle, root,
            $"{BattleTypes.Label(info.BattleType, info.MatchGroup)} — {info.MapName}"));
        BattleStarted?.Invoke(info);
    }

    private ArenaInfo? ReadWithRetry(string path)
    {
        string? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var text = sr.ReadToEnd();
                if (ArenaInfoParser.TryParse(text, out var info, out lastError)) return info;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException ex) { lastError = ex.Message; }
            catch (UnauthorizedAccessException ex) { lastError = ex.Message; }
            Thread.Sleep(150);
        }
        _log.LogWarning("Could not read {Path}: {Error}", path, lastError);
        return null;
    }

    private void EndBattleIfAny()
    {
        if (_currentKey is null) return;
        _log.LogInformation("Battle ended: {Key}", _currentKey);
        _currentKey = null;
        Current = null;
        BattleEnded?.Invoke();
    }

    private void EnsureWatcher(string root)
    {
        if (string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase) && _fsw is not null) return;
        DisposeWatcher();
        _watchedRoot = root;
        try
        {
            _fsw = new FileSystemWatcher(root, GameLocator.ArenaFileName)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 16 * 1024,
            };
            FileSystemEventHandler h = (_, _) => _wake.Release();
            _fsw.Created += h;
            _fsw.Changed += h;
            _fsw.Deleted += h;
            _fsw.Renamed += (_, _) => _wake.Release();
            _fsw.Error += (_, e) => _log.LogWarning(e.GetException(), "File watcher error; polling continues");
            _fsw.EnableRaisingEvents = true;
            _log.LogInformation("Watching {Root} for {File}", root, GameLocator.ArenaFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not start file watcher on {Root}; polling only", root);
            _fsw = null;
        }
    }

    private void DisposeWatcher()
    {
        _fsw?.Dispose();
        _fsw = null;
    }

    private void SetStatus(WatcherStatus s)
    {
        if (s == _status) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _wake.Dispose();
    }
}
