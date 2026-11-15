using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Foretop.App.Services;

/// <summary>
/// Ship icons from the Wargaming CDN, downloaded once into the app data folder and decoded at display
/// size (the originals are several times larger than the 34 px they are shown at). Frozen images are
/// shared by every row that shows the same ship.
/// </summary>
public sealed class ShipIconCache(HttpClient http, string directory, ILogger log)
{
    private const int DecodeWidth = 72; // 2× the largest display width, for high-DPI screens

    private readonly ConcurrentDictionary<string, ImageSource?> _memory = new();
    private readonly ConcurrentDictionary<string, Task<bool>> _downloads = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    public static ShipIconCache? Instance { get; set; }

    /// <summary>The icon if it is available now; otherwise starts the download and calls <paramref name="onReady"/> on the UI thread when done.</summary>
    public ImageSource? Get(string? url, Action? onReady)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        if (_memory.TryGetValue(url, out var cached)) return cached;

        var path = PathFor(url);
        if (File.Exists(path))
        {
            var img = Load(path);
            _memory[url] = img;
            return img;
        }

        var task = _downloads.GetOrAdd(url, u => DownloadAsync(u, path));
        if (onReady is not null)
            task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result) _ui.BeginInvoke(onReady);
            }, TaskScheduler.Default);
        return null;
    }

    private async Task<bool> DownloadAsync(string url, string path)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            log.LogDebug("Ship icon {Url} not downloaded: {Error}", url, ex.Message);
            _downloads.TryRemove(url, out _); // try again next time
            return false;
        }
    }

    private ImageSource? Load(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // read now, keep no file handle
            bi.DecodePixelWidth = DecodeWidth;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            log.LogDebug("Ship icon {Path} unreadable: {Error}", path, ex.Message);
            return null;
        }
    }

    private string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 12).ToLowerInvariant();
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return Path.Combine(directory, hash + (ext.Length is > 1 and < 6 ? ext : ".png"));
    }
}
