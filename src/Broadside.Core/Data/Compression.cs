using System.IO.Compression;
using System.Text;

namespace Broadside.Core.Data;

/// <summary>Brotli for cached JSON. Quality 5 compresses ship lists about 6× in well under a millisecond.</summary>
internal static class Compression
{
    public static byte[] Pack(string text)
    {
        var src = Encoding.UTF8.GetBytes(text);
        var dst = new byte[BrotliEncoder.GetMaxCompressedLength(src.Length)];
        if (!BrotliEncoder.TryCompress(src, dst, out var written, quality: 5, window: 22))
            throw new InvalidOperationException("Brotli compression failed");
        return dst.AsSpan(0, written).ToArray();
    }

    public static string Unpack(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
