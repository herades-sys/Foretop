using System.Text;

namespace Foretop.Core.Game;

/// <summary>
/// Reads the JSON header of a .wowsreplay file. It has the same shape as tempArenaInfo.json,
/// so finished battles can be opened (and the app tested) without the game running.
/// Layout: uint32 magic, uint32 block count, then per block uint32 length + bytes; block 1 is the arena JSON.
/// </summary>
public static class ReplayHeaderReader
{
    private const uint Magic = 0x11343212;
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    public static string ReadArenaJson(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var br = new BinaryReader(fs);
        var magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Not a World of Warships replay (unexpected file signature).");
        var blocks = br.ReadUInt32();
        if (blocks < 1)
            throw new InvalidDataException("Replay has no JSON header.");
        var len = br.ReadInt32();
        if (len <= 0 || len > MaxJsonBytes)
            throw new InvalidDataException($"Replay JSON header has an invalid length ({len}).");
        var bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }
}
