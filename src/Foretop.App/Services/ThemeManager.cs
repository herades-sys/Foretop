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
    public static readonly IReadOnlyList<string> Options = ["System", "Dark", "Light"];

    public static string Current { get; private set; } = "Dark";

    public static string Resolve(string? setting) => setting switch
    {
        "Light" => "Light",
        "Dark" => "Dark",
        _ => WindowsUsesLightTheme() ? "Light" : "Dark",
    };

    public static void Apply(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"/Themes/Colors.{theme}.xaml", UriKind.Relative) };
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
        var dark = Current == "Dark" ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
