using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class ClansView : UserControl
{
    private ClansViewModel? _vm;

    public ClansView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ClansViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
        Unloaded += (_, _) => { if (_vm is not null) _vm.PropertyChanged -= OnVmChanged; _vm = null; };
        Loaded += (_, _) =>
        {
            if (_vm is null && DataContext is ClansViewModel vm) { _vm = vm; vm.PropertyChanged += OnVmChanged; }
        };
    }

    /// <summary>A newly shown clan starts at the top of the page (the member table would otherwise pull the view down).</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClansViewModel.Clan))
            Dispatcher.BeginInvoke(() => Scroller.ScrollToTop(), DispatcherPriority.Loaded);
    }

    private void OnMemberDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MembersGrid.SelectedItem is ClanMemberRow row && DataContext is ClansViewModel vm) vm.OpenMemberCommand.Execute(row);
    }
}
