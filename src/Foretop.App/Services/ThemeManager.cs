using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Foretop.App.Services;

/// <summary>
/// Swaps the colour palette (merged dictionary 0) between Dark and Light. "System" follows the Windows
/// app theme. Views use StaticResource for speed, so the main window is rebuilt after a switch.
/// </summary>
public static class ThemeManager
{
    /// <summary>Theme names as shown in Settings → the palette file Themes/Colors.&lt;file&gt;.xaml.</summary>
    private static readonly (string Name, string File)[] Themes =
    [
        ("Dark", "Dark"), ("Light", "Light"),
        ("Steel", "Steel"), ("Chart Room", "ChartRoom"), ("Combat Information Center", "CIC"),
    ];

    public static readonly IReadOnlyList<string> Options = ["System", .. Themes.Select(t => t.Name)];

    public static string Current { get; private set; } = "Dark";

    /// <summary>The theme to use for a setting: a known name, else the Windows app theme (light or dark).</summary>
    public static string Resolve(string? setting) =>
        Themes.Any(t => t.Name == setting) ? setting! : WindowsUsesLightTheme() ? "Light" : "Dark";

    public static void Apply(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var file = Themes.FirstOrDefault(t => t.Name == theme).File ?? "Dark";
        var palette = new ResourceDictionary { Source = new Uri($"/Themes/Colors.{file}.xaml", UriKind.Relative) };
        if (dicts.Count > 0) dicts[0] = palette;
        else dicts.Insert(0, palette);
        Current = theme;
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark or light window title bar to match the theme (Windows 10 20H1+ / 11; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Application.Current.Resources["IsDarkTheme"] is false ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
