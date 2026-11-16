using System.IO.Compression;
using System.Text;

namespace Foretop.Core.GameData;

/// <summary>One file inside the client's packages.</summary>
public sealed record PackedFile(string Path, string Package, long Offset, uint PackedSize, uint UnpackedSize, ulong Compression, uint Crc32);

/// <summary>
/// Read-only view of the game's virtual file system: every <c>bin\&lt;build&gt;\idx\*.idx</c> index, pointing into
/// <c>res_packages\*.pkg</c>. Format (little-endian, "ISFP" v0x40) as implemented by landaire/wows-toolkit's wowsunpack;
/// see docs/ARCHITECTURE.md. Nothing is ever written to the game folder.
/// </summary>
public sealed class GameFileSystem
{
    private const ulong RootParent = 0xDBB1A1D1B108B927;
    private readonly Dictionary<string, PackedFile> _files;

    private GameFileSystem(string root, string build, string newestOnDisk, Dictionary<string, PackedFile> files)
    {
        Root = root;
        Build = build;
        NewestOnDisk = newestOnDisk;
        _files = files;
    }

    /// <summary>The newest build folder when this was opened; differs from <see cref="Build"/> while it could not be read.</summary>
    public string NewestOnDisk { get; }

    /// <summary>True while an older build is used because the newest one is incomplete (the game is updating).</summary>
    public bool IsFallback => Build != NewestOnDisk;

    public DateTime OpenedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>Should a cached instance be opened again: the game was moved or updated, or a fallback is due a retry.</summary>
    public bool IsOutdated(string root) =>
        Root != root || NewestBuild(root) != NewestOnDisk || (IsFallback && DateTime.UtcNow - OpenedAtUtc > TimeSpan.FromMinutes(1));

    public string Root { get; }

    /// <summary>The client build number (the newest folder under bin\).</summary>
    public string Build { get; }

    public int Count => _files.Count;

    public IEnumerable<PackedFile> Files => _files.Values;

    public static string? NewestBuild(string gameRoot)
    {
        var bin = Path.Combine(gameRoot, "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateDirectories(bin)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.All(char.IsDigit) && Directory.Exists(Path.Combine(bin, n, "idx")))
            .OrderByDescending(n => long.Parse(n!))
            .FirstOrDefault();
    }

    /// <summary>Loads every index of the newest build. Throws <see cref="InvalidDataException"/> for unknown formats.</summary>
    /// <remarks>
    /// While the game updates, the newest build folder exists before its index files are complete. Then the newest
    /// build that reads cleanly is used (the previous one) and <see cref="IsFallback"/> is set; the first error is
    /// thrown only when no build can be read.
    /// </remarks>
    public static GameFileSystem Open(string gameRoot)
    {
        var builds = Builds(gameRoot);
        if (builds.Count == 0) throw new DirectoryNotFoundException($"No bin\\<build>\\idx folder under {gameRoot}");
        Exception? first = null;
        foreach (var build in builds)
        {
            try
            {
                return OpenBuild(gameRoot, build, builds[0]);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                first ??= ex;
            }
        }
        throw first!;
    }

    /// <summary>Loads the indexes of one particular build (an older replay needs the definitions it was recorded with).
    /// Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> when that build cannot be read.</summary>
    public static GameFileSystem OpenBuild(string gameRoot, string build, string? newestOnDisk = null)
    {
        var idxDir = Path.Combine(gameRoot, "bin", build, "idx");
        if (!Directory.Exists(idxDir)) throw new DirectoryNotFoundException($"Build {build} is not installed.");
        var files = new Dictionary<string, PackedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in Directory.EnumerateFiles(idxDir, "*.idx"))
            ReadIndex(idx, files);
        if (files.Count == 0) throw new InvalidDataException($"Build {build} has no readable index yet.");
        return new GameFileSystem(gameRoot, build, newestOnDisk ?? NewestBuild(gameRoot) ?? build, files);
    }

    /// <summary>Build folders with an idx folder, newest first.</summary>
    public static List<string> Builds(string gameRoot)
    {
        var bin = Path.Combine(gameRoot, "bin");
        if (!Directory.Exists(bin)) return [];
        return Directory.EnumerateDirectories(bin)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.All(char.IsDigit) && Directory.Exists(Path.Combine(bin, n, "idx")))
            .OrderByDescending(n => long.Parse(n!))
            .Select(n => n!)
            .ToList();
    }

    public bool Exists(string path) => _files.ContainsKey(Normalize(path));

    public PackedFile? Find(string path) => _files.TryGetValue(Normalize(path), out var f) ? f : null;

    public IEnumerable<PackedFile> Under(string directory)
    {
        var prefix = Normalize(directory).TrimEnd('/') + "/";
        return _files.Values.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads and decompresses one file.</summary>
    public byte[] Read(string path)
    {
        var f = Find(path) ?? throw new FileNotFoundException("Not in the game packages: " + path);
        return Read(f);
    }

    public byte[] Read(PackedFile f)
    {
        var pkg = Path.Combine(Root, "res_packages", f.Package);
        using var fs = new FileStream(pkg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        fs.Position = f.Offset;
        var packed = new byte[f.PackedSize];
        fs.ReadExactly(packed);
        if (f.Compression == 0) return packed;

        var output = new byte[f.UnpackedSize];
        using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        inflate.ReadExactly(output);
        return output;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    // ------------------------------------------------------------------ idx parsing

    private static void ReadIndex(string idxPath, Dictionary<string, PackedFile> files)
    {
        var data = File.ReadAllBytes(idxPath);
        var span = data.AsSpan();
        if (data.Length < 0x38 || U32(span, 0) != 0x50465349)
            throw new InvalidDataException($"{Path.GetFileName(idxPath)}: not an ISFP index");
        var version = U32(span, 4);
        if (version != 0x02000000)
            throw new InvalidDataException($"{Path.GetFileName(idxPath)}: unsupported index version 0x{version:X8}");

        var resourceCount = (int)U32(span, 0x10);
        var fileInfoCount = (int)U32(span, 0x14);
        var volumeCount = (int)U32(span, 0x18);
        var resourcesAt = 0x10 + (int)U64(span, 0x20);
        var fileInfosAt = 0x10 + (int)U64(span, 0x28);
        var volumesAt = 0x10 + (int)U64(span, 0x30);

        var nodes = new Dictionary<ulong, (string Name, ulong Parent)>(resourceCount);
        for (var i = 0; i < resourceCount; i++)
        {
            var rec = resourcesAt + i * 32;
            var namePtr = rec + (int)U64(span, rec + 8);
            nodes[U64(span, rec + 16)] = (CString(span, namePtr), U64(span, rec + 24));
        }

        var volumes = new Dictionary<ulong, string>(volumeCount);
        for (var i = 0; i < volumeCount; i++)
        {
            var rec = volumesAt + i * 24;
            var name = CString(span, rec + (int)U64(span, rec + 8));
            if (name.StartsWith("//.//", StringComparison.Ordinal)) name = name[5..];
            volumes[U64(span, rec + 16)] = name;
        }

        var paths = new Dictionary<ulong, string>();
        string PathOf(ulong id)
        {
            if (paths.TryGetValue(id, out var p)) return p;
            if (!nodes.TryGetValue(id, out var n)) return "";
            p = n.Parent == RootParent || !nodes.ContainsKey(n.Parent) ? n.Name : PathOf(n.Parent) + "/" + n.Name;
            paths[id] = p;
            return p;
        }

        for (var i = 0; i < fileInfoCount; i++)
        {
            var rec = fileInfosAt + i * 48;
            var resourceId = U64(span, rec);
            var volumeId = U64(span, rec + 8);
            var path = PathOf(resourceId);
            if (path.Length == 0 || !volumes.TryGetValue(volumeId, out var pkg)) continue;
            var file = new PackedFile(path, pkg, (long)U64(span, rec + 16), U32(span, rec + 32), U32(span, rec + 40),
                U64(span, rec + 24), U32(span, rec + 36));
            files.TryAdd(path, file); // first copy wins; duplicates across indexes are identical in practice
        }
    }

    // Bounds-checked reads: an index cut short (the game updater still writing it) is InvalidDataException.
    private static uint U32(ReadOnlySpan<byte> s, int at) =>
        at >= 0 && at + 4 <= s.Length ? BitConverter.ToUInt32(s.Slice(at, 4)) : throw new InvalidDataException("Game index file is cut short.");

    private static ulong U64(ReadOnlySpan<byte> s, int at) =>
        at >= 0 && at + 8 <= s.Length ? BitConverter.ToUInt64(s.Slice(at, 8)) : throw new InvalidDataException("Game index file is cut short.");

    private static string CString(ReadOnlySpan<byte> s, int at)
    {
        if (at < 0 || at >= s.Length) throw new InvalidDataException("Game index file is cut short.");
        var end = s[at..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(s.Slice(at, end < 0 ? s.Length - at : end));
    }
}
