using System.Windows.Controls;

namespace Foretop.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // The ID is masked by default; the PasswordBox cannot bind, so it is synced by hand.
        DataContextChanged += (_, _) => SyncFromViewModel();
        Loaded += (_, _) => SyncFromViewModel();
    }

    private ViewModels.SettingsViewModel? Vm => DataContext as ViewModels.SettingsViewModel;
    private bool _syncing;

    private void SyncFromViewModel()
    {
        if (Vm is null || IdBox.Password == Vm.ApplicationId) return;
        _syncing = true;
        IdBox.Password = Vm.ApplicationId;
        _syncing = false;
    }

    private void OnIdChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_syncing && Vm is not null) Vm.ApplicationId = IdBox.Password;
    }

    private void OnIdTextLostFocus(object sender, System.Windows.RoutedEventArgs e) => SyncFromViewModel();
}
