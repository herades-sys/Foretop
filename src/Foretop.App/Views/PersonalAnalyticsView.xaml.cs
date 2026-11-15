using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class PersonalAnalyticsView : UserControl
{
    public PersonalAnalyticsView() => InitializeComponent();

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (History.SelectedItem is PersonalRow row && DataContext is PersonalAnalyticsViewModel vm) vm.OpenCommand.Execute(row);
    }
}
