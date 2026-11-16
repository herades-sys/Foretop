using Foretop.Core.Game;
using Foretop.Core.Replays;
using Foretop.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Foretop.Core.Personal;

/// <summary>
/// Adds replay facts (<see cref="ReplayFacts"/>) to own battle results: finds the replay the game wrote for the battle
/// (same battle key as the live roster), reads it once and stores the facts with the result. Battles whose replay
/// does not turn up within a day are marked as having none. Runs in the background; safe to call often.
/// </summary>
public sealed class PersonalEnricher(BattleResultStore store, ReplayLibrary replays, ILogger log)
{
    private static readonly TimeSpan WaitForReplay = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime Written, string? Key)> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows were changed.</summary>
    public event Action? Changed;

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return; // already running
        var changed = false;
        try
        {
            var todo = store.Since(DateTimeOffset.UtcNow.AddYears(-5), 5000)
                .Where(r => r.Status is ResultStatus.Ok && r.Extras?.Replay is null && r.Extras?.ReplayNote is null)
                .ToList();
            if (todo.Count == 0) return;

            var folder = replays.Folder;
            var byKey = folder is null || !Directory.Exists(folder) ? [] : IndexReplays(folder, todo.Min(r => r.StartedAt));
            foreach (var r in todo)
            {
                ct.ThrowIfCancellationRequested();
                var x = r.Extras ?? new PersonalExtras();
                if (!byKey.TryGetValue(r.BattleKey, out var path))
                {
                    if (DateTimeOffset.UtcNow - (r.EndedAt ?? r.StartedAt) > WaitForReplay)
                    {
                        store.Upsert(r with { Extras = x with { ReplayNote = "no replay of this battle was found (replays off, or deleted)" } });
                        changed = true;
                    }
                    continue;
                }
                // One bad replay must not stop the others (the list is newest first: it would block every older battle).
                ReplayData? data;
                try
                {
                    data = await replays.ParseAsync(path, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    store.Upsert(r with { Extras = x with { ReplayNote = "the replay could not be read: " + ex.Message } });
                    log.LogWarning("Replay facts for {Key}: {File} unreadable: {Error}", r.BattleKey, Path.GetFileName(path), ex.Message);
                    changed = true;
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogInformation("Replay facts for {Key}: {File} not readable now ({Error}), next run", r.BattleKey, Path.GetFileName(path), ex.Message);
                    continue;
                }
                if (data is null) return; // no game installation right now: try again later
                var self = data.Self;
                if (self is null || data.SelfAccountId is not long account || account != r.AccountId)
                {
                    store.Upsert(r with { Extras = x with { ReplayNote = "the replay belongs to another account" } });
                    changed = true;
                    continue;
                }
                var team = 0;
                var o = ReplayFacts.Compute(data.ResultsJson, account, self.ShipId, self.VehicleId, team, data.Players, data.Deaths,
                    data.TimeLeft, data.Duration, r.Result);
                store.Upsert(r with { Extras = x with { Replay = o.Facts with { ReplayFile = Path.GetFileName(path) }, ReplayNote = o.Note } });
                log.LogInformation("Replay facts for {Key}: {Facts} {Note}", r.BattleKey, o.Facts, o.Note);
                changed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.LogWarning("Replay facts not read: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
            if (changed) Changed?.Invoke();
        }
    }

    /// <summary>Battle key → replay path, for replays written after <paramref name="since"/> (header only, cached per file).</summary>
    private Dictionary<string, string> IndexReplays(string folder, DateTimeOffset since)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*.wowsreplay", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path).Equals("temp.wowsreplay", StringComparison.OrdinalIgnoreCase)) continue; // still recording
            var written = File.GetLastWriteTimeUtc(path);
            if (written < since.UtcDateTime.AddHours(-1)) continue;
            if (!_keys.TryGetValue(path, out var k) || k.Written != written)
            {
                string? key = null;
                try
                {
                    if (ArenaInfoParser.TryParse(ReplayHeaderReader.ReadArenaJson(path), out var info, out _)) key = info!.BattleKey;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                _keys[path] = k = (written, key);
            }
            if (k.Key is { } bk) result[bk] = path;
        }
        return result;
    }
}
