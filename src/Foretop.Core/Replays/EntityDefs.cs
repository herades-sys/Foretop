using System.Xml.Linq;
using Foretop.Core.GameData;

namespace Foretop.Core.Replays;

/// <summary>
/// The game's BigWorld entity definitions (<c>scripts/entity_defs</c>), read from the client. Replays address entity
/// methods and properties by index; the index is the position in the definition list after a stable sort by the
/// argument's encoded size (variable-length last). Reading the definitions instead of hard-coding numbers keeps the
/// decoder working when a patch adds a method.
/// </summary>
public sealed class EntityDefs
{
    /// <summary>Size used for anything without a fixed encoding (strings, blobs, open arrays, nullable dicts).</summary>
    public const int Variable = 0xFFFF;

    private readonly Dictionary<string, XElement> _aliases;
    private readonly Dictionary<string, EntityDef> _entities = new(StringComparer.Ordinal);

    /// <summary>Entity type names by the 1-based type id used in entity-create packets.</summary>
    public IReadOnlyList<string> TypeNames { get; }

    private EntityDefs(Func<string, string?> read)
    {
        _aliases = XDocument.Parse(read("scripts/entity_defs/alias.xml") ?? "<root/>").Root!.Elements()
            .GroupBy(e => e.Name.LocalName).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var entities = XDocument.Parse(read("scripts/entities.xml") ?? "<root/>").Root!;
        TypeNames = (entities.Element("ClientServerEntities")?.Elements() ?? []).Select(e => e.Name.LocalName).ToList();
        foreach (var name in TypeNames)
        {
            var def = Load(name, read, isInterface: false);
            if (def is not null) _entities[name] = def;
        }
    }

    public static EntityDefs Load(GameFileSystem fs) => new(path =>
    {
        var f = fs.Find(path);
        return f is null ? null : System.Text.Encoding.UTF8.GetString(fs.Read(f));
    });

    /// <summary>For tests: definitions from a dictionary of file contents.</summary>
    public static EntityDefs FromFiles(IReadOnlyDictionary<string, string> files) => new(p => files.GetValueOrDefault(p));

    public EntityDef? Entity(string name) => _entities.GetValueOrDefault(name);

    public string? TypeName(int typeId) => typeId >= 1 && typeId <= TypeNames.Count ? TypeNames[typeId - 1] : null;

    // ------------------------------------------------------------------ parsing

    private EntityDef? Load(string name, Func<string, string?> read, bool isInterface)
    {
        var text = read(isInterface ? $"scripts/entity_defs/interfaces/{name}.def" : $"scripts/entity_defs/{name}.def");
        if (text is null) return null;
        var root = XDocument.Parse(text).Root!;
        var props = new List<PropertyDef>();
        var methods = new List<MethodDef>();
        Collect(root, read, props, methods);
        if (isInterface) return new EntityDef(name, props, methods);
        return new EntityDef(name,
            props.Where(p => p.IsClient).OrderBy(p => p.Size).ToList(), // OrderBy is stable
            methods.OrderBy(m => m.Size).ToList());
    }

    /// <summary>
    /// Parent and interfaces (in the listed order) first, then the entity's own definitions. Verified against a
    /// build 13533378 replay: Vehicle.health = 28, Avatar.onArenaStateReceived = 154 only come out in this order.
    /// </summary>
    private void Collect(XElement root, Func<string, string?> read, List<PropertyDef> props, List<MethodDef> methods)
    {
        CollectInherited(root, read, props, methods);
        foreach (var p in root.Element("Properties")?.Elements() ?? [])
        {
            var type = p.Element("Type");
            if (type is null) continue;
            var flags = (p.Element("Flags")?.Value ?? "").Trim();
            props.Add(new PropertyDef(p.Name.LocalName, type, flags, SizeOf(type)));
        }
        foreach (var m in root.Element("ClientMethods")?.Elements() ?? [])
        {
            var args = Args(m);
            var size = args.Aggregate(0, (s, a) => Math.Min(Variable, s + a.Size));
            methods.Add(new MethodDef(m.Name.LocalName, args, size));
        }
    }

    private void CollectInherited(XElement root, Func<string, string?> read, List<PropertyDef> props, List<MethodDef> methods)
    {
        var parent = root.Element("Parent")?.Value.Trim();
        if (!string.IsNullOrEmpty(parent) && read($"scripts/entity_defs/{parent}.def") is { } pt)
            Collect(XDocument.Parse(pt).Root!, read, props, methods);
        foreach (var i in root.Element("Implements")?.Elements("Interface") ?? [])
        {
            var it = read($"scripts/entity_defs/interfaces/{i.Value.Trim()}.def");
            if (it is not null) Collect(XDocument.Parse(it).Root!, read, props, methods);
        }
    }

    private List<ArgDef> Args(XElement method)
    {
        var list = new List<ArgDef>();
        foreach (var a in method.Elements("Arg")) list.Add(new ArgDef("", a, SizeOf(a)));
        foreach (var a in method.Element("Args")?.Elements() ?? []) list.Add(new ArgDef(a.Name.LocalName, a, SizeOf(a)));
        return list;
    }

    /// <summary>Head word of a type element ("INT32", "ARRAY", "FIXED_DICT", an alias name).</summary>
    public static string Head(XElement type) =>
        string.Concat(type.Nodes().OfType<XText>().Select(t => t.Value)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public XElement Resolve(XElement type)
    {
        for (var guard = 0; guard < 16; guard++)
        {
            var head = Head(type);
            if (!_aliases.TryGetValue(head, out var alias)) return type;
            type = alias;
        }
        return type;
    }

    public int SizeOf(XElement type)
    {
        type = Resolve(type);
        switch (Head(type))
        {
            case "INT8" or "UINT8" or "BOOL": return 1;
            case "INT16" or "UINT16": return 2;
            case "INT32" or "UINT32" or "FLOAT32" or "FLOAT": return 4;
            case "INT64" or "UINT64" or "FLOAT64": return 8;
            case "VECTOR2": return 8;
            case "VECTOR3": return 12;
            case "VECTOR4": return 16;
            case "ARRAY" or "TUPLE":
            {
                var of = type.Element("of");
                if (of is null || !int.TryParse(type.Element("size")?.Value.Trim(), out var n)) return Variable;
                var s = SizeOf(of);
                return s >= Variable ? Variable : Math.Min(Variable, s * n);
            }
            case "FIXED_DICT":
            {
                if (string.Equals(type.Element("AllowNone")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return Variable;
                var total = 0;
                foreach (var f in type.Element("Properties")?.Elements() ?? [])
                {
                    var t = f.Element("Type");
                    total = Math.Min(Variable, total + (t is null ? Variable : SizeOf(t)));
                }
                return total;
            }
            default: return Variable; // STRING, UNICODE_STRING, BLOB, PYTHON, MAILBOX, USER_TYPE
        }
    }
}

public sealed record ArgDef(string Name, XElement Type, int Size);

public sealed record MethodDef(string Name, IReadOnlyList<ArgDef> Args, int Size);

public sealed record PropertyDef(string Name, XElement Type, string Flags, int Size)
{
    /// <summary>Properties the server sends to some client; only these are indexed in replays.</summary>
    public bool IsClient => Flags is "ALL_CLIENTS" or "OTHER_CLIENTS" or "OWN_CLIENT" or "CELL_PUBLIC_AND_OWN" or "BASE_AND_CLIENT";
}

public sealed record EntityDef(string Name, IReadOnlyList<PropertyDef> Properties, IReadOnlyList<MethodDef> ClientMethods)
{
    public int MethodIndex(string name)
    {
        for (var i = 0; i < ClientMethods.Count; i++) if (ClientMethods[i].Name == name) return i;
        return -1;
    }

    public int PropertyIndex(string name)
    {
        for (var i = 0; i < Properties.Count; i++) if (Properties[i].Name == name) return i;
        return -1;
    }
}
