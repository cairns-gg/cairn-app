using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Closes with true only when a checked address was confirmed. Cancel, Escape and the
/// title bar all leave the mod as it was.
/// </summary>
public partial class ModUrlWindow : Window
{
    public ModUrlWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
