using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Foretop.Core.GameData;

namespace Foretop.Core.Replays;

/// <summary>One replay file, from its header only.</summary>
public sealed record ReplayEntry(string Path, DateTime When, string MapDisplayName, string PlayerVehicle, string MatchGroup, string PlayerName, long SizeBytes);

/// <summary>
/// The minimap of a space: water and land images (760 px, PNG) and the playable area they cover exactly
/// (<see cref="MapBounds"/>, world X east, Z north). Space is the map's folder, e.g. "spaces/28_naval_mission".
/// </summary>
public sealed record MapAssets(byte[]? Water, byte[]? Land, MapBounds Bounds, string Space = "");

/// <summary>Lists replays in the game's <c>replays</c> folder and opens them with the client's entity definitions.</summary>
public sealed class ReplayLibrary(Func<string?> gameRoot, ShipDataStore shipData)
{
    private readonly Dictionary<string, MapAssets> _maps = new(StringComparer.OrdinalIgnoreCase);
    // Entity definitions per client build: the installed one plus older builds still on disk for older replays.
    private readonly Dictionary<string, EntityDefs> _defs = new(StringComparer.Ordinal);

    public string? Folder => gameRoot() is { } root ? System.IO.Path.Combine(root, "replays") : null;

    public IReadOnlyList<ReplayEntry> List()
    {
        var folder = Folder;
        if (folder is null || !Directory.Exists(folder)) return [];
        var list = new List<ReplayEntry>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.wowsreplay", SearchOption.AllDirectories))
        {
            if (System.IO.Path.GetFileName(path).Equals("temp.wowsreplay", StringComparison.OrdinalIgnoreCase)) continue; // still recording
            if (ReadEntry(path) is { } e) list.Add(e);
        }
        return list.OrderByDescending(e => e.When).ToList();
    }

    /// <summary>Header block 0 only (a few KB), so listing hundreds of replays stays fast.</summary>
    public static ReplayEntry? ReadEntry(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[12];
            if (f.Read(head, 0, 12) < 12 || BitConverter.ToUInt32(head, 0) != ReplayFile.Magic) return null;
            var len = BitConverter.ToInt32(head, 8);
            if (len is <= 0 or > 4 << 20) return null;
            var json = new byte[len];
            f.ReadExactly(json);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var when = DateTime.TryParseExact(S("dateTime"), "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt : File.GetLastWriteTime(path);
            return new ReplayEntry(path, when, S("mapDisplayName"), S("playerVehicle"), S("matchGroup"), S("playerName"), f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Decodes a replay and loads its map. Throws <see cref="InvalidDataException"/> with a readable message.</summary>
    public async Task<(ReplayData Data, MapAssets Map)> OpenAsync(string path, CancellationToken ct = default)
    {
        try { return await OpenCoreAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsDecodingFailure(ex)) { throw new InvalidDataException("The replay could not be decoded: " + ex.Message, ex); }
    }

    /// <summary>Failures of a damaged, unsupported or foreign-version file (not I/O, not cancellation).</summary>
    private static bool IsDecodingFailure(Exception ex) =>
        ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or OutOfMemoryException);

    private async Task<(ReplayData Data, MapAssets Map)> OpenCoreAsync(string path, CancellationToken ct)
    {
        var file = await Task.Run(() => ReplayFile.Read(path), ct).ConfigureAwait(false);
        var result = await shipData.WithFileSystemAsync(fs =>
        {
            var (defs, note) = DefsFor(fs, file);
            var data = ReplayParser.Parse(file, defs, note);
            return (data, Map(fs, data.MapSpace));
        }, ct).ConfigureAwait(false);
        if (result.data is null) throw new InvalidDataException("Replays need the game installation (for its map images and message definitions).");
        return (result.data, result.Item2);
    }

    /// <summary>Decodes a replay without loading map images (personal battle facts). Null without the game installation.</summary>
    public async Task<ReplayData?> ParseAsync(string path, CancellationToken ct = default)
    {
        try { return await ParseCoreAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsDecodingFailure(ex)) { throw new InvalidDataException("The replay could not be decoded: " + ex.Message, ex); }
    }

    private async Task<ReplayData?> ParseCoreAsync(string path, CancellationToken ct)
    {
        var file = await Task.Run(() => ReplayFile.Read(path), ct).ConfigureAwait(false);
        return await shipData.WithFileSystemAsync(fs =>
        {
            var (defs, note) = DefsFor(fs, file);
            return ReplayParser.Parse(file, defs, note);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The entity definitions of the build the replay was recorded with. Method and property numbers change between
    /// game versions, so an older replay read with the current definitions loses its player list and events. The game
    /// keeps the previous build folder after an update; its definitions are read once (the index is not kept). When the
    /// replay's build is gone, the current definitions are used and the note says so.
    /// </summary>
    private (EntityDefs Defs, string? Note) DefsFor(GameFileSystem fs, ReplayFile file)
    {
        EntityDefs Cached(string build, Func<EntityDefs> load)
        {
            if (_defs.TryGetValue(build, out var d)) return d;
            d = load();
            if (_defs.Count >= 4) _defs.Remove(_defs.Keys.First(k => k != fs.Build));
            _defs[build] = d;
            return d;
        }
        var current = Cached(fs.Build, () => EntityDefs.Load(fs));
        var build = file.ClientBuild;
        if (build is null || build == fs.Build) return (current, null);
        if (_defs.TryGetValue(build, out var known)) return (known, null);
        if (GameFileSystem.Builds(fs.Root).Contains(build))
        {
            try { return (Cached(build, () => EntityDefs.Load(GameFileSystem.OpenBuild(fs.Root, build))), null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return (current, $"This replay was recorded with another game version (build {build}); its files are no longer installed, "
            + $"so it is read with the current version's (build {fs.Build}). Player names, events or positions may be missing.");
    }

    private MapAssets Map(GameFileSystem fs, string space)
    {
        space = space.Replace('\\', '/').Trim('/');
        if (!space.StartsWith("spaces/", StringComparison.OrdinalIgnoreCase)) space = "spaces/" + space;
        var key = fs.Build + "|" + space;
        if (_maps.TryGetValue(key, out var cached)) return cached;
        // Keep the images of a few maps only: each holds two 760 px PNGs, and a long session opens many replays.
        if (_maps.Count >= 6) _maps.Remove(_maps.Keys.First());
        byte[]? Read(string name) => fs.Find($"{space}/{name}") is { } f ? fs.Read(f) : null;
        var settings = Read("space.settings");
        var bounds = MapBounds.FromSpaceSettings(settings is null ? null : Encoding.UTF8.GetString(settings));
        var map = new MapAssets(Read("minimap_water.png"), Read("minimap.png"), bounds, space);
        _maps[key] = map;
        return map;
    }
}
