using Microsoft.Data.Sqlite;

namespace Foretop.Core.Data;

/// <summary>Owns the SQLite file and its schema. Connections are pooled by Microsoft.Data.Sqlite.</summary>
public sealed class Database
{
    private const int SchemaVersion = 4;
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 3000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"));
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        if (version < 1)
        {
            Exec(c, """
                CREATE TABLE IF NOT EXISTS kv (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS api_cache (
                    region TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    key TEXT NOT NULL,
                    json TEXT,
                    fetched_at INTEGER NOT NULL,
                    PRIMARY KEY (region, kind, key)
                );
                CREATE TABLE IF NOT EXISTS accounts (
                    region TEXT NOT NULL,
                    name_key TEXT NOT NULL,
                    account_id INTEGER,
                    nickname TEXT NOT NULL,
                    resolved_at INTEGER NOT NULL,
                    PRIMARY KEY (region, name_key)
                );
                CREATE TABLE IF NOT EXISTS ships (
                    ship_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    tier INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    nation TEXT NOT NULL,
                    is_premium INTEGER NOT NULL,
                    is_special INTEGER NOT NULL,
                    image_small TEXT,
                    image_contour TEXT
                );
                CREATE TABLE IF NOT EXISTS player_snapshots (
                    region TEXT NOT NULL,
                    account_id INTEGER NOT NULL,
                    taken_at INTEGER NOT NULL,
                    battles INTEGER NOT NULL,
                    wins INTEGER NOT NULL,
                    damage INTEGER NOT NULL,
                    xp INTEGER NOT NULL,
                    frags INTEGER NOT NULL,
                    survived INTEGER NOT NULL,
                    planes INTEGER NOT NULL,
                    PRIMARY KEY (region, account_id, taken_at)
                );
                CREATE TABLE IF NOT EXISTS battles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    battle_key TEXT NOT NULL UNIQUE,
                    region TEXT NOT NULL,
                    started_at INTEGER,
                    detected_at INTEGER NOT NULL,
                    match_group TEXT NOT NULL,
                    map TEXT,
                    player_name TEXT,
                    arena_json TEXT NOT NULL,
                    summary_json TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_battles_detected ON battles(detected_at DESC);
                """);
        }
        if (version < 2)
        {
            Exec(c, "ALTER TABLE battles ADD COLUMN source TEXT NOT NULL DEFAULT 'live';");
        }
        if (version < 3)
        {
            Exec(c, "ALTER TABLE api_cache ADD COLUMN zjson BLOB;");
            Exec(c, "ALTER TABLE battles ADD COLUMN summary_z BLOB;");
        }
        if (version < 4)
        {
            // Own results per battle: account totals of the battle's mode at the start (baseline) and the difference
            // once the API counts the battle. Values are NULL until then.
            Exec(c, """
                CREATE TABLE IF NOT EXISTS battle_results (
                    battle_key TEXT PRIMARY KEY,
                    region TEXT NOT NULL,
                    account_id INTEGER NOT NULL,
                    mode TEXT NOT NULL,
                    ship_id INTEGER NOT NULL,
                    started_at INTEGER NOT NULL,
                    ended_at INTEGER,
                    status TEXT NOT NULL,
                    baseline_json TEXT,
                    battles INTEGER, wins INTEGER, losses INTEGER, draws INTEGER,
                    damage INTEGER, xp INTEGER, frags INTEGER, survived INTEGER, planes INTEGER,
                    resolved_at INTEGER
                );
                CREATE INDEX IF NOT EXISTS ix_results_started ON battle_results(started_at DESC);
                """);
        }
        Exec(c, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
