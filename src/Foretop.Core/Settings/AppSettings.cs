using System.Text.Json;
using System.Text.Json.Serialization;
using Foretop.Core.Api;

namespace Foretop.Core.Settings;

public sealed class AppSettings
{
    public Region Region { get; set; } = Region.Eu;

    /// <summary>DPAPI-protected application ID (base64). Never stored in plain text.</summary>
    public string? ProtectedApplicationId { get; set; }

    /// <summary>Manual game folder; empty = detect automatically.</summary>
    public string? GameRoot { get; set; }

    public bool AutoDetectBattles { get; set; } = true;
    public bool SwitchToLiveOnBattle { get; set; } = true;
    public bool CompactMode { get; set; }
    /// <summary>Sidebar shows only the page icons.</summary>
    public bool SidebarCollapsed { get; set; }
    /// <summary>Interface font family; null = Foretop's default (Segoe UI).</summary>
    public string? FontFamily { get; set; }
    /// <summary>Size of the whole interface, 1 = 100 %.</summary>
    public double TextScale { get; set; } = 1.0;
    /// <summary>Look for a newer Foretop on GitHub after start and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowBots { get; set; } = true;

    /// <summary>The player's own nickname, learnt from the last live battle (shown in the sidebar).</summary>
    public string? PlayerName { get; set; }
    /// <summary>"System" (follow Windows), "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Requests per second sent to the Wargaming API (limit for standalone apps is about 10).</summary>
    public double ApiRatePerSecond { get; set; } = 8;

    public int PlayerCacheMinutes { get; set; } = 60;
    public int ClanCacheHours { get; set; } = 24;
    public int AccountIdCacheDays { get; set; } = 30;

    public double WindowWidth { get; set; } = 1720;
    public double WindowHeight { get; set; } = 900;
}

public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string protectedValue);
}

/// <summary>Loads and saves settings.json in the app data folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public SettingsStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public string? GetApplicationId() =>
        string.IsNullOrEmpty(Current.ProtectedApplicationId) ? null : _protector.Unprotect(Current.ProtectedApplicationId);

    public void SetApplicationId(AppSettings target, string? appId) =>
        target.ProtectedApplicationId = string.IsNullOrWhiteSpace(appId) ? null : _protector.Protect(appId.Trim());

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, _path, overwrite: true);
        Current = s;
        Changed?.Invoke(s);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Json), Json)!;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Unreadable settings: keep the file for inspection, start with defaults.
            File.Copy(_path, _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
        }
        return new AppSettings();
    }
}
