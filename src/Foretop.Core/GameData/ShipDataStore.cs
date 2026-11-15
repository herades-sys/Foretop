using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foretop.Core.GameData;

public enum ShipDataState
{
    /// <summary>Nothing checked yet.</summary>
    Idle,

    /// <summary>No game installation found.</summary>
    NoGame,

    /// <summary>Reading the game files (first time, or after a game update while older data stays in use).</summary>
    Reading,

    Ready,
    Failed,
}

/// <summary>
/// Ship Intelligence data: a compact per-build extract of the game client, cached in
/// <c>%LOCALAPPDATA%\Foretop\shipdata\&lt;build&gt;.json.br</c>. Reading the client (≈1 GB peak) is done by a
/// separate process supplied by the caller, so the app itself never holds the raw game data.
/// </summary>
public sealed class ShipDataStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly Func<string?> _gameRoot;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ShipData>? _byName;

    public ShipDataStore(string dataDir, Func<string?> gameRoot, ILogger<ShipDataStore>? log = null)
    {
        _dir = Path.Combine(dataDir, "shipdata");
        _gameRoot = gameRoot;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public ShipDataSet? Data { get; private set; }
    public ShipDataState State { get; private set; } = ShipDataState.Idle;
    public string? Message { get; private set; }
    public (int Done, int Total) Progress { get; private set; }

    /// <summary>The newest build in the game folder, when known.</summary>
    public string? GameBuild { get; private set; }

    /// <summary>True while data from an older build is shown and the new build is being read.</summary>
    public bool ShowingOlderBuild => Data is not null && GameBuild is not null && Data.Build != GameBuild;

    public event Action? Changed;

    public ShipData? Find(string name) => _byName is not null && _byName.TryGetValue(name, out var s) ? s : null;

    /// <summary>A ship by display name (case-insensitive), preferring obtainable, higher-tier ones.</summary>
    public ShipData? FindByDisplayName(string text) => Data?.Ships
        .Where(s => string.Equals(s.DisplayName, text.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .OrderByDescending(s => s.Obtainable).ThenByDescending(s => s.Tier).FirstOrDefault();

    public static string CacheFile(string dir, string build) => Path.Combine(dir, build + ".json.br");

    /// <summary>
    /// Loads cached data for the installed build, or runs <paramref name="extract"/> (gameRoot, outFile, progress, ct)
    /// to create it. Data from an older build stays available while the new one is read.
    /// </summary>
    public async Task EnsureAsync(Func<string, string, IProgress<(int, int)>, CancellationToken, Task> extract, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return; // already running
        try
        {
            var root = _gameRoot();
            var build = root is null ? null : GameFileSystem.NewestBuild(root);
            GameBuild = build;
            if (root is null || build is null)
            {
                Data ??= LoadNewestCache();
                Set(ShipDataState.NoGame, "No World of Warships installation found.");
                return;
            }
            if (Data?.Build == build && State == ShipDataState.Ready) return;

            var file = CacheFile(_dir, build);
            if (File.Exists(file) && TryLoad(file) is { } cached)
            {
                Use(cached);
                Set(ShipDataState.Ready, null);
                return;
            }

            Data ??= LoadNewestCache(); // older build, shown meanwhile
            if (Data is not null) Index(Data);
            Directory.CreateDirectory(_dir);
            Progress = (0, 0);
            Set(ShipDataState.Reading, "Reading ship data from the game client");
            var progress = new Progress<(int, int)>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            await extract(root, file, progress, ct).ConfigureAwait(false);
            if (TryLoad(file) is not { } fresh) throw new InvalidDataException("The extracted ship data could not be read.");
            Use(fresh);
            Set(ShipDataState.Ready, null);
            _log.LogInformation("Ship data ready: {Count} ships, build {Build}", fresh.Ships.Count, fresh.Build);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reading ship data failed");
            Set(ShipDataState.Failed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Use(ShipDataSet set)
    {
        Data = set;
        Index(set);
        if (set.Maps.Count > 0) Game.BattleTypes.MapNames = set.Maps;
    }

    private void Index(ShipDataSet set)
    {
        _byName = set.Ships.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byId = set.Ships.Where(s => s.Id != 0).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
    }

    private Dictionary<long, ShipData>? _byId;

    /// <summary>A ship by its id (GameParams id = Wargaming API ship_id = arena info shipId).</summary>
    public ShipData? FindById(long id) => _byId is not null && _byId.TryGetValue(id, out var s) ? s : null;

    private void Set(ShipDataState state, string? message)
    {
        State = state;
        Message = message;
        Changed?.Invoke();
    }

    private ShipDataSet? LoadNewestCache()
    {
        if (!Directory.Exists(_dir)) return null;
        return Directory.EnumerateFiles(_dir, "*.json.br")
            .OrderByDescending(f => long.TryParse(Path.GetFileName(f).Split('.')[0], out var b) ? b : 0)
            .Select(TryLoad).FirstOrDefault(d => d is not null);
    }

    public static void Save(ShipDataSet set, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var br = new BrotliStream(fs, CompressionLevel.Optimal))
            JsonSerializer.Serialize(br, set, Json);
        File.Move(tmp, file, overwrite: true);
    }

    public ShipDataSet? TryLoad(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            using var br = new BrotliStream(fs, CompressionMode.Decompress);
            var set = JsonSerializer.Deserialize<ShipDataSet>(br, Json);
            return set is not null && set.FormatVersion == ShipExtractor.FormatVersion ? set : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _log.LogWarning("Ship data cache {File} unreadable: {Error}", file, ex.Message);
            return null;
        }
    }

    /// <summary>The whole extraction, for the helper process: game files → extract → cache file.</summary>
    public static ShipDataSet ExtractToFile(string gameRoot, string file, IProgress<(int Done, int Total)>? progress, Action<string>? warn = null)
    {
        var fs = GameFileSystem.Open(gameRoot);
        var gp = GameParamsReader.Load(fs);
        var texts = GameTexts.Load(gameRoot, fs.Build);
        var set = ShipExtractor.Extract(fs.Build, gp, texts, progress, warn);
        gp.Clear();
        ExportImages(fs, set, ImageDir(Path.GetDirectoryName(file)!, set.Build), warn);
        Save(set, file);
        return set;
    }

    /// <summary>Folder with the ship artwork exported from the game GUI: preview\&lt;index&gt;.png and icon\&lt;index&gt;.png.</summary>
    public static string ImageDir(string shipDataDir, string build) => Path.Combine(shipDataDir, build + "-images");

    public string? PreviewPath(ShipData ship) => Data is null ? null : Existing(Path.Combine(ImageDir(_dir, Data.Build), "preview", ship.Index + ".png"));

    // ------------------------------------------------------------------ armour geometry (read on demand)

    private readonly SemaphoreSlim _fsGate = new(1, 1);
    private GameFileSystem? _fs;
    private readonly Dictionary<string, ArmourMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The hull armour model of a ship, read from the client when first asked for (about 0.5 s for the file index,
    /// then well under 100 ms per ship). Null when there is no game installation or no armour model for the hull.
    /// </summary>
    public async Task<ArmourMesh?> LoadArmourAsync(ShipData ship, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ship.HullModel)) return null;
        await _fsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_meshes.TryGetValue(ship.HullModel, out var cached)) return cached;
            return await Task.Run(() =>
            {
                var root = _gameRoot();
                if (root is null) return null;
                if (_fs is null || _fs.Root != root || _fs.Build != GameFileSystem.NewestBuild(root))
                {
                    _fs = GameFileSystem.Open(root); // first use, or the game was updated / moved
                    _meshes.Clear();
                }
                ArmourMesh? mesh = null;
                try
                {
                    mesh = ArmourGeometry.LoadHull(_fs, ship.HullModel);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    _log.LogWarning(ex, "Armour geometry for {Ship} could not be read", ship.Name);
                }
                if (_meshes.Count > 24) _meshes.Clear();
                _meshes[ship.HullModel] = mesh;
                return mesh;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _fsGate.Release();
        }
    }

    /// <summary>Runs <paramref name="work"/> on the game's file index (opened once, reopened after a game update).</summary>
    public async Task<T?> WithFileSystemAsync<T>(Func<GameFileSystem, T> work, CancellationToken ct = default)
    {
        await _fsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var root = _gameRoot();
                if (root is null || GameFileSystem.NewestBuild(root) is null) return default;
                if (_fs is null || _fs.Root != root || _fs.Build != GameFileSystem.NewestBuild(root))
                {
                    _fs = GameFileSystem.Open(root);
                    _meshes.Clear();
                }
                return work(_fs);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _fsGate.Release();
        }
    }

    public string? IconPath(ShipData ship) => Data is null ? null : Existing(Path.Combine(ImageDir(_dir, Data.Build), "icon", ship.Index + ".png"));

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static void ExportImages(GameFileSystem fs, ShipDataSet set, string dir, Action<string>? warn)
    {
        foreach (var (kind, source) in new[] { ("preview", "gui/ship_previews/"), ("icon", "gui/ship_icons/") })
        {
            Directory.CreateDirectory(Path.Combine(dir, kind));
            foreach (var index in set.Ships.Select(s => s.Index).Distinct())
            {
                var f = fs.Find(source + index + ".png");
                if (f is null) continue;
                try { File.WriteAllBytes(Path.Combine(dir, kind, index + ".png"), fs.Read(f)); }
                catch (IOException ex) { warn?.Invoke($"{index} {kind}: {ex.Message}"); }
            }
        }
    }
}
