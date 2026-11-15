using System.Windows;
using System.Windows.Media;

namespace Foretop.App.Services;

/// <summary>A font the user can pick; Name is null for Foretop's default.</summary>
public sealed record FontOption(string? Name, string Label, FontFamily Family)
{
    public override string ToString() => Label;
}

/// <summary>A text size choice; shown as "125 %".</summary>
public sealed record ScaleOption(double Value)
{
    public override string ToString() => (Value * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %";
}

/// <summary>
/// The interface font: the <c>UiFont</c> resource, which the window and every TextBlock use through DynamicResource,
/// so a change shows at once. Numbers in fixed-width places keep <c>NumFont</c>.
/// </summary>
public static class FontManager
{
    public const string DefaultFamily = "Segoe UI Variable Text, Segoe UI";

    /// <summary>Interface sizes offered in Settings (the whole interface scales, so nothing gets cut off or crowded).</summary>
    public static IReadOnlyList<ScaleOption> TextScales { get; } = [new(0.9), new(1.0), new(1.1), new(1.25), new(1.5)];

    public static void Apply(string? family)
    {
        if (Application.Current is null) return;
        Application.Current.Resources["UiFont"] = new FontFamily(string.IsNullOrWhiteSpace(family) ? DefaultFamily : family);
    }

    public static double ClampScale(double scale) => scale is >= 0.8 and <= 2.0 ? scale : 1.0;

    /// <summary>Foretop's default first, then every installed font by name.</summary>
    public static IReadOnlyList<FontOption> InstalledFonts()
    {
        var list = new List<FontOption> { new(null, "Default (Segoe UI)", new FontFamily(DefaultFamily)) };
        var lang = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var name = f.FamilyNames.TryGetValue(lang, out var local) ? local : f.Source;
            if (!string.IsNullOrWhiteSpace(name)) list.Add(new FontOption(f.Source, name, f));
        }
        return [list[0], .. list.Skip(1).DistinctBy(o => o.Label, StringComparer.OrdinalIgnoreCase).OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)];
    }
}
