using System.Net.Http;
using Foretop.App.Services;
using Foretop.Core.Api;
using Foretop.Core.Data;
using Foretop.Core.Game;
using Foretop.Core.GameData;
using Foretop.Core.Settings;
using Foretop.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Foretop.App;

/// <summary>Composition root: builds every service once and owns their lifetime.</summary>
public sealed class AppHost : IDisposable
{
    public AppHost()
    {
        Loggers = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new FileLoggerProvider(AppPaths.LogDir, LogLevel.Information));
        });
        Log = Loggers.CreateLogger("Foretop");

        Settings = new SettingsStore(AppPaths.Settings, new DpapiSecretProtector());
        Database = OpenDatabase(out var recoveredTo);
        DatabaseArchivedTo = recoveredTo;
        Cache = new ApiCache(Database);
        Accounts = new AccountStore(Database);
        Snapshots = new SnapshotStore(Database);
        History = new BattleHistoryStore(Database);
        Kv = new KvStore(Database);

        Http = HttpFactory.Create();
        Limiter = new RateLimiter(Math.Clamp(Settings.Current.ApiRatePerSecond, 1, 10));
        Scheduler = new RequestScheduler(Limiter);
        Api = new WgApiClient(Http, Scheduler, Settings.GetApplicationId, Loggers.CreateLogger<WgApiClient>());
        Expected = new ExpectedValuesProvider(Http, Kv, Loggers.CreateLogger<ExpectedValuesProvider>());
        Ships = new ShipCatalog(Database, Kv, Api, Loggers.CreateLogger<ShipCatalog>());
        Stats = new PlayerStatsService(Api, Cache, Accounts, Snapshots, Expected, () => Settings.Current,
            Loggers.CreateLogger<PlayerStatsService>());
        Watcher = new BattleWatcher(() => Settings.Current.GameRoot, Loggers.CreateLogger<BattleWatcher>());
        Results = new BattleResultTracker(Api, Accounts, new BattleResultStore(Database), Snapshots, () => Settings.Current,
            Loggers.CreateLogger<BattleResultTracker>());

        Settings.Changed += s => Limiter.SetRate(Math.Clamp(s.ApiRatePerSecond, 1, 10));
        Ships.LoadCached();
        ShipData = new ShipDataStore(AppPaths.DataDir,
            () => string.IsNullOrWhiteSpace(Settings.Current.GameRoot) ? GameLocator.FindGameRoot() : Settings.Current.GameRoot,
            Loggers.CreateLogger<ShipDataStore>());
        Stats.IsTestShip = id => ShipData.FindById(id)?.IsTestShip == true;
        Replays = new Core.Replays.ReplayLibrary(
            () => string.IsNullOrWhiteSpace(Settings.Current.GameRoot) ? GameLocator.FindGameRoot() : Settings.Current.GameRoot, ShipData);
        ShipIconCache.Instance = new ShipIconCache(Http, AppPaths.IconDir, Loggers.CreateLogger<ShipIconCache>());
        Updates = new UpdateService(this);
    }

    /// <summary>New Foretop versions from GitHub releases.</summary>
    public UpdateService Updates { get; }

    public ILoggerFactory Loggers { get; }
    public ILogger Log { get; }
    public SettingsStore Settings { get; }
    public Database Database { get; }
    public ApiCache Cache { get; }
    public AccountStore Accounts { get; }
    public SnapshotStore Snapshots { get; }
    public BattleHistoryStore History { get; }
    public KvStore Kv { get; }
    public HttpClient Http { get; }
    public RateLimiter Limiter { get; }
    public RequestScheduler Scheduler { get; }
    public WgApiClient Api { get; }
    public ExpectedValuesProvider Expected { get; }
    public ShipCatalog Ships { get; }
    public PlayerStatsService Stats { get; }
    public BattleWatcher Watcher { get; }

    /// <summary>Your own result per battle (account totals before and after), for the Dashboard's session view.</summary>
    public BattleResultTracker Results { get; }

    /// <summary>Ship Intelligence data read from the game client.</summary>
    public ShipDataStore ShipData { get; }

    /// <summary>Replay files in the game's replays folder, decoded for the minimap player.</summary>
    public Core.Replays.ReplayLibrary Replays { get; }

    public Region Region => Settings.Current.Region;

    /// <summary>Set when the database was unreadable at startup and moved aside; the path of the moved copy.</summary>
    public string? DatabaseArchivedTo { get; }

    /// <summary>
    /// Opens the database. A damaged file is moved to archive\&lt;time&gt;\ (never deleted) and a fresh one created,
    /// so a broken cache cannot stop the app from starting.
    /// </summary>
    private Database OpenDatabase(out string? archivedTo)
    {
        archivedTo = null;
        try
        {
            return new Database(AppPaths.Database);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = System.IO.Path.Combine(AppPaths.ArchiveDir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            System.IO.Directory.CreateDirectory(dir);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = AppPaths.Database + suffix;
                if (System.IO.File.Exists(src)) System.IO.File.Move(src, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(src)));
            }
            archivedTo = dir;
            Log.LogError(ex, "Database unreadable; moved to {Dir} and started a new one", dir);
            return new Database(AppPaths.Database);
        }
    }

    public void Dispose()
    {
        Watcher.Dispose();
        Scheduler.Dispose();
        Http.Dispose();
        Loggers.Dispose();
    }
}
