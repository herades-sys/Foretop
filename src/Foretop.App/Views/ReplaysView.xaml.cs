using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Foretop.App.ViewModels;

namespace Foretop.App.Views;

public partial class ReplaysView : UserControl
{
    public ReplaysView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ReplaysViewModel old) old.Chat.CollectionChanged -= OnChatChanged;
            if (e.NewValue is ReplaysViewModel vm) vm.Chat.CollectionChanged += OnChatChanged;
        };
    }

    // Keep the newest chat line in view.
    private void OnChatChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    private void OnChatClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatRow row) Vm?.JumpTo(row.T);
    }

    private ReplaysViewModel? Vm => DataContext as ReplaysViewModel;

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is ReplayRow row) Vm?.OpenCommand.Execute(row);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is ReplayRow row)
        {
            Vm?.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Space plays and pauses, unless a text box has the focus.
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && Vm is { HasScene: true } vm)
        {
            vm.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>A 0–1 fraction times the width given as parameter.</summary>
public sealed class FractionWidth : IValueConverter
{
    public static readonly FractionWidth Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is double f ? Math.Clamp(f, 0, 1) : 0) * (double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when all bound values are equal.</summary>
public sealed class EqualsConverter : IMultiValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Visible only when every bound value is false.</summary>
public sealed class AllFalseToVisible : IMultiValueConverter
{
    public static readonly AllFalseToVisible Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is false) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
