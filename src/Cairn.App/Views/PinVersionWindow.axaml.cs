using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Closes with true only when a version was chosen. Cancel, Escape and the title bar all
/// leave the mod as it was — including leaving an existing pin in place, since the way to
/// remove one is the pin button on the row rather than a choice in here.
/// </summary>
public partial class PinVersionWindow : Window
{
    public PinVersionWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnPin(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
