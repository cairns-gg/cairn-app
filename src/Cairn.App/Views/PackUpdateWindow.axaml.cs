using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Confirms taking an author's newer revision. Closes with true only when it was applied,
/// so dismissing the window by any route — Cancel, the title bar, Escape — leaves the pack
/// exactly as it was.
///
/// The choices made on the rows are written straight through to the plan, so cancelling
/// discards them with the plan itself rather than leaving half an answer behind.
/// </summary>
public partial class PackUpdateWindow : Window
{
    public PackUpdateWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
