using System.Globalization;
using System.Windows.Data;

namespace Broadside.App.Views;

/// <summary>Two-way enum ↔ bool for radio-button navigation.</summary>
public sealed class EnumMatch : IValueConverter
{
    public static readonly EnumMatch Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
