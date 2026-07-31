using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Confirms a game-version change. Closes with true only when the change was applied, so
/// dismissing the window by any route — Cancel, the title bar, Escape — leaves the pack
/// alone.
/// </summary>
public partial class VersionChangeWindow : Window
{
    public VersionChangeWindow()
    {
        InitializeComponent();
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
