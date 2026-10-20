using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Closes with true only on the restore button. Cancel, Escape and the title bar leave the
/// pack exactly as it was — nothing in here has touched it.
/// </summary>
public partial class RevisionsWindow : Window
{
    public RevisionsWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnRestore(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
