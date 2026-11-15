using System.Text;

namespace Foretop.Core.GameData;

/// <summary>
/// The game's own texts: <c>bin\&lt;build&gt;\res\texts\&lt;lang&gt;\LC_MESSAGES\global.mo</c> (GNU gettext MO).
/// The file has no charset header, so strings are decoded as UTF-8.
/// </summary>
public sealed class GameTexts
{
    private readonly Dictionary<string, string> _map;

    private GameTexts(Dictionary<string, string> map, string language)
    {
        _map = map;
        Language = language;
    }

    public string Language { get; }
    public int Count => _map.Count;

    public static GameTexts Empty { get; } = new(new Dictionary<string, string>(), "none");

    /// <summary>Loads the given language, falling back to English.</summary>
    public static GameTexts Load(string gameRoot, string build, string language = "en")
    {
        foreach (var lang in new[] { language, "en" }.Distinct())
        {
            var path = Path.Combine(gameRoot, "bin", build, "res", "texts", lang, "LC_MESSAGES", "global.mo");
            if (File.Exists(path)) return new GameTexts(Parse(File.ReadAllBytes(path)), lang);
        }
        return Empty;
    }

    public static Dictionary<string, string> Parse(byte[] b)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (b.Length < 28) return map;
        var magic = BitConverter.ToUInt32(b, 0);
        if (magic != 0x950412DE) throw new InvalidDataException("Not a little-endian gettext MO file");
        var n = BitConverter.ToInt32(b, 8);
        var o = BitConverter.ToInt32(b, 12);
        var t = BitConverter.ToInt32(b, 16);
        for (var i = 0; i < n; i++)
        {
            var ol = BitConverter.ToInt32(b, o + 8 * i);
            var oo = BitConverter.ToInt32(b, o + 8 * i + 4);
            var tl = BitConverter.ToInt32(b, t + 8 * i);
            var to = BitConverter.ToInt32(b, t + 8 * i + 4);
            var key = Encoding.UTF8.GetString(b, oo, ol);
            var nul = key.IndexOf('\0');
            if (nul >= 0) key = key[..nul]; // plural forms: singular is the key
            var value = Encoding.UTF8.GetString(b, to, tl);
            nul = value.IndexOf('\0');
            if (nul >= 0) value = value[..nul];
            map[key] = value;
        }
        return map;
    }

    public string? Get(string key) => _map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Replace(' ', ' ') : null;

    /// <summary>All non-empty texts whose key starts with the prefix.</summary>
    public IEnumerable<KeyValuePair<string, string>> WithPrefix(string prefix) =>
        _map.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => KeyValuePair.Create(p.Key, p.Value.Replace(' ', ' ')));

    /// <summary>IDS_ + upper-case name, the convention for most game entities.</summary>
    public string? Ids(string name) => Get("IDS_" + name.ToUpperInvariant());
}
