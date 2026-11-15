using System.IO.Compression;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Foretop.Core.Replays;

/// <summary>
/// A <c>.wowsreplay</c> file: magic 0x11343212, a count of length-prefixed JSON blocks (block 0 is the arena info,
/// the same shape as <c>tempArenaInfo.json</c>), then the packet stream: two uint32 (unpacked and packed size),
/// Blowfish-ECB with every block XOR-ed with the previous plain block, then zlib.
/// The key is the one every public replay tool uses; the file is only read.
/// </summary>
public sealed class ReplayFile
{
    public const uint Magic = 0x11343212;

    private static readonly byte[] Key = [0x29, 0xB7, 0xC9, 0x09, 0x38, 0x3F, 0x84, 0x88, 0xFA, 0x98, 0xEC, 0x4E, 0x13, 0x19, 0x79, 0xFB];

    public required string ArenaJson { get; init; }
    public required IReadOnlyList<string> ExtraBlocks { get; init; }
    public required byte[] Packets { get; init; }

    public static ReplayFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static ReplayFile Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || BitConverter.ToUInt32(bytes, 0) != Magic) throw new InvalidDataException("Not a World of Warships replay.");
        var pos = 4;
        var count = BitConverter.ToInt32(bytes, pos); pos += 4;
        if (count is < 1 or > 16) throw new InvalidDataException("Unexpected replay header.");
        var blocks = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var len = BitConverter.ToInt32(bytes, pos); pos += 4;
            if (len < 0 || pos + len > bytes.Length) throw new InvalidDataException("Truncated replay header.");
            blocks.Add(Encoding.UTF8.GetString(bytes, pos, len));
            pos += len;
        }
        if (pos + 8 > bytes.Length) throw new InvalidDataException("The replay has no battle data (still recording, or cut off).");
        var unpacked = BitConverter.ToInt32(bytes, pos);
        return new ReplayFile { ArenaJson = blocks[0], ExtraBlocks = blocks.Skip(1).ToList(), Packets = Decrypt(bytes.AsSpan(pos + 8), unpacked) };
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> data, int unpackedHint)
    {
        var bf = new BlowfishEngine();
        bf.Init(false, new KeyParameter(Key));
        var n = data.Length / 8 * 8;
        var plain = new byte[n];
        var src = data[..n].ToArray();
        for (var i = 0; i < n; i += 8)
        {
            bf.ProcessBlock(src, i, plain, i);
            if (i > 0) for (var k = 0; k < 8; k++) plain[i + k] ^= plain[i - 8 + k];
        }
        using var z = new ZLibStream(new MemoryStream(plain), CompressionMode.Decompress);
        var ms = new MemoryStream(unpackedHint is > 0 and < 512 << 20 ? unpackedHint : 0);
        try
        {
            z.CopyTo(ms);
        }
        catch (InvalidDataException) when (ms.Length > 0)
        {
            // A replay cut off mid-battle (game crash) ends in a broken zlib stream; keep what decoded.
        }
        return ms.ToArray();
    }
}
