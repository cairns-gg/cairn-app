using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Closes with true only when the named action was chosen. Cancel, Escape and the title
/// bar all mean no, so nothing destructive can happen by dismissing this.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
