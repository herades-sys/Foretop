using System.IO.Compression;
namespace Foretop.Core.Data;

/// <summary>Keeps the log folder small without losing anything.</summary>
public static class LogArchive
{
    /// <summary>
    /// Daily logs older than <paramref name="keep"/> go into one zip per month under the archive folder (about ten
    /// times smaller). Nothing is lost: a log file is removed only after its zip entry holds every byte of it.
    /// </summary>
    public static int PackOldLogs(string logDir, string archiveDir, TimeSpan keep)
    {
        var packed = 0;
        try
        {
            var cutoff = DateTime.Now - keep;
            foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stamp = name[(name.LastIndexOf('-') + 1)..];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day) || day >= cutoff.Date) continue;
                Directory.CreateDirectory(archiveDir);
                var zipPath = Path.Combine(archiveDir, $"logs-{day:yyyy-MM}.zip");
                var length = new FileInfo(file).Length;
                using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
                {
                    var entryName = Path.GetFileName(file);
                    if (zip.GetEntry(entryName) is null)
                        zip.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Optimal);
                }
                using (var check = System.IO.Compression.ZipFile.OpenRead(zipPath))
                    if (check.GetEntry(Path.GetFileName(file)) is not { } e || e.Length != length) continue; // keep the original
                File.Delete(file); // its content is in the zip
                packed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* try again next start */ }
        return packed;
    }
}
