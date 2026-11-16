using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class PersonalAnalyticsView : UserControl
{
    public PersonalAnalyticsView() => InitializeComponent();

    private PersonalAnalyticsViewModel? Vm => DataContext as PersonalAnalyticsViewModel;

    /// <summary>Shift+click on a tier chip selects the range from the last clicked tier (the normal toggle is skipped).</summary>
    private void OnTierChipDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: FilterChip chip } && Vm is { } vm
            && vm.Filters.TierClicked(chip.Value, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            e.Handled = true;
    }

    /// <summary>A click on a row of the tier table filters everything by that tier.</summary>
    private void OnTierRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject d && FindRow(d) is { Item: AnalyticsRow row } && Vm is { } vm)
            vm.TierOverview.PickCommand.Execute(row);
    }

    private static DataGridRow? FindRow(System.Windows.DependencyObject? d)
    {
        while (d is not null and not DataGridRow) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (History.SelectedItem is PersonalRow row && DataContext is PersonalAnalyticsViewModel vm) vm.OpenCommand.Execute(row);
    }
}
