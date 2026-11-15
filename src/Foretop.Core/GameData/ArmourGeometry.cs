using System.Buffers.Binary;
using System.Numerics;

namespace Foretop.Core.GameData;

/// <summary>One armour triangle. <see cref="Key"/> = (layer &lt;&lt; 16) | material id, the same key as the GameParams armour dicts.</summary>
public readonly record struct ArmourTriangle(int Key, Vector3 A, Vector3 B, Vector3 C)
{
    public int Layer => Key >> 16;
    public int Material => Key & 0xFFFF;
}

/// <summary>
/// A ship's hull armour model in metres. Axes: X = length (bow positive), Y = height, Z = beam (starboard positive).
/// </summary>
public sealed record ArmourMesh(IReadOnlyList<ArmourTriangle> Triangles, Vector3 Min, Vector3 Max, IReadOnlyList<string> Sources)
{
    public float Length => Max.X - Min.X;
    public float Beam => Max.Z - Min.Z;
    public float Height => Max.Y - Min.Y;
}

/// <summary>
/// Reads the armour models in the client's <c>.geometry</c> files (MergedGeometryPrototype). Layout as documented by
/// wows-toolkit (geometry.rs, parse_geometry / parse_blob_prototype / parse_armor_data); see docs/GAME_DATA.md.
/// </summary>
public static class ArmourGeometry
{
    /// <summary>One model unit is 15 m (Yamato's hull spans 17.53 units for 263 m).</summary>
    public const float MetresPerUnit = 15f;

    /// <summary>Armour triangles in one .geometry file, in model units (left-handed: X beam, Y up, Z length).</summary>
    public static List<ArmourTriangle> Parse(ReadOnlySpan<byte> file)
    {
        var result = new List<ArmourTriangle>();
        if (file.Length < 0x48) return result;
        var armorCount = BinaryPrimitives.ReadUInt32LittleEndian(file[0x14..]);
        var armorPtr = BinaryPrimitives.ReadInt64LittleEndian(file[0x40..]);
        if (armorCount == 0 || armorCount > 64 || armorPtr <= 0) return result;

        for (var i = 0; i < armorCount; i++)
        {
            var proto = armorPtr + i * 0x20L;
            if (proto + 0x20 > file.Length) break;
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(file[(int)proto..]);
            var dataPtr = BinaryPrimitives.ReadInt64LittleEndian(file[(int)(proto + 0x18)..]);
            var start = proto + dataPtr;
            if (start < 0 || size < 32 || start + size > file.Length) continue;
            ParseArmourBlob(file.Slice((int)start, (int)size), result);
        }
        return result;
    }

    private static void ParseArmourBlob(ReadOnlySpan<byte> blob, List<ArmourTriangle> into)
    {
        var off = 32; // two global header entries (bounding box, node count)
        while (off + 32 <= blob.Length)
        {
            var key = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[off..]);
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[(off + 16 + 12)..]);
            off += 32;
            if (count < 0 || count % 3 != 0 || off + count * 16L > blob.Length) break;
            for (var v = 0; v + 2 < count; v += 3)
            {
                into.Add(new ArmourTriangle(key, Vertex(blob, off + v * 16), Vertex(blob, off + (v + 1) * 16), Vertex(blob, off + (v + 2) * 16)));
            }
            off += count * 16;
        }
    }

    private static Vector3 Vertex(ReadOnlySpan<byte> b, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(b[at..]),
        BinaryPrimitives.ReadSingleLittleEndian(b[(at + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(b[(at + 8)..]));

    /// <summary>
    /// The hull's armour: every .geometry file in the hull model's folder that carries an armour model, minus destroyed
    /// variants. Converted to metres with X = length (bow positive), Y = height, Z = beam. Null when nothing was found.
    /// </summary>
    public static ArmourMesh? LoadHull(GameFileSystem fs, string hullModelPath)
    {
        var dir = hullModelPath.Replace('\\', '/');
        var slash = dir.LastIndexOf('/');
        if (slash < 0) return null;
        dir = dir[..(slash + 1)];

        var tris = new List<ArmourTriangle>();
        var sources = new List<string>();
        foreach (var f in fs.Under(dir).Where(f => f.Path.EndsWith(".geometry", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path))
        {
            var name = f.Path[(f.Path.LastIndexOf('/') + 1)..];
            if (name.Contains("dead", StringComparison.OrdinalIgnoreCase)) continue;
            // Only files directly in the hull folder (sub-folders hold other parts).
            if (f.Path.IndexOf('/', dir.Length) >= 0) continue;
            var found = Parse(fs.Read(f));
            if (found.Count == 0) continue;
            sources.Add(name);
            tris.AddRange(found.Select(t => new ArmourTriangle(t.Key, ToShip(t.A), ToShip(t.B), ToShip(t.C))));
        }
        if (tris.Count == 0) return null;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var t in tris)
        {
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
        }
        return new ArmourMesh(tris, min, max, sources);
    }

    /// <summary>Model space (left-handed, X beam, Y up, Z forward) → ship space in metres (X forward, Y up, Z starboard).</summary>
    private static Vector3 ToShip(Vector3 m) => new(m.Z * MetresPerUnit, m.Y * MetresPerUnit, m.X * MetresPerUnit);
}
