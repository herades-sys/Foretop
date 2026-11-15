using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Foretop.Core.Game;

public sealed record GameProcessInfo(string ExePath, string? Root, DateTime StartTime);

/// <summary>Finds the World of Warships install folder. Reads paths only; never touches the game process.</summary>
[SupportedOSPlatform("windows")]
public static partial class GameLocator
{
    public const string ProcessName = "WorldOfWarships64";
    public const string ArenaFileName = "tempArenaInfo.json";

    /// <summary>The running client, if any. The exe sits at &lt;root&gt;\bin\&lt;build&gt;\bin64\.</summary>
    public static GameProcessInfo? FindRunningGame()
    {
        var found = FindRunningGameProcess();
        found?.Process.Dispose();
        return found?.Info;
    }

    /// <summary>
    /// Like <see cref="FindRunningGame"/>, but hands back the open process so the caller can watch
    /// <see cref="Process.HasExited"/> instead of listing every process again (caller disposes it).
    /// </summary>
    public static (GameProcessInfo Info, Process Process)? FindRunningGameProcess()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(ProcessName); }
        catch (InvalidOperationException) { return null; }

        (GameProcessInfo, Process)? result = null;
        foreach (var p in processes)
        {
            if (result is null)
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe is not null)
                    {
                        result = (new GameProcessInfo(exe, RootFromExe(exe), p.StartTime), p);
                        continue;
                    }
                }
                catch (Win32Exception) { /* access denied: game elevated, app not */ }
                catch (InvalidOperationException) { /* exited meanwhile */ }
            }
            p.Dispose();
        }
        return result;
    }

    public static bool IsGameRunning()
    {
        var ps = Process.GetProcessesByName(ProcessName);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    public static string? RootFromExe(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        for (var i = 0; i < 5 && dir is not null; i++)
        {
            if (LooksLikeGameRoot(dir)) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static bool LooksLikeGameRoot(string dir) =>
        Directory.Exists(dir) &&
        (File.Exists(Path.Combine(dir, "game_info.xml")) ||
         File.Exists(Path.Combine(dir, "WorldOfWarships.exe")) ||
         (Directory.Exists(Path.Combine(dir, "bin")) && Directory.Exists(Path.Combine(dir, "replays"))));

    /// <summary>Install folders worth checking when the game is not running.</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in RawCandidates())
        {
            string full;
            try { full = Path.GetFullPath(c); } catch { continue; }
            if (seen.Add(full) && LooksLikeGameRoot(full)) yield return full;
        }
    }

    /// <summary>Best guess for the install folder: running game first, then known locations.</summary>
    public static string? FindGameRoot() => FindRunningGame()?.Root ?? CandidateRoots().FirstOrDefault();

    private static IEnumerable<string> RawCandidates()
    {
        foreach (var path in GameCenterEntries()) yield return path;
        foreach (var path in UninstallEntries()) yield return path;

        foreach (var drive in SafeDrives())
        {
            yield return Path.Combine(drive, "Games", "World_of_Warships");
            yield return Path.Combine(drive, "Games", "World_of_Warships_EU");
            yield return Path.Combine(drive, "Games", "World_of_Warships_NA");
            yield return Path.Combine(drive, "Games", "World_of_Warships_ASIA");
            yield return Path.Combine(drive, "World_of_Warships");
            yield return Path.Combine(drive, "SteamLibrary", "steamapps", "common", "World of Warships");
        }

        foreach (var lib in SteamLibraries())
            yield return Path.Combine(lib, "steamapps", "common", "World of Warships");
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>
    /// Wargaming Game Center keeps one small file per installed game under
    /// %ProgramData%\Wargaming.net\GameCenter\apps\wows.*\; its content is the install folder.
    /// </summary>
    private static IEnumerable<string> GameCenterEntries()
    {
        var results = new List<string>();
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Wargaming.net", "GameCenter", "apps");
        try
        {
            if (!Directory.Exists(apps)) return results;
            foreach (var dir in Directory.EnumerateDirectories(apps, "wows.*"))
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                if (info.Length is 0 or > 1024) continue;
                var text = File.ReadAllText(file).Trim();
                if (text.Length > 2 && Path.IsPathFullyQualified(text)) results.Add(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return results;
    }

    private static List<string>? _uninstallCache;

    /// <summary>Forget cached registry results (after the user changed something).</summary>
    public static void InvalidateCache() => _uninstallCache = null;

    /// <summary>Install folders from the uninstall registry. Opening every uninstall key is slow, so it runs once per session.</summary>
    private static IEnumerable<string> UninstallEntries() => _uninstallCache ??= ReadUninstallEntries();

    private static List<string> ReadUninstallEntries()
    {
        var results = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyPath in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(sub);
                    var name = entry?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("World of Warships", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry!.GetValue("InstallLocation") is string loc && loc.Length > 0) results.Add(loc);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return results;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var results = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steam) return results;
            steam = steam.Replace('/', '\\');
            results.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                    results.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return results;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();

    /// <summary>
    /// Places the client may write tempArenaInfo.json: replays\, replays\&lt;version&gt;\ and the game root.
    /// Returns existing files, newest first.
    /// </summary>
    public static IEnumerable<FileInfo> FindArenaFiles(string root)
    {
        var files = new List<FileInfo>();
        var replays = Path.Combine(root, "replays");
        TryAdd(files, Path.Combine(replays, ArenaFileName));
        try
        {
            if (Directory.Exists(replays))
                foreach (var sub in Directory.EnumerateDirectories(replays))
                    TryAdd(files, Path.Combine(sub, ArenaFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        TryAdd(files, Path.Combine(root, ArenaFileName));
        return files.OrderByDescending(f => f.LastWriteTimeUtc);

        static void TryAdd(List<FileInfo> list, string path)
        {
            var fi = new FileInfo(path);
            if (fi.Exists) list.Add(fi);
        }
    }
}
