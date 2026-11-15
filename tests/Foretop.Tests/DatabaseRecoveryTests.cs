using Foretop.Core.Data;
using Microsoft.Data.Sqlite;

namespace Foretop.Tests;

public class DatabaseRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foretop-dbrec-" + Guid.NewGuid().ToString("N"));

    public DatabaseRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } // test scratch folder only
    }

    [Fact]
    public void A_healthy_database_is_not_damaged()
    {
        var db = new Database(Path.Combine(_dir, "ok.db"));
        using (var c = db.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO kv(key, value, updated_at) VALUES ('a', 'b', 1);"; cmd.ExecuteNonQuery(); }
        SqliteConnection.ClearAllPools();
        Assert.Null(DatabaseRecovery.CheckFile(db.Path));
    }

    [Fact]
    public void A_garbage_file_is_damaged()
    {
        var path = Path.Combine(_dir, "bad.db");
        File.WriteAllBytes(path, Enumerable.Range(0, 8192).Select(i => (byte)(i * 31)).ToArray());
        Assert.NotNull(DatabaseRecovery.CheckFile(path));
    }

    [Fact]
    public void Files_are_moved_together_or_not_at_all()
    {
        var path = Path.Combine(_dir, "x.db");
        File.WriteAllText(path, "main");
        File.WriteAllText(path + "-wal", "wal");
        var archive = Path.Combine(_dir, "archive");

        using (new FileStream(path + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read)) // another process holds the WAL
            Assert.ThrowsAny<IOException>(() => DatabaseRecovery.MoveTogether(path, archive));
        Assert.True(File.Exists(path));            // nothing moved
        Assert.True(File.Exists(path + "-wal"));

        DatabaseRecovery.MoveTogether(path, archive);
        Assert.False(File.Exists(path));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(archive, "x.db-wal")));
        Assert.Equal("main", File.ReadAllText(Path.Combine(archive, "x.db")));
    }
}
