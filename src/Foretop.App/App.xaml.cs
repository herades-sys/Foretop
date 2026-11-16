using System.Windows;
using System.Windows.Threading;
using Foretop.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Foretop.App;

public partial class App : Application
{
    private AppHost? _host;
    private Services.SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Helper mode: read the game files into the ship-data cache, then exit (no window).
        if (e.Args.Contains(Services.ShipDataExtraction.Switch))
        {
            Shutdown(Services.ShipDataExtraction.RunHelper(e.Args));
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        // --anonymize: hide player names and clan tags (for published screenshots)
        Services.Anonymizer.Enabled = e.Args.Contains("--anonymize");
        var dataAt = Array.IndexOf(e.Args, "--data-dir");
        if (dataAt >= 0 && dataAt + 1 < e.Args.Length) Services.AppPaths.UseDataDir(e.Args[dataAt + 1]);

        // Development runs (--snapshot, --soak with its own --data-dir) may run beside a normal instance; everything else
        // is single-instance.
        if (!e.Args.Contains("--snapshot") && !(e.Args.Contains("--soak") && e.Args.Contains("--data-dir")))
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

        var migratedFrom = Services.AppPaths.MigrateFromBroadside();
        _host = new AppHost();
        if (migratedFrom is not null)
        {
            _host.Log.LogInformation("Data moved from {Old} (the app was renamed from Broadside)", migratedFrom);
            if (_host.Settings.Current.StartWithWindows)
                Services.StartupRegistration.Apply(true, _host.Settings.Current.StartMinimized);
        }
        _host.Log.LogInformation("Foretop {Version} starting", Services.AppInfo.Version);

        // --theme "<name>": this session only (not saved), for snapshots of each theme
        var themeAt = Array.IndexOf(e.Args, "--theme");
        Services.ThemeManager.Apply(Services.ThemeManager.Resolve(
            themeAt >= 0 && themeAt + 1 < e.Args.Length ? e.Args[themeAt + 1]
            : e.Args.Contains("--light") ? "Light" : _host.Settings.Current.Theme));

        Services.FontManager.Apply(_host.Settings.Current.FontFamily);
        Services.Anonymizer.Self = _host.Settings.Current.PlayerName;
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

        // Own battle results; not in development snapshot runs, which may run beside the real instance.
        if (!e.Args.Contains("--snapshot"))
        {
            _host.Results.Attach(_host.Watcher);
            _ = _host.Results.ResumeAsync();
            // Replay facts for counted battles: after start (also fills in older battles) and whenever results change.
            var enrich = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            enrich.Tick += (_, _) => { enrich.Stop(); _ = Task.Run(() => _host.Personal.RunAsync()); };
            enrich.Start();
            _host.Results.Changed += () => Dispatcher.BeginInvoke(() => { enrich.Stop(); enrich.Start(); });
        }
        // Update check: not in development snapshot runs, unless they bring their own release file (--update-feed).
        var feedAt = Array.IndexOf(e.Args, "--update-feed");
        var feed = feedAt >= 0 && feedAt + 1 < e.Args.Length ? e.Args[feedAt + 1] : null;
        if (!e.Args.Contains("--snapshot") || feed is not null) _host.Updates.Start(_host.Settings.Current.CheckForUpdates, feed);
        if (e.Args.Contains("--update-panel")) _host.Updates.IsPanelOpen = true;
        var checkUpdates = _host.Settings.Current.CheckForUpdates;
        _host.Settings.Changed += s =>
        {
            if (s.CheckForUpdates == checkUpdates) return;
            checkUpdates = s.CheckForUpdates;
            Dispatcher.BeginInvoke(() => _host.Updates.SetEnabled(checkUpdates));
        };

        if (_host.Settings.Current.AutoDetectBattles) _host.Watcher.Start();
        _host.Settings.Changed += s =>
        {
            if (s.AutoDetectBattles) _host.Watcher.Start();
            else _host.Watcher.Stop();
            var theme = Services.ThemeManager.Resolve(s.Theme);
            if (theme != Services.ThemeManager.Current) Dispatcher.BeginInvoke(() => SwitchTheme(theme));
        };

        // Ship Intelligence data: read the game client once per build, in a helper process.
        StartShipData(_host);

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

        if (Arg("--page") is { } page)
        {
            if (page.Equals("Statistics", StringComparison.OrdinalIgnoreCase)) page = nameof(Page.Analytics);
            if (Enum.TryParse<Page>(page, true, out var p)) shell.SelectedPage = p;
        }
        if (Arg("--profile") is { } name) shell.OpenProfile(name);
        // --ship-filter 8,10,BB: pre-select profile ship filter chips (tiers as numbers, classes as DD/CA/BB/CV/SS)
        if (Arg("--ship-filter") is { } filter)
            foreach (var part in filter.Split(',', StringSplitOptions.TrimEntries))
                foreach (var chip in shell.Profiles.TierChips.Where(c => c.Value.ToString() == part).Concat(shell.Profiles.ClassChips.Where(c => c.Label == part)))
                    chip.IsChecked = true;
        // --analytics-source Recorded|Account, --analytics-tiers 8,10, --analytics-min 20, --analytics-class Battleship: Analytics filters
        if (Enum.TryParse<Core.Personal.AnalyticsSource>(Arg("--analytics-source"), true, out var src)) shell.Personal.Filters.Source = src;
        if (Arg("--analytics-tiers") is { } at)
            shell.Personal.Filters.SetTiers(at.Split(',').Select(t => int.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));
        if (int.TryParse(Arg("--analytics-min"), out var amin) && shell.Personal.Filters.MinBattlesOptions.FirstOrDefault(o => o.Value == amin) is { } mo)
            shell.Personal.Filters.MinBattles = mo;
        if (Arg("--analytics-class") is { } acls)
            foreach (var chip in shell.Personal.Filters.ClassChips.Where(c => c.Tip.StartsWith(acls, StringComparison.OrdinalIgnoreCase))) chip.IsChecked = true;
        // --player-search <text>: types the text into Player Search (name prefix or account id)
        if (Arg("--player-search") is { } q) { shell.SelectedPage = Page.PlayerSearch; shell.PlayerSearch.Query = q; }
        // --soak <minutes>: long-session test. Cycles through the real workload (recorded battle roster shown as live and
        // refreshed against the API, page switches, Analytics filters, replay playback) and logs memory, handles and
        // threads every minute; quits at the end. Only for a separate --data-dir.
        if (int.TryParse(Arg("--soak"), out var soakMinutes) && args.Contains("--data-dir")) StartSoak(shell, soakMinutes);
        // --best-tiers 8,9 / --best-min 50: Best performing ships filter on the profile (a number not in the list = custom)
        if (Arg("--best-tiers") is { } bestTiers)
            foreach (var part in bestTiers.Split(',', StringSplitOptions.TrimEntries))
                foreach (var chip in shell.Profiles.BestTierChips.Where(c => c.Value.ToString() == part))
                    chip.IsChecked = true;
        if (int.TryParse(Arg("--best-min"), out var bestMin))
        {
            if (shell.Profiles.MinBattlesOptions.FirstOrDefault(o => o.Value == bestMin) is { } known) shell.Profiles.SelectedMinBattles = known;
            else
            {
                shell.Profiles.SelectedMinBattles = shell.Profiles.MinBattlesOptions.Last();
                shell.Profiles.CustomMinBattles = bestMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        // --font <family> / --text-scale <1.25>: view only, not saved
        if (Arg("--font") is { } font) Services.FontManager.Apply(font);
        if (double.TryParse(Arg("--text-scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts)) shell.UiScale = ts;
        // --stats-filter <min tier> [--stats-view Overall|Filtered|Both]: Live Battle statistics filter, for snapshots
        if (int.TryParse(Arg("--stats-filter"), out var minTier) && shell.Live.StatsFilterOptions.FirstOrDefault(o => o.MinTier == minTier) is { } opt)
        {
            shell.Live.PersistStatsFilter = false; // a development run must not change the saved choice
            shell.Live.SelectedStatsFilter = opt;
        }
        if (Enum.TryParse<ViewModels.StatsView>(Arg("--stats-view"), true, out var sv))
        {
            shell.Live.PersistStatsFilter = false;
            shell.Live.StatsViewMode = sv;
        }
        // --enrich-now: read replay facts for own results at once (normally 15 s after start)
        if (args.Contains("--enrich-now")) _ = Task.Run(() => ((App)Current)._host!.Personal.RunAsync());
        if (args.Contains("--sidebar-collapsed")) shell.IsSidebarCollapsed = true; // view only, not saved
        // --replay <path|latest> [--replay-time <s>]: open a replay in the minimap player at a time.
        if (Arg("--replay") is { } replay)
        {
            shell.SelectedPage = Page.Replays;
            _ = OpenReplayAsync();
            async Task OpenReplayAsync()
            {
                await shell.Replays.EnsureLoadedAsync();
                var row = replay == "latest" ? shell.Replays.Replays.FirstOrDefault()
                    : shell.Replays.Replays.FirstOrDefault(r => r.Entry.Path.Equals(System.IO.Path.GetFullPath(replay), StringComparison.OrdinalIgnoreCase))
                      ?? shell.Replays.RowForFile(System.IO.Path.GetFullPath(replay)); // a file outside the replay folder
                if (row is null) return;
                await shell.Replays.OpenCommand.ExecuteAsync(row);
                if (double.TryParse(Arg("--replay-time"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at))
                    shell.Replays.Time = at;
                if (args.Contains("--replay-play")) shell.Replays.PlayPauseCommand.Execute(null);
                // --replay-debug, --replay-full, --replay-zoom <zoom>[,x,z]: minimap view for snapshots (zoom on world x,z)
                if (args.Contains("--replay-debug")) shell.Replays.ShowDebug = true;
                // --render-minimap <WxH,WxH,…> <folder>: the minimap alone, laid out off screen at each size (any size, not
                // limited by the monitor), saved as minimap_<W>x<H>.png; checks the renderer at other resolutions.
                if (Arg("--render-minimap") is { } sizes && args.SkipWhile(a => a != "--render-minimap").Skip(2).FirstOrDefault() is { } outDir
                    && shell.Replays.Scene is { } scene)
                    foreach (var sz in sizes.Split(','))
                    {
                        var parts = sz.Split('x');
                        double cw = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), ch = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        var canvas = new Views.ReplayCanvas { Scene = scene, Time = shell.Replays.Time, ShowDebug = shell.Replays.ShowDebug };
                        var host = new System.Windows.Controls.Border { Background = (System.Windows.Media.Brush)Current.FindResource("Panel"), Child = canvas };
                        host.Measure(new Size(cw, ch));
                        host.Arrange(new Rect(0, 0, cw, ch));
                        host.UpdateLayout();
                        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)cw, (int)ch, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bmp.Render(host);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                        using var f = System.IO.File.Create(System.IO.Path.Combine(outDir, $"minimap_{sz}.png"));
                        enc.Save(f);
                    }
                if (args.Contains("--replay-full")) shell.Replays.IsFullView = true;
                if (Arg("--replay-zoom") is { } rz)
                    _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        var p = rz.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        FindAll<Views.ReplayCanvas>(window).FirstOrDefault()?.ZoomTo(p[0], p.Length > 2 ? p[1] : null, p.Length > 2 ? p[2] : null);
                    });
            }
        }
        // --window <width>x<height>: window size in DIPs (not maximised), for snapshots at other sizes.
        if (Arg("--window") is { } size && size.Split('x') is [var ws, var hs]
            && double.TryParse(ws, out var ww) && double.TryParse(hs, out var wh))
        {
            window.WindowState = WindowState.Normal;
            // Windows keeps a window within about the monitor's size. A test of a larger screen (2560x1440, 3440x1440
            // on a 1920x1080 monitor) raises that limit for this window, so the layout and the snapshot are real.
            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            int maxW = (int)Math.Ceiling(ww * scale.DpiScaleX) + 32, maxH = (int)Math.Ceiling(wh * scale.DpiScaleY) + 32;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                const int WmGetMinMaxInfo = 0x0024;
                if (msg == WmGetMinMaxInfo)
                {
                    // MINMAXINFO: ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize (POINTs of two ints).
                    var curW = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 32);
                    var curH = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 36);
                    System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 32, Math.Max(curW, maxW));
                    System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 36, Math.Max(curH, maxH));
                }
                return IntPtr.Zero;
            });
            (window.Width, window.Height) = (ww, wh);
        }
        // --account <name>: use this account name for the session only (not saved), for testing Ship Statistics.
        if (Arg("--account") is { } account && ((App)Current)._host is { } host) host.Settings.Current.PlayerName = account;
        // Ship Intelligence: --ship <name> opens a ship, --compare-ships "A;B;C" fills the comparison,
        // --ships-section <Database|Armour|Statistics|Comparison|Equipment> picks the section. Applied once the data is loaded.
        if (Arg("--ship") is { } shipName || Arg("--compare-ships") is not null || Arg("--ships-section") is not null)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
            var tries = 0;
            t.Tick += (_, _) =>
            {
                if (shell.Ships.StateKind != "Ready" && ++tries < 60) return;
                t.Stop();
                if (Arg("--compare-ships") is { } list)
                {
                    foreach (var n in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (shell.Ships.TryOpenShip(n)) shell.Ships.AddCurrentToComparisonCommand.Execute(null);
                }
                if (Arg("--ship") is { } s) shell.Ships.TryOpenShip(s);
                if (Arg("--ships-section") is { } sec && Enum.TryParse<ShipSection>(sec, true, out var section)) shell.Ships.Section = section;
                if (Arg("--armour-view") is { } view) shell.Ships.SetArmourViewCommand.Execute(view);
                if (Arg("--plate") is { } plate && int.TryParse(plate, out var key)) shell.Ships.SelectedPlateKey = key;
                if (args.Contains("--citadel-only"))
                {
                    var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    later.Tick += (_, _) => { later.Stop(); shell.Ships.CitadelOnlyCommand.Execute(null); };
                    later.Start();
                }
            };
            t.Start();
        }
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

    private static void StartSoak(ShellViewModel shell, int minutes)
    {
        var host = ((App)Current)._host!;
        var log = host.Log;
        var started = DateTime.UtcNow;
        var step = 0;
        var arena = host.History.List(1).FirstOrDefault() is { } rec && Core.Game.ArenaInfoParser.TryParse(rec.ArenaJson, out var info, out _) ? info : null;
        var pages = new[] { Page.Live, Page.Dashboard, Page.History, Page.Analytics, Page.Profiles, Page.Replays, Page.Ships, Page.Analytics };
        var rnd = new Random(42);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        log.LogInformation("Soak: {Minutes} min, roster {HasRoster}", minutes, arena is not null);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            try
            {
                step++;
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(minutes))
                {
                    timer.Stop();
                    log.LogInformation("Soak: finished after {Steps} steps", step);
                    Current.Shutdown();
                    return;
                }
                shell.SelectedPage = pages[step % pages.Length];
                if (arena is not null && step % 15 == 1) shell.Live.ShowBattle(arena, live: true);   // every 5 min: roster + API
                if (arena is not null && step % 15 == 8) shell.Live.RefreshCommand.Execute(null);      // forced refresh
                if (shell.SelectedPage == Page.Analytics)
                {
                    var f = shell.Personal.Filters;
                    f.Source = rnd.Next(2) == 0 ? Core.Personal.AnalyticsSource.Account : Core.Personal.AnalyticsSource.Recorded;
                    f.SetTiers(rnd.Next(3) == 0 ? [] : Enumerable.Range(rnd.Next(1, 9), rnd.Next(1, 4)));
                }
                if (shell.SelectedPage == Page.Profiles && step % 24 == 4 && host.Settings.Current.PlayerName is { } me) shell.OpenProfile(me);
                if (shell.SelectedPage == Page.Replays && shell.Replays.Scene is null && shell.Replays.Replays.Count > 0)
                    shell.Replays.OpenCommand.Execute(shell.Replays.Replays[0]);
                if (shell.SelectedPage == Page.Replays && shell.Replays.Scene is not null && !shell.Replays.IsPlaying)
                {
                    shell.Replays.Speed = 40;
                    shell.Replays.PlayPauseCommand.Execute(null);
                }
                if (step % 3 == 0)
                {
                    proc.Refresh();
                    log.LogInformation("Soak {Step}: managed {Managed} MB, working set {Ws} MB, private {Priv} MB, handles {Handles}, threads {Threads}, gen2 {Gen2}, cpu {Cpu} s",
                        step, GC.GetTotalMemory(false) / 1048576, proc.WorkingSet64 / 1048576, proc.PrivateMemorySize64 / 1048576, proc.HandleCount,
                        proc.Threads.Count, GC.CollectionCount(2), (int)proc.TotalProcessorTime.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Soak step {Step} failed", step);
            }
        };
        timer.Start();
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

    internal static void StartShipData(AppHost host) =>
        _ = host.ShipData.EnsureAsync((root, file, progress, ct) =>
            Services.ShipDataExtraction.RunAsync(root, file, progress, host.Log, ct), CancellationToken.None);

    /// <summary>Applies another palette and rebuilds the main window on the same view models.</summary>
    private void SwitchTheme(string theme)
    {
        if (_host is null || MainWindow is not MainWindow old) return;
        Services.ThemeManager.Apply(theme);
        Services.FontManager.Apply(_host.Settings.Current.FontFamily); // a theme may bring its own fonts
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
        MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails are in the log.", "Foretop",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Log.LogInformation("Foretop exiting");
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
