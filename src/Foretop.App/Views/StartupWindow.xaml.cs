using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

/// <summary>
/// Shown while Foretop opens: the checks of <see cref="StartupViewModel"/>, then the main window. Closes by itself when
/// the checks are done (a little later when one of them has news); a click, Enter, Space or Esc opens Foretop at once.
/// </summary>
public partial class StartupWindow : Window
{
    private static readonly TimeSpan PauseWhenFine = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PauseWithNews = TimeSpan.FromSeconds(4);
    private Action? _continue;

    public StartupWindow(StartupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private StartupViewModel Vm => (StartupViewModel)DataContext;

    /// <summary>Runs the checks; <paramref name="next"/> (show the main window) runs once, when done or when skipped.</summary>
    public async void Run(Action next, string? snapshot = null)
    {
        _continue = next;
        await Vm.RunAsync();
        if (snapshot is not null) Snapshot(snapshot);
        if (_continue is null) return; // skipped meanwhile
        ContinueButton.Focus();
        var pause = new DispatcherTimer { Interval = Vm.HasNews ? PauseWithNews : PauseWhenFine };
        pause.Tick += (_, _) => { pause.Stop(); Continue(); };
        pause.Start();
    }

    private void Continue()
    {
        var next = _continue;
        _continue = null;
        next?.Invoke();
        Close();
    }

    private void OnContinue(object sender, RoutedEventArgs e) => Continue();

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space or Key.Escape) Continue();
    }

    /// <summary>Development: the screen as an image, once the checks are done (--startup-snapshot).</summary>
    private void Snapshot(string png)
    {
        UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(png);
        enc.Save(fs);
    }
}
