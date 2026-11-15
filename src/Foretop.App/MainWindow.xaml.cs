using System.Windows;

namespace Foretop.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        var s = host.Settings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        Closing += (_, _) => SaveSize();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
        CommandBindings.Add(new System.Windows.Input.CommandBinding(Views.ShellCommands.FocusSearch, (_, _) =>
        {
            GlobalSearch.Focus();
            GlobalSearch.SelectAll();
        }));
    }

    private void SaveSize()
    {
        if (WindowState != WindowState.Normal) return;
        var s = _host.Settings.Clone();
        if (Math.Abs(s.WindowWidth - Width) < 1 && Math.Abs(s.WindowHeight - Height) < 1) return;
        s.WindowWidth = Width;
        s.WindowHeight = Height;
        try { _host.Settings.Save(s); } catch (System.IO.IOException) { }
    }
}
