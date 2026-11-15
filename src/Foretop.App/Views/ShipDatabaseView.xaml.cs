using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class ShipDatabaseView : UserControl
{
    public ShipDatabaseView() => InitializeComponent();

    private void OnRowOpen(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is ShipRow row) (DataContext as ShipIntelligenceViewModel)?.OpenRowCommand.Execute(row);
    }
}
