using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foretop.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Foretop.App.Services;

/// <summary>
/// Looks for a newer Foretop on GitHub shortly after start and then once a day, and installs it on request:
/// downloads the MSI, checks GitHub's SHA-256, closes Foretop, runs the installer and starts the new version.
/// The portable version only offers the download page (an MSI would install a second copy).
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private const string LastCheckKey = "update.lastCheck";
    private const string SkipKey = "update.skip";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly AppHost _host;
    private readonly UpdateChecker _checker;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _download;

    public UpdateService(AppHost host)
    {
        _host = host;
        _checker = new UpdateChecker(host.Http);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += async (_, _) => await CheckIfDueAsync();
        var last = host.Kv.Get(LastCheckKey);
        LastChecked = last?.UpdatedAt;
        StatusText = last is null ? "Not checked yet." : last.Value.Value;
    }

    /// <summary>The newer release, if any (also when the user skipped it; see <see cref="ShowNotice"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotice), nameof(NoticeText), nameof(NotesPreview), nameof(InstallLabel))]
    private ReleaseInfo? _available;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private DateTimeOffset? _lastChecked;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(InstallCommand))] private bool _isInstalling;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNotice))] private string? _skipped;

    /// <summary>True when Foretop runs from the folder its installer put it in (only then can it update itself).</summary>
    public bool IsInstalledCopy { get; } = DetectInstalledCopy();

    public bool ShowNotice => Available is not null && Skipped != Available.Version.ToString(3);
    public string NoticeText => Available is null ? "" : $"Update {Available.Version.ToString(3)}";
    public string InstallLabel => IsInstalledCopy ? "Install and restart" : "Open download page";
    public string LastCheckedText => LastChecked is { } t ? "Last checked " + t.LocalDateTime.ToString("g") : "";
    partial void OnLastCheckedChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastCheckedText));

    /// <summary>The release notes without the download/checksum block, for the update panel.</summary>
    public string NotesPreview
    {
        get
        {
            if (Available is null) return "";
            var notes = Available.Notes.Replace("\r", "");
            var cut = notes.IndexOf("### Download", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) notes = notes[..cut];
            notes = notes.Replace("**", "").Trim();
            // Join the changelog's wrapped lines (indented continuations) and show bullets as such.
            var lines = new List<string>();
            foreach (var line in notes.Split('\n'))
            {
                if (line.Length > 0 && char.IsWhiteSpace(line[0]) && lines.Count > 0 && lines[^1].Length > 0) lines[^1] += " " + line.Trim();
                else lines.Add(line.StartsWith("- ") ? "• " + line[2..].Trim() : line.Trim());
            }
            notes = string.Join("\n", lines).Trim();
            return notes.Length > 1500 ? notes[..1500].TrimEnd() + " …" : notes;
        }
    }

    public void Start(bool enabled, string? feedFile)
    {
        _checker.FeedFile = feedFile;
        Skipped = _host.Kv.Get(SkipKey)?.Value;
        if (!enabled && feedFile is null) return;
        _timer.Start();
        // First check a little after start, so it never competes with a battle that is loading.
        var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(feedFile is null ? 20 : 1) };
        once.Tick += async (_, _) => { once.Stop(); await CheckIfDueAsync(force: feedFile is not null); };
        once.Start();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) { _timer.Start(); _ = CheckIfDueAsync(); }
        else _timer.Stop();
    }

    private bool _checkedThisRun;

    private async Task CheckIfDueAsync(bool force = false)
    {
        // The first check of each run always asks GitHub: a release found before a restart is not kept, so skipping
        // it for the rest of the 24 h would hide the update notice.
        if (!force && _checkedThisRun && LastChecked is { } last && DateTimeOffset.Now - last < Interval && Available is null) return;
        _checkedThisRun = true;
        await CheckAsync();
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        StatusText = "Checking GitHub…";
        try
        {
            var result = await _checker.CheckAsync(AppInfo.Version);
            Available = result.Outcome == UpdateCheckOutcome.Available ? result.Release : null;
            StatusText = result.Message;
            LastChecked = DateTimeOffset.Now;
            if (_checker.FeedFile is null) _host.Kv.Set(LastCheckKey, result.Message);
            _host.Log.LogInformation("Update check: {Outcome} ({Message})", result.Outcome, result.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = Available?.PageUrl is { Length: > 0 } u ? u : _checker.ReleasesPage;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Skip()
    {
        if (Available is null) return;
        Skipped = Available.Version.ToString(3);
        _host.Kv.Set(SkipKey, Skipped);
        IsPanelOpen = false;
    }

    [RelayCommand]
    private void Later() => IsPanelOpen = false;

    private bool CanInstall() => !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Available is not { } release) return;
        if (!IsInstalledCopy || release.Msi is null)
        {
            OpenReleasePage();
            return;
        }

        IsInstalling = true;
        _download = new CancellationTokenSource();
        try
        {
            var msi = await DownloadAsync(release.Msi, _download.Token);
            ProgressText = "Closing Foretop and installing…";
            RunInstallerAndExit(msi, release);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Download cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _host.Log.LogWarning(ex, "Update download failed");
            ProgressText = "The update could not be downloaded: " + ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task<string> DownloadAsync(ReleaseAsset asset, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.DataDir, "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, asset.Name);
        var partial = target + ".part";

        var source = Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) ? uri : null;
        if (asset.Sha256 is null)
            throw new InvalidDataException("GitHub gives no checksum for this file, so it is not installed automatically. Download it from the release page instead.");
        if (source is { IsFile: true } && _checker.FeedFile is not null)
        {
            File.Copy(source.LocalPath, partial, overwrite: true); // development feed (--update-feed)
        }
        else if (source is null || source.Scheme != Uri.UriSchemeHttps
                 || !(source.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || source.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("the download address is not on GitHub, so it was not used.");
        }
        else
        {
            using var http = HttpFactory.Create();
            http.Timeout = Timeout.InfiniteTimeSpan; // a 56 MB file; ct cancels
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            var shown = DateTime.MinValue;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(200))
                {
                    shown = DateTime.UtcNow;
                    ProgressText = total > 0 ? $"Downloading… {100.0 * done / total:0} % of {total / 1048576.0:0} MB" : $"Downloading… {done / 1048576.0:0} MB";
                }
            }
        }

        ProgressText = "Checking the download…";
        {
            var expected = asset.Sha256;
            await using var check = File.OpenRead(partial);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
            if (actual != expected)
                throw new InvalidDataException("the file does not match GitHub's checksum, so it was not installed.");
        }
        File.Move(partial, target, overwrite: true);
        _host.Log.LogInformation("Update {File} downloaded and verified", asset.Name);
        return target;
    }

    /// <summary>
    /// A small script waits for this process to end, runs the MSI with a progress bar only, then starts the installed
    /// Foretop again. The script and the installer stay in %LOCALAPPDATA%\Foretop\updates.
    /// </summary>
    private void RunInstallerAndExit(string msi, ReleaseInfo release)
    {
        var exe = InstalledExecutable() ?? Environment.ProcessPath!;
        var log = Path.ChangeExtension(msi, ".install.log");
        var script = Path.Combine(Path.GetDirectoryName(msi)!, $"install-{release.Version.ToString(3)}.ps1");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        File.WriteAllText(script, $"""
            # Foretop update to {release.Version.ToString(3)}: wait for Foretop to close, install, start it again.
            Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue
            $p = Start-Process msiexec.exe -ArgumentList '/i', {Q("\"" + msi + "\"")}, '/passive', '/norestart', '/l*v', {Q("\"" + log + "\"")} -Wait -PassThru
            Start-Process {Q(exe)}
            exit $p.ExitCode
            """, new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        _host.Log.LogInformation("Installing update {Version}; Foretop closes now", release.Version);
        Application.Current.Shutdown();
    }

    private static string? InstalledExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Foretop\Installer");
        return key?.GetValue("Executable") as string;
    }

    private static bool DetectInstalledCopy()
    {
        var installed = InstalledExecutable();
        return installed is not null && Environment.ProcessPath is { } me
            && string.Equals(Path.GetFullPath(installed), Path.GetFullPath(me), StringComparison.OrdinalIgnoreCase);
    }
}
