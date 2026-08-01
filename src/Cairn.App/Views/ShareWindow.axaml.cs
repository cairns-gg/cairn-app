using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Shows what publishing a pack would send, before it is sent. Closes with true only when
/// Publish was pressed, so dismissing the window any other way — Cancel, the title bar,
/// Escape — publishes nothing.
/// </summary>
public partial class ShareWindow : Window
{
    public ShareWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnPublish(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
