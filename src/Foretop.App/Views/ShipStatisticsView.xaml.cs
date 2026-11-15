using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class ShipStatisticsView : UserControl
{
    public ShipStatisticsView() => InitializeComponent();

    private void OnRowOpen(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is OwnShipRow row) (DataContext as ShipIntelligenceViewModel)?.OpenOwnShipCommand.Execute(row);
    }
}
