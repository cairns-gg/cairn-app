using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Shows what a pack would bring before it is added. Closes with true only when Add was
/// pressed, so Cancel, Escape and the title bar all leave the library alone.
/// </summary>
public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnAdd(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
