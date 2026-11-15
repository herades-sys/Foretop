using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();

    private void OnPlayerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: EncounteredPlayer p }) (DataContext as StatisticsViewModel)?.OpenPlayerCommand.Execute(p);
    }
}
