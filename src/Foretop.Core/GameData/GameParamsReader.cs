using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Foretop.Core.GameData;

/// <summary>
/// Loads <c>content/GameParams.data</c>: the bytes are stored reversed and zlib-compressed, and contain a Python
/// pickle of every game entity (ships, shells, upgrades, consumables…) keyed by name.
/// </summary>
public static class GameParamsReader
{
    public const string FilePath = "content/GameParams.data";

    /// <summary>Returns the name → entity map.</summary>
    public static Dictionary<object, object?> Load(GameFileSystem fs)
    {
        var raw = fs.Read(FilePath);
        Array.Reverse(raw);
        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var buffered = new BufferedStream(z, 1 << 20);
        var root = Unpickler.Load(buffered);
        return Unwrap(root) ?? throw new InvalidDataException("GameParams has an unexpected shape");
    }

    /// <summary>Modern builds: {"": {name: entity}}; older ones: a tuple/list with that dict first.</summary>
    private static Dictionary<object, object?>? Unwrap(object? root)
    {
        switch (root)
        {
            case Dictionary<object, object?> d when d.TryGetValue("", out var inner):
                return Unwrap(inner) ?? d;
            case Dictionary<object, object?> d:
                return d;
            case PyObject o when o.Attrs.TryGetValue("", out var inner):
                return Unwrap(inner);
            case IList list when list.Count > 0:
                return Unwrap(list[0]);
            case object?[] arr when arr.Length > 0:
                return Unwrap(arr[0]);
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ typed access helpers

    public static object? Get(object? node, string key) => node switch
    {
        PyObject o => o.Attrs.TryGetValue(key, out var v) ? v : null,
        Dictionary<object, object?> d => d.TryGetValue(key, out var v) ? v : null,
        _ => null,
    };

    public static object? Dig(object? node, params string[] keys)
    {
        foreach (var k in keys) node = Get(node, k);
        return node;
    }

    public static string? Str(object? node, string key) => Get(node, key) as string;

    public static double? Num(object? node, string key) => Get(node, key) switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public static IEnumerable<KeyValuePair<object, object?>> Entries(object? node) => node switch
    {
        PyObject o => o.Attrs,
        Dictionary<object, object?> d => d,
        _ => [],
    };

    public static IEnumerable<object?> Items(object? node) => node switch
    {
        List<object?> l => l,
        object?[] a => a,
        _ => [],
    };

    /// <summary>"typeinfo.type" of an entity (Ship, Projectile, Modernization, Ability, Unit…).</summary>
    public static string? TypeOf(object? entity) => Str(Get(entity, "typeinfo"), "type");

    // ------------------------------------------------------------------ inspection (development)

    /// <summary>JSON dump of a node, depth-limited, for inspecting the real data.</summary>
    public static string ToJson(object? node, int maxDepth = 6, int maxItems = 400)
    {
        var sb = new StringBuilder();
        using var w = new Utf8JsonWriter(new MemoryStream());
        var ms = new MemoryStream();
        using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            Write(jw, node, 0, maxDepth, maxItems, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(Utf8JsonWriter w, object? node, int depth, int maxDepth, int maxItems, HashSet<object> seen)
    {
        switch (node)
        {
            case null: w.WriteNullValue(); return;
            case string s: w.WriteStringValue(s); return;
            case bool b: w.WriteBooleanValue(b); return;
            case long l: w.WriteNumberValue(l); return;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); return;
            case double d: w.WriteStringValue(d.ToString(CultureInfo.InvariantCulture)); return;
        }
        if (depth >= maxDepth || !seen.Add(node))
        {
            w.WriteStringValue(depth >= maxDepth ? $"<{node.GetType().Name} …>" : "<cycle>");
            return;
        }
        switch (node)
        {
            case PyObject o:
                w.WriteStartObject();
                w.WriteString("__class__", o.Type.ToString());
                foreach (var (k, v) in o.Attrs.Take(maxItems)) { w.WritePropertyName(k.ToString() ?? "null"); Write(w, v, depth + 1, maxDepth, maxItems, seen); }
                w.WriteEndObject();
                break;
            case Dictionary<object, object?> d:
                w.WriteStartObject();
                foreach (var (k, v) in d.Take(maxItems)) { w.WritePropertyName(k.ToString() ?? "null"); Write(w, v, depth + 1, maxDepth, maxItems, seen); }
                if (d.Count > maxItems) w.WriteString("…", $"{d.Count - maxItems} more");
                w.WriteEndObject();
                break;
            case IEnumerable e:
                w.WriteStartArray();
                var n = 0;
                foreach (var v in e) { if (n++ >= maxItems) break; Write(w, v, depth + 1, maxDepth, maxItems, seen); }
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(node.ToString());
                break;
        }
        seen.Remove(node);
    }
}
