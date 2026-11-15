using System.Windows.Controls;

namespace Foretop.App.Views;

public partial class PlayerSearchView : UserControl
{
    public PlayerSearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => QueryBox.Focus();
    }
}
