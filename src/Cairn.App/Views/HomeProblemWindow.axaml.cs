using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cairn.App.Views;

/// <summary>
/// Two ways out, and both of them honest. Quit changes nothing, so reconnecting the disk and
/// starting again finds everything where it was. Using the default clears the pointer, which
/// is a decision to start empty rather than a repair — so it is the second button, not the
/// accented one.
/// </summary>
public partial class HomeProblemWindow : Window
{
    /// <summary>True when the user chose to go back to the default and carry on.</summary>
    public bool UseDefault { get; private set; }

    public HomeProblemWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    private void OnUseDefault(object? sender, RoutedEventArgs e)
    {
        UseDefault = true;
        Close();
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
