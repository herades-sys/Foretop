using System.Windows.Controls;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class ProfileView : UserControl
{
    public ProfileView() => InitializeComponent();

    private ProfileViewModel? Vm => DataContext as ProfileViewModel;

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.SearchCommand.Execute(null);
        if (e.Key == Key.Escape && Vm is not null) Vm.ShowSuggestions = false;
    }

    private void OnEncounterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: EncounterRow row }) Vm?.OpenEncounterCommand.Execute(row);
    }
}
