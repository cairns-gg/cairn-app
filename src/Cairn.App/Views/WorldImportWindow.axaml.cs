using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Picks worlds out of a plain Vintage Story install to copy into a pack. Closes with true
/// only when Copy was pressed; every other way out copies nothing.
/// </summary>
public partial class WorldImportWindow : Window
{
    public WorldImportWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
