using System.Globalization;
using System.Windows.Data;

namespace Foretop.App.Views;

/// <summary>Two-way enum ↔ bool for radio-button navigation.</summary>
public sealed class EnumMatch : IValueConverter
{
    public static readonly EnumMatch Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>Window-level commands.</summary>
public static class ShellCommands
{
    public static readonly System.Windows.Input.RoutedUICommand FocusSearch = new("Search", nameof(FocusSearch), typeof(ShellCommands));
}

public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the bound value's text equals the parameter (enums, strings).</summary>
public sealed class EqualsToVisible : IValueConverter
{
    public static readonly EqualsToVisible Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound number is not 0.</summary>
public sealed class NonZeroToVisible : IValueConverter
{
    public static readonly NonZeroToVisible Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n != 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bar width: ratio (0–1) times the parameter (maximum width in pixels).</summary>
public sealed class RatioToWidth : IValueConverter
{
    public static readonly RatioToWidth Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double r && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) ? Math.Max(0, r) * max : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when all bound values are equal (radio buttons generated from a list, checked for the current choice).</summary>
public sealed class AllEqual : IMultiValueConverter
{
    public static readonly AllEqual Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values.Skip(1).All(v => Equals(v, values[0]));

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
