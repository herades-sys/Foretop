using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Foretop.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Foretop.App.Services;

public static class AppPaths
{
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foretop");

    /// <summary>Development runs (--data-dir) use their own folder, so they never touch the real settings and database.</summary>
    public static void UseDataDir(string dir) => DataDir = Path.GetFullPath(dir);

    public static string Database => Path.Combine(DataDir, "foretop.db");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string IconDir => Path.Combine(DataDir, "icons");
    public static string ArchiveDir => Path.Combine(DataDir, "archive");

    /// <summary>
    /// The app was called Broadside before 1.1. On first start as Foretop, its data folder is moved over
    /// (settings, cache, history, logs) and the database file renamed. Nothing is deleted.
    /// </summary>
    public static string? MigrateFromBroadside()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Broadside");
        if (!Directory.Exists(old) || Directory.Exists(DataDir)) return null;
        try
        {
            Directory.Move(old, DataDir);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var oldDb = Path.Combine(DataDir, "broadside.db" + suffix);
                if (File.Exists(oldDb)) File.Move(oldDb, Database + suffix);
            }
            return old;
        }
        catch (IOException)
        {
            return null; // e.g. an old Broadside still running; try again next start
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Encrypts the application ID for the current Windows user (DPAPI).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Broadside.ApplicationId.v1"); // pre-rename value; changing it breaks stored IDs

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // settings copied from another user or PC
        }
    }
}

public static class HttpFactory
{
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 6,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Foretop/" + AppInfo.Version);
        return http;
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Foretop";
    private const string OldValueName = "Broadside";

    public static void Apply(bool enabled, bool minimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (key.GetValue(OldValueName) is not null)
            key.DeleteValue(OldValueName, throwOnMissingValue: false); // the entry from before the rename
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? "";
            key.SetValue(ValueName, $"\"{exe}\"" + (minimized ? " --minimized" : ""));
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false); // a registry value, not a file
        }
    }
}

/// <summary>Structured JSON-lines log, one file per day, written on a background task.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly string _dir;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string dir, LogLevel minLevel)
    {
        _dir = dir;
        _minLevel = minLevel;
        Directory.CreateDirectory(dir);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private async Task WriteLoopAsync()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            try
            {
                var path = Path.Combine(_dir, $"foretop-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(path, line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never crash the app */ }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var entry = new Dictionary<string, object?>
            {
                ["t"] = DateTimeOffset.Now.ToString("O"),
                ["level"] = logLevel.ToString(),
                ["cat"] = category[(category.LastIndexOf('.') + 1)..],
                ["msg"] = formatter(state, exception),
            };
            if (state is IEnumerable<KeyValuePair<string, object?>> props)
                foreach (var (k, v) in props)
                    if (k != "{OriginalFormat}") entry[k] = v?.ToString();
            if (exception is not null) entry["error"] = exception.ToString();
            owner._channel.Writer.TryWrite(System.Text.Json.JsonSerializer.Serialize(entry));
        }
    }
}
