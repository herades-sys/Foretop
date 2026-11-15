using System.Diagnostics;
using System.Runtime.Versioning;

namespace Foretop.Core.Game;

public enum CheckLevel
{
    Ok,
    Warning,
    Problem,
    Info,
}

public sealed record DiagnosticCheck(string Name, CheckLevel Level, string Detail);

/// <summary>
/// Explains, step by step, whether battle detection can work on this PC. Read-only: it looks at the
/// process list and the file system, nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameDiagnostics
{
    public static List<DiagnosticCheck> Run(string? manualRoot) => Run(manualRoot, GameLocator.FindRunningGame, CountGameProcesses);

    /// <summary>Testable core: the process lookups are passed in.</summary>
    internal static List<DiagnosticCheck> Run(string? manualRoot, Func<GameProcessInfo?> findGame, Func<int> countProcesses)
    {
        var checks = new List<DiagnosticCheck>();

        // 1. Game process
        var count = countProcesses();
        var game = findGame();
        if (game is not null)
            checks.Add(new("Game running", CheckLevel.Ok, $"{game.ExePath} (started {game.StartTime:t})"));
        else if (count > 0)
            checks.Add(new("Game running", CheckLevel.Warning,
                "The game is running, but Foretop may not read its path (the game probably runs as administrator). " +
                "Set the game folder below, or run Foretop the same way."));
        else
            checks.Add(new("Game running", CheckLevel.Info, "Not running. Start World of Warships to detect battles."));

        // 2. Game folder
        string? root;
        if (!string.IsNullOrWhiteSpace(manualRoot))
        {
            root = manualRoot;
            checks.Add(GameLocator.LooksLikeGameRoot(manualRoot)
                ? new("Game folder", CheckLevel.Ok, $"{manualRoot} (set manually)")
                : new("Game folder", CheckLevel.Problem, $"{manualRoot} does not look like a World of Warships folder (no game_info.xml, or bin + replays)."));
        }
        else
        {
            root = game?.Root ?? GameLocator.CandidateRoots().FirstOrDefault();
            checks.Add(root is not null
                ? new("Game folder", CheckLevel.Ok, $"{root} (found automatically)")
                : new("Game folder", CheckLevel.Problem, "Not found. Set it below (the folder that contains WorldOfWarships.exe and the replays folder)."));
        }
        if (root is null) return checks;

        // 3. Replays: their presence shows that replays are enabled in the game.
        var replays = Path.Combine(root, "replays");
        if (!Directory.Exists(replays))
        {
            checks.Add(new("Replays", CheckLevel.Problem,
                "No replays folder. Enable replays in the game settings — the game only writes the battle roster when replays are on."));
        }
        else
        {
            var files = SafeFiles(replays, "*.wowsreplay");
            if (files.Count == 0)
                checks.Add(new("Replays", CheckLevel.Warning,
                    "The replays folder has no replays yet. If you have played since installing, replays are probably disabled in the game settings."));
            else
            {
                var newest = files.Max(f => f.LastWriteTime);
                checks.Add(new("Replays", CheckLevel.Ok, $"{files.Count} replays, newest {newest:g} — replays are enabled."));
            }
        }

        // 4. The arena file itself
        var arena = GameLocator.FindArenaFiles(root).FirstOrDefault();
        if (arena is null)
        {
            checks.Add(new("Battle file", CheckLevel.Info,
                "No tempArenaInfo.json right now. It appears when a battle loads and disappears when it ends."));
        }
        else
        {
            string text;
            try
            {
                using var fs = new FileStream(arena.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            catch (IOException ex)
            {
                checks.Add(new("Battle file", CheckLevel.Warning, $"{arena.FullName} exists but could not be read: {ex.Message}"));
                return checks;
            }

            var stale = game is not null && arena.LastWriteTime < game.StartTime.AddSeconds(-30);
            if (!ArenaInfoParser.TryParse(text, out var info, out var error))
                checks.Add(new("Battle file", CheckLevel.Warning, $"{arena.FullName}: {error}"));
            else if (stale || game is null)
                checks.Add(new("Battle file", CheckLevel.Info,
                    $"Left over from an earlier battle ({arena.LastWriteTime:g}, {info!.Vehicles.Count} players); ignored until a new battle loads."));
            else
                checks.Add(new("Battle file", CheckLevel.Ok,
                    $"Current battle: {BattleTypes.Label(info!.BattleType, info.MatchGroup)} on {info.MapName}, {info.Vehicles.Count} players."));
        }
        return checks;
    }

    private static int CountGameProcesses()
    {
        var running = Process.GetProcessesByName(GameLocator.ProcessName);
        foreach (var p in running) p.Dispose();
        return running.Length;
    }

    private static List<FileInfo> SafeFiles(string dir, string pattern)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.AllDirectories).Take(5000).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
