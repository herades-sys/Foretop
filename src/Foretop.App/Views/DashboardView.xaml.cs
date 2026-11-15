using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Foretop.App.ViewModels;
using Page = Foretop.App.ViewModels.Page;

namespace Foretop.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void OnBattleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Recent.SelectedItem is HistoryRow row) (DataContext as DashboardViewModel)?.OpenBattleCommand.Execute(row);
    }

    private void OnOwnBattleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Own.SelectedItem is OwnBattleRow row) (DataContext as DashboardViewModel)?.OpenOwnBattleCommand.Execute(row);
    }
}

/// <summary>Page values for XAML command parameters.</summary>
public static class PageRef
{
    public static readonly Page Settings = Page.Settings;
    public static readonly Page History = Page.History;
    public static readonly Page Ships = Page.Ships;
    public static readonly Page Profiles = Page.Profiles;
}

/// <summary>Visible when the bound count is 0.</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public static readonly ZeroToVisible Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
