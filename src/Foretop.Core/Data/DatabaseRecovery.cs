using System.IO;
using Microsoft.Data.Sqlite;

namespace Foretop.Core.Data;

/// <summary>Decides whether a database file is really damaged, and sets it aside safely.</summary>
public static class DatabaseRecovery
{
    /// <summary>
    /// SQLite's own quick check on the file. Null = the file is fine (or could not be checked because it is in use, which
    /// is not damage); otherwise the problem SQLite reports.
    /// </summary>
    public static string? CheckFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            c.Open();
            using (var wait = c.CreateCommand())
            {
                wait.CommandText = "PRAGMA busy_timeout = 3000;";
                wait.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "quick_check returned nothing";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // busy/locked: in use, not damaged
        {
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Moves the database and its -wal/-shm files into <paramref name="dir"/>, all or none: the newest data of a WAL
    /// database lives in the -wal file, so moving the main file alone loses it. Throws when a file is in use.
    /// </summary>
    public static void MoveTogether(string database, string dir)
    {
        var files = new[] { "", "-wal", "-shm" }.Select(s => database + s).Where(File.Exists).ToList();
        foreach (var f in files)
        {
            // In use by another process? Then nothing is moved.
            using var probe = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Directory.CreateDirectory(dir);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var f in files)
            {
                var to = Path.Combine(dir, Path.GetFileName(f));
                File.Move(f, to);
                moved.Add((f, to));
            }
        }
        catch (IOException)
        {
            foreach (var (from, to) in moved) File.Move(to, from); // put back what was already moved
            throw;
        }
    }
}
