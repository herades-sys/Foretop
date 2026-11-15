using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(Grid.SelectedItems.OfType<HistoryRow>().ToList());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is HistoryRow row) Vm?.OpenCommand.Execute(row);
    }
}
