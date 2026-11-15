using System.Text;
using System.Xml.Linq;

namespace Foretop.Core.Replays;

/// <summary>
/// Decodes BigWorld-encoded values with the types from <see cref="EntityDefs"/>. Values come back as
/// long / double / bool / string / byte[] / float[] (vectors) / List&lt;object?&gt; / Dictionary&lt;string, object?&gt;.
/// </summary>
public ref struct BigWorldReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly EntityDefs _defs;
    public int Position { get; private set; }

    public BigWorldReader(ReadOnlySpan<byte> data, EntityDefs defs)
    {
        _data = data;
        _defs = defs;
        Position = 0;
    }

    public readonly int Remaining => _data.Length - Position;

    public byte U8() => _data[Position++];
    public short I16() { var v = BitConverter.ToInt16(_data.Slice(Position, 2)); Position += 2; return v; }
    public ushort U16() { var v = BitConverter.ToUInt16(_data.Slice(Position, 2)); Position += 2; return v; }
    public int I32() { var v = BitConverter.ToInt32(_data.Slice(Position, 4)); Position += 4; return v; }
    public uint U32() { var v = BitConverter.ToUInt32(_data.Slice(Position, 4)); Position += 4; return v; }
    public long I64() { var v = BitConverter.ToInt64(_data.Slice(Position, 8)); Position += 8; return v; }
    public float F32() { var v = BitConverter.ToSingle(_data.Slice(Position, 4)); Position += 4; return v; }

    public ReadOnlySpan<byte> Bytes(int n)
    {
        if (n < 0 || n > Remaining) throw new InvalidDataException("Value runs past the end of the packet.");
        var s = _data.Slice(Position, n);
        Position += n;
        return s;
    }

    /// <summary>Length prefix of strings, blobs and arrays: one byte, or 0xFF followed by a 24-bit length.</summary>
    public int PackedLength()
    {
        var b = U8();
        if (b != 0xFF) return b;
        var lo = U16();
        var hi = U8();
        return lo | (hi << 16);
    }

    public object? Read(XElement type)
    {
        type = _defs.Resolve(type);
        switch (EntityDefs.Head(type))
        {
            case "INT8": return (long)(sbyte)U8();
            case "UINT8": return (long)U8();
            case "BOOL": return U8() != 0;
            case "INT16": return (long)I16();
            case "UINT16": return (long)U16();
            case "INT32": return (long)I32();
            case "UINT32": return (long)U32();
            case "INT64": return I64();
            case "UINT64": return I64();
            case "FLOAT32" or "FLOAT": return (double)F32();
            case "FLOAT64": { var v = BitConverter.ToDouble(Bytes(8)); return v; }
            case "VECTOR2": return new[] { F32(), F32() };
            case "VECTOR3": return new[] { F32(), F32(), F32() };
            case "VECTOR4": return new[] { F32(), F32(), F32(), F32() };
            case "STRING" or "UNICODE_STRING": return Encoding.UTF8.GetString(Bytes(PackedLength()));
            case "BLOB" or "PYTHON": return Bytes(PackedLength()).ToArray();
            case "ARRAY" or "TUPLE":
            {
                var of = type.Element("of") ?? throw new InvalidDataException("Array without element type.");
                var n = int.TryParse(type.Element("size")?.Value.Trim(), out var fixedN) ? fixedN : PackedLength();
                var list = new List<object?>(Math.Min(n, 4096));
                for (var i = 0; i < n; i++) list.Add(Read(of));
                return list;
            }
            case "FIXED_DICT":
            {
                if (string.Equals(type.Element("AllowNone")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase) && U8() == 0) return null;
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var f in type.Element("Properties")?.Elements() ?? [])
                {
                    var t = f.Element("Type") ?? throw new InvalidDataException($"Field {f.Name} without type.");
                    d[f.Name.LocalName] = Read(t);
                }
                return d;
            }
            default: throw new NotSupportedException($"Type {EntityDefs.Head(type)} is not decoded.");
        }
    }
}
