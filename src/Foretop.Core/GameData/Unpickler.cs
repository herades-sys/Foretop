using System.Globalization;
using System.Numerics;
using System.Text;

namespace Foretop.Core.GameData;

/// <summary>A Python global reference (module.name) met in a pickle.</summary>
public sealed record PyGlobal(string Module, string Name)
{
    public override string ToString() => Module + "." + Name;
}

/// <summary>
/// An instance of a Python class. Attributes come from the pickled state (__dict__); Args keeps the
/// constructor arguments of REDUCE/NEWOBJ calls that are not plain object reconstruction.
/// </summary>
public sealed class PyObject
{
    public PyObject(PyGlobal type) => Type = type;

    public PyGlobal Type { get; }
    public Dictionary<object, object?> Attrs { get; } = new(PyKeyComparer.Instance);
    public object?[]? Args { get; set; }

    public object? this[string name] => Attrs.TryGetValue(name, out var v) ? v : null;

    public override string ToString() => $"<{Type} with {Attrs.Count} attrs>";
}

/// <summary>Dictionary keys from Python: strings and integers compare by value.</summary>
public sealed class PyKeyComparer : IEqualityComparer<object>
{
    public static readonly PyKeyComparer Instance = new();

    public new bool Equals(object? x, object? y) => x switch
    {
        null => y is null,
        long a when y is long b => a == b,
        string a when y is string b => string.Equals(a, b, StringComparison.Ordinal),
        _ => x.Equals(y),
    };

    public int GetHashCode(object obj) => obj switch
    {
        string s => StringComparer.Ordinal.GetHashCode(s),
        _ => obj.GetHashCode(),
    };
}

/// <summary>
/// Minimal, safe Python unpickler (protocols 0–5). It builds plain .NET values and never executes
/// anything: GLOBAL and REDUCE only produce descriptive objects.
/// Mapping: None → null, bool → bool, int → long (BigInteger when larger), float → double,
/// str/bytes → string (UTF-8, Latin-1 fallback), list → List&lt;object?&gt;, tuple → object?[],
/// dict → Dictionary&lt;object, object?&gt;, set → HashSet&lt;object&gt;, objects → <see cref="PyObject"/>.
/// </summary>
public sealed class Unpickler
{
    private readonly Stream _s;
    private readonly List<object?> _stack = new(256);
    private readonly Stack<int> _marks = new();
    private readonly Dictionary<long, object?> _memo = new();

    private sealed class MarkObject;

    private Unpickler(Stream s) => _s = s;

    public static object? Load(Stream stream) => new Unpickler(stream).Run();

    public static object? Load(byte[] data) => Load(new MemoryStream(data, writable: false));

    private object? Run()
    {
        while (true)
        {
            var op = _s.ReadByte();
            if (op < 0) throw new InvalidDataException("Pickle ended without STOP");
            switch ((byte)op)
            {
                case 0x80: ReadByte(); break;                                   // PROTO
                case 0x95: ReadBytes(8); break;                                 // FRAME (size only)
                case (byte)'.': return Pop();                                   // STOP
                case (byte)'(': _marks.Push(_stack.Count); break;               // MARK
                case (byte)'0': Pop(); break;                                   // POP
                case (byte)'1': PopMark(); break;                               // POP_MARK
                case (byte)'2': Push(_stack[^1]); break;                        // DUP
                case (byte)'N': Push(null); break;                              // NONE
                case 0x88: Push(true); break;                                   // NEWTRUE
                case 0x89: Push(false); break;                                  // NEWFALSE
                case (byte)'I': PushIntLine(ReadLine()); break;                 // INT
                case (byte)'J': Push((long)BitConverter.ToInt32(ReadBytes(4))); break;   // BININT
                case (byte)'K': Push((long)ReadByte()); break;                  // BININT1
                case (byte)'M': Push((long)BitConverter.ToUInt16(ReadBytes(2))); break;  // BININT2
                case (byte)'L': Push(ParseLong(ReadLine().TrimEnd('L'))); break; // LONG
                case 0x8a: Push(LongFromBytes(ReadBytes(ReadByte()))); break;   // LONG1
                case 0x8b: Push(LongFromBytes(ReadBytes(BitConverter.ToInt32(ReadBytes(4))))); break; // LONG4
                case (byte)'F': Push(double.Parse(ReadLine(), CultureInfo.InvariantCulture)); break;    // FLOAT
                case (byte)'G':                                                 // BINFLOAT (big-endian)
                {
                    var b = ReadBytes(8);
                    Array.Reverse(b);
                    Push(BitConverter.ToDouble(b));
                    break;
                }
                case (byte)'S': Push(UnquoteString(ReadLine())); break;          // STRING
                case (byte)'T': Push(Text(ReadBytes(BitConverter.ToInt32(ReadBytes(4))))); break; // BINSTRING
                case (byte)'U': Push(Text(ReadBytes(ReadByte()))); break;       // SHORT_BINSTRING
                case (byte)'V': Push(RawUnicodeEscape(ReadLine())); break;      // UNICODE (raw-unicode-escape)
                case (byte)'X': Push(Utf8(ReadBytes(BitConverter.ToInt32(ReadBytes(4))))); break; // BINUNICODE
                case 0x8c: Push(Utf8(ReadBytes(ReadByte()))); break;            // SHORT_BINUNICODE
                case 0x8d: Push(Utf8(ReadBytes(checked((int)BitConverter.ToInt64(ReadBytes(8)))))); break; // BINUNICODE8
                case (byte)'B': Push(Text(ReadBytes(BitConverter.ToInt32(ReadBytes(4))))); break; // BINBYTES
                case (byte)'C': Push(Text(ReadBytes(ReadByte()))); break;       // SHORT_BINBYTES
                case 0x8e: Push(Text(ReadBytes(checked((int)BitConverter.ToInt64(ReadBytes(8)))))); break; // BINBYTES8
                case 0x96: Push(Text(ReadBytes(checked((int)BitConverter.ToInt64(ReadBytes(8)))))); break; // BYTEARRAY8
                case (byte)']': Push(new List<object?>()); break;               // EMPTY_LIST
                case (byte)'a': { var v = Pop(); ((List<object?>)_stack[^1]!).Add(v); break; } // APPEND
                case (byte)'e': { var items = PopMark(); ((List<object?>)_stack[^1]!).AddRange(items); break; } // APPENDS
                case (byte)'l': Push(new List<object?>(PopMark())); break;      // LIST
                case (byte)')': Push(Array.Empty<object?>()); break;            // EMPTY_TUPLE
                case (byte)'t': Push(PopMark().ToArray()); break;               // TUPLE
                case 0x85: Push(new[] { Pop() }); break;                        // TUPLE1
                case 0x86: { var b = Pop(); var a = Pop(); Push(new[] { a, b }); break; } // TUPLE2
                case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); Push(new[] { a, b, c }); break; } // TUPLE3
                case (byte)'}': Push(NewDict()); break;                         // EMPTY_DICT
                case (byte)'d': { var d = NewDict(); FillDict(d, PopMark()); Push(d); break; } // DICT
                case (byte)'s': { var v = Pop(); var k = Pop(); SetItem(_stack[^1], k, v); break; } // SETITEM
                case (byte)'u': { var items = PopMark(); FillDict(_stack[^1], items); break; } // SETITEMS
                case 0x8f: Push(new HashSet<object>(PyKeyComparer.Instance)); break; // EMPTY_SET
                case 0x90: { var items = PopMark(); var set = (HashSet<object>)_stack[^1]!; foreach (var i in items) set.Add(i!); break; } // ADDITEMS
                case 0x91: Push(new HashSet<object>(PopMark()!, PyKeyComparer.Instance)); break; // FROZENSET
                case (byte)'g': Push(_memo[long.Parse(ReadLine(), CultureInfo.InvariantCulture)]); break; // GET
                case (byte)'h': Push(_memo[ReadByte()]); break;                 // BINGET
                case (byte)'j': Push(_memo[BitConverter.ToUInt32(ReadBytes(4))]); break; // LONG_BINGET
                case (byte)'p': _memo[long.Parse(ReadLine(), CultureInfo.InvariantCulture)] = _stack[^1]; break; // PUT
                case (byte)'q': _memo[ReadByte()] = _stack[^1]; break;          // BINPUT
                case (byte)'r': _memo[BitConverter.ToUInt32(ReadBytes(4))] = _stack[^1]; break; // LONG_BINPUT
                case 0x94: _memo[_memo.Count] = _stack[^1]; break;              // MEMOIZE
                case (byte)'c': { var module = ReadLine(); var name = ReadLine(); Push(new PyGlobal(module, name)); break; } // GLOBAL
                case 0x93: { var name = (string)Pop()!; var module = (string)Pop()!; Push(new PyGlobal(module, name)); break; } // STACK_GLOBAL
                case (byte)'R': { var args = (object?[])Pop()!; var callable = Pop(); Push(Reduce(callable, args)); break; } // REDUCE
                case 0x81: { var args = (object?[])Pop()!; var cls = Pop(); Push(NewObject(cls, args)); break; } // NEWOBJ
                case 0x92: { Pop(); var args = (object?[])Pop()!; var cls = Pop(); Push(NewObject(cls, args)); break; } // NEWOBJ_EX
                case (byte)'o': { var items = PopMark(); Push(NewObject(items[0], items.Skip(1).ToArray())); break; } // OBJ
                case (byte)'i': { var module = ReadLine(); var name = ReadLine(); var items = PopMark(); Push(NewObject(new PyGlobal(module, name), items.ToArray())); break; } // INST
                case (byte)'b': { var state = Pop(); Build(_stack[^1], state); break; } // BUILD
                case 0x97: break;                                               // NEXT_BUFFER (unused)
                case 0x98: break;                                               // READONLY_BUFFER
                default:
                    throw new InvalidDataException($"Unsupported pickle opcode 0x{op:X2} at offset {_s.Position - 1}");
            }
        }
    }

    // ------------------------------------------------------------------ objects

    private static object? Reduce(object? callable, object?[] args)
    {
        // copy_reg._reconstructor(cls, base, state): plain object reconstruction (Python 2 protocol 0–2).
        if (callable is PyGlobal { Name: "_reconstructor" } && args.Length >= 1 && args[0] is PyGlobal cls)
            return new PyObject(cls);
        if (callable is PyGlobal g) return new PyObject(g) { Args = args };
        return new PyObject(new PyGlobal("?", callable?.ToString() ?? "?")) { Args = args };
    }

    private static object NewObject(object? cls, object?[] args) =>
        new PyObject(cls as PyGlobal ?? new PyGlobal("?", cls?.ToString() ?? "?")) { Args = args.Length > 0 ? args : null };

    private static void Build(object? target, object? state)
    {
        if (target is not PyObject obj) return;
        if (state is object?[] { Length: 2 } pair) // (dict, slotstate)
        {
            Merge(obj, pair[0]);
            Merge(obj, pair[1]);
        }
        else
        {
            Merge(obj, state);
        }

        static void Merge(PyObject o, object? s)
        {
            if (s is Dictionary<object, object?> d)
                foreach (var (k, v) in d) o.Attrs[k] = v;
        }
    }

    // ------------------------------------------------------------------ stack helpers

    private static Dictionary<object, object?> NewDict() => new(PyKeyComparer.Instance);

    private static void SetItem(object? target, object? key, object? value)
    {
        if (target is Dictionary<object, object?> d) d[key ?? "None"] = value;
    }

    private static void FillDict(object? target, List<object?> items)
    {
        for (var i = 0; i + 1 < items.Count; i += 2) SetItem(target, items[i], items[i + 1]);
    }

    private void Push(object? o) => _stack.Add(o);

    private object? Pop()
    {
        var o = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return o;
    }

    private List<object?> PopMark()
    {
        var at = _marks.Pop();
        var items = _stack.GetRange(at, _stack.Count - at);
        _stack.RemoveRange(at, _stack.Count - at);
        return items;
    }

    // ------------------------------------------------------------------ primitives

    private int ReadByte()
    {
        var b = _s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return b;
    }

    private byte[] ReadBytes(int n)
    {
        var buf = new byte[n];
        _s.ReadExactly(buf);
        return buf;
    }

    private string ReadLine()
    {
        var sb = new StringBuilder();
        int b;
        while ((b = ReadByte()) != '\n') sb.Append((char)b);
        return sb.ToString();
    }

    private void PushIntLine(string line)
    {
        if (line == "00") Push(false);
        else if (line == "01") Push(true);
        else Push(ParseLong(line));
    }

    private static object ParseLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        return BigInteger.Parse(s, CultureInfo.InvariantCulture);
    }

    private static object LongFromBytes(byte[] b)
    {
        if (b.Length == 0) return 0L;
        var big = new BigInteger(b, isUnsigned: false, isBigEndian: false);
        // Not a ?: — its common type would be BigInteger and every long would come back boxed as one.
        if (big >= long.MinValue && big <= long.MaxValue) return (long)big;
        return big;
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);

    /// <summary>Python 2 byte strings: UTF-8 when valid, Latin-1 otherwise.</summary>
    private static string Text(byte[] b)
    {
        try { return StrictUtf8.GetString(b); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>Python's raw-unicode-escape: only \uXXXX and \UXXXXXXXX are escapes; everything else is Latin-1.</summary>
    private static string RawUnicodeEscape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length + 0 && s[i + 1] == 'u' &&
                int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
            {
                sb.Append((char)cp);
                i += 5;
            }
            else if (s[i] == '\\' && i + 9 < s.Length + 0 && s[i + 1] == 'U' &&
                     int.TryParse(s.AsSpan(i + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp32))
            {
                sb.Append(char.ConvertFromUtf32(cp32));
                i += 9;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static string UnquoteString(string s)
    {
        if (s.Length >= 2 && (s[0] == '\'' || s[0] == '"')) s = s[1..^1];
        return System.Text.RegularExpressions.Regex.Unescape(s);
    }
}
