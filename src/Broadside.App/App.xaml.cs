using System.Windows;
using System.Windows.Threading;
using Broadside.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Broadside.App;

public partial class App : Application
{
    private AppHost? _host;
    private Services.SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Development runs (--snapshot) may run beside a normal instance; everything else is single-instance.
        if (!e.Args.Contains("--snapshot"))
        {
            _single = Services.SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(BringToFront));
            if (_single is null)
            {
                Shutdown();
                return;
            }
        }

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _host?.Log.LogError(args.Exception, "Unobserved background task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _host?.Log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);

        // WPF formats bindings as en-US unless told otherwise; use the user's regional settings like the view models do.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(
                System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));

        if (e.Args.Contains("--software"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        _host = new AppHost();
        _host.Log.LogInformation("Broadside {Version} starting", Services.AppInfo.Version);

        Services.ThemeManager.Apply(Services.ThemeManager.Resolve(
            e.Args.Contains("--light") ? "Light" : _host.Settings.Current.Theme));

        var shell = new ShellViewModel(_host);
        var window = new MainWindow(_host) { DataContext = shell };
        MainWindow = window;
        if (e.Args.Contains("--minimized") || _host.Settings.Current.StartMinimized)
            window.WindowState = WindowState.Minimized;
        window.ContentRendered += (_, _) =>
        {
            var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            _host.Log.LogInformation("Window rendered {Ms} ms after process start", (int)(DateTime.Now - started).TotalMilliseconds);
        };
        window.Show();

        if (_host.Settings.Current.AutoDetectBattles) _host.Watcher.Start();
        _host.Settings.Changed += s =>
        {
            if (s.AutoDetectBattles) _host.Watcher.Start();
            else _host.Watcher.Stop();
            var theme = Services.ThemeManager.Resolve(s.Theme);
            if (theme != Services.ThemeManager.Current) Dispatcher.BeginInvoke(() => SwitchTheme(theme));
        };

        // Warm the ship catalogue and PR expected values in the background.
        _ = _host.Ships.EnsureLoadedAsync(_host.Region, CancellationToken.None);
        _ = _host.Expected.GetAsync(CancellationToken.None);

        // Housekeeping: drop cached API responses nobody has needed for 30 days (they are refetched if needed).
        var host = _host;
        _ = Task.Run(() =>
        {
            try
            {
                var removed = host.Cache.Prune(TimeSpan.FromDays(30));
                host.Log.LogInformation("Cache: {Removed} old responses pruned, {Count} kept, {Kb} KB",
                    removed, host.Cache.Count(), host.Cache.SizeBytes() / 1024);
            }
            catch (Exception ex) { host.Log.LogWarning(ex, "Cache maintenance failed"); }
        });

        HandleDevArgs(e.Args, shell, window);
        if (e.Args.Contains("--diag"))
        {
            var diag = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            diag.Tick += (_, _) =>
            {
                var gc = GC.GetGCMemoryInfo();
                var p = System.Diagnostics.Process.GetCurrentProcess();
                _host.Log.LogInformation(
                    "diag: managed heap {Heap} MB, committed {Committed} MB, working set {Ws} MB, private {Priv} MB, gen2 count {Gen2}",
                    GC.GetTotalMemory(false) / 1048576, gc.TotalCommittedBytes / 1048576, p.WorkingSet64 / 1048576,
                    p.PrivateMemorySize64 / 1048576, GC.CollectionCount(2));
            };
            diag.Start();
        }
        if (e.Args.Contains("--open-latest-history") && _host.History.List(1).FirstOrDefault() is { } latest)
            shell.Live.ShowStored(latest);
    }

    /// <summary>
    /// Development switches: --open &lt;replay|json&gt; shows a file, --page &lt;Live|Settings|…&gt; picks a page,
    /// --snapshot &lt;png&gt; [--wait &lt;s&gt;] renders the window to an image and exits.
    /// </summary>
    private static void HandleDevArgs(string[] args, ShellViewModel shell, Window window)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--open") is { } file && System.IO.File.Exists(file))
        {
            var json = file.EndsWith(".wowsreplay", StringComparison.OrdinalIgnoreCase)
                ? Core.Game.ReplayHeaderReader.ReadArenaJson(file)
                : System.IO.File.ReadAllText(file);
            if (Core.Game.ArenaInfoParser.TryParse(json, out var info, out _)) shell.Live.ShowBattle(info!, live: false);
        }

        if (Arg("--page") is { } page && Enum.TryParse<Page>(page, true, out var p)) shell.SelectedPage = p;
        if (Arg("--profile") is { } name) shell.OpenProfile(name);
        if (args.Contains("--compare")) shell.Live.ShowComparison = true;
        if (Arg("--switch-theme") is { } switchTo)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); ((App)Current).SwitchTheme(switchTo); };
            t.Start();
        }
        if (Arg("--scroll") is { } scroll && double.TryParse(scroll, out var offset))
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                // Scroll the first ScrollViewer of the current page, for screenshots of long pages.
                var sv = FindAll<System.Windows.Controls.ScrollViewer>(window)
                    .Where(x => x.IsVisible).OrderByDescending(x => x.ExtentHeight).FirstOrDefault();
                sv?.ScrollToVerticalOffset(offset);
            });

        if (Arg("--snapshot") is { } png)
        {
            var wait = double.TryParse(Arg("--wait"), out var w) ? w : 3;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window = Current.MainWindow; // may have been rebuilt (theme switch)
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(window);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(png)) enc.Save(fs);
                Current.Shutdown();
            };
            timer.Start();
        }
    }

    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindAll<T>(child)) yield return c;
        }
    }

    /// <summary>Applies another palette and rebuilds the main window on the same view models.</summary>
    private void SwitchTheme(string theme)
    {
        if (_host is null || MainWindow is not MainWindow old) return;
        Services.ThemeManager.Apply(theme);
        var w = new MainWindow(_host)
        {
            DataContext = old.DataContext,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left, Top = old.Top, Width = old.Width, Height = old.Height,
        };
        MainWindow = w;
        w.Show();
        w.WindowState = old.WindowState;
        old.Close();
        _host.Log.LogInformation("Theme switched to {Theme}", theme);
    }

    private void BringToFront()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Log.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails are in the log.", "Broadside",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Log.LogInformation("Broadside exiting");
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
