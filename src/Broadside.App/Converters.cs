using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Broadside.Core.Stats;

namespace Broadside.App;

/// <summary>
/// Colours a value. Parameter picks the metric:
///   wr    win rate %      red &lt; 47 ≤ yellow &lt; 53 ≤ green &lt; 60 ≤ purple
///   pr    Personal Rating red &lt; 1100 ≤ yellow &lt; 1550 ≤ green &lt; 2100 ≤ purple
///   ratio dmg ÷ expected  red &lt; 0.85 ≤ yellow &lt; 1.15 ≤ green &lt; 1.5 ≤ purple
/// </summary>
public sealed class RatingBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double v) return Res("TextDim");
        var (low, mid, high) = (parameter as string) switch
        {
            "wr" => (47.0, 53.0, 60.0),
            "pr" => (1100.0, 1550.0, 2100.0),
            "ratio" => (0.85, 1.15, 1.5),
            _ => (double.MinValue, double.MaxValue, double.MaxValue),
        };
        return v < low ? Res("RateRed") : v < mid ? Res("RateYellow") : v < high ? Res("RateGreen") : Res("RatePurple");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Same thresholds as the converter, for view models that build chart data.</summary>
    public static Brush For(double? value, string metric) =>
        (Brush)new RatingBrushConverter().Convert(value, typeof(Brush), metric, CultureInfo.InvariantCulture);

    public static Brush ForClass(ShipClass c) =>
        (Brush)new ShipClassBrushConverter().Convert(c, typeof(Brush), null, CultureInfo.InvariantCulture);
}

public sealed class ShipClassBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ShipClass.Destroyer => RatingBrushConverter.Res("ClassDD"),
        ShipClass.Cruiser => RatingBrushConverter.Res("ClassCA"),
        ShipClass.Battleship => RatingBrushConverter.Res("ClassBB"),
        ShipClass.AirCarrier => RatingBrushConverter.Res("ClassCV"),
        ShipClass.Submarine => RatingBrushConverter.Res("ClassSS"),
        _ => RatingBrushConverter.Res("TextDim"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is not null && value is not "") ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
