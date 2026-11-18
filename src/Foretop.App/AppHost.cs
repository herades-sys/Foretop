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
        Karma = new Core.Karma.WowsKarmaClient(Http, Cache, Loggers.CreateLogger<Core.Karma.WowsKarmaClient>());
        Stats.KarmaLookup = Karma.GetAsync;
        Stats.IsTestShip = id => ShipData.FindById(id)?.IsTestShip == true;
        Stats.ShipTier = id => Ships.Get(id)?.Tier is int t and > 0 ? t : ShipData.FindById(id)?.Tier is int g and > 0 ? g : null;
        Replays = new Core.Replays.ReplayLibrary(
            () => string.IsNullOrWhiteSpace(Settings.Current.GameRoot) ? GameLocator.FindGameRoot() : Settings.Current.GameRoot, ShipData);
        ShipIconCache.Instance = new ShipIconCache(Http, AppPaths.IconDir, Loggers.CreateLogger<ShipIconCache>());
        Updates = new UpdateService(this);

        // Personal analytics: ship name/tier/class and the battle rating are kept with each own result.
        Results.ShipLookup = id =>
        {
            if (Ships.Get(id) is { } s) return (s.Name, s.Tier, s.Class.ToString());
            return ShipData.FindById(id) is { } g ? (g.DisplayName, g.Tier, g.Species) : null;
        };
        Results.Expected = ct => Expected.GetAsync(ct);
        Personal = new Core.Personal.PersonalEnricher(Results.Store, Replays, Loggers.CreateLogger<Core.Personal.PersonalEnricher>());
    }

    /// <summary>Adds replay facts (base XP, kills, time survived, team position) to own results.</summary>
    public Core.Personal.PersonalEnricher Personal { get; }

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

    /// <summary>Players' karma from WoWS Karma (wows-karma.com).</summary>
    public Core.Karma.WowsKarmaClient Karma { get; }
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
        Microsoft.Data.Sqlite.SqliteException? last = null;
        // An error while another Foretop opens or closes the same file can look like damage for a moment: try again,
        // then let SQLite check the file itself. Only a file that fails that check is set aside.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return new Database(AppPaths.Database);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 11 or 26) // BUSY, LOCKED, CORRUPT, NOTADB
            {
                last = ex;
                Log.LogWarning("Database open failed (attempt {Attempt}): {Error}", attempt + 1, ex.Message);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Thread.Sleep(750 * (attempt + 1));
            }
        }
        if (last!.SqliteErrorCode is 5 or 6 || Core.Data.DatabaseRecovery.CheckFile(AppPaths.Database) is null)
            throw new System.IO.IOException("Foretop's database is in use or could not be opened right now. Close other Foretop windows and start it again.", last);

        var dir = System.IO.Path.Combine(AppPaths.ArchiveDir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        Core.Data.DatabaseRecovery.MoveTogether(AppPaths.Database, dir); // all three files or none
        archivedTo = dir;
        Log.LogError(last, "Database damaged ({Check}); moved to {Dir} and started a new one", "integrity check failed", dir);
        return new Database(AppPaths.Database);
    }

    public void Dispose()
    {
        Watcher.Dispose();
        Scheduler.Dispose();
        Http.Dispose();
        Loggers.Dispose();
    }
}
