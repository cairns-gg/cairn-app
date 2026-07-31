using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;

namespace Cairn.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fetches a mod's versions the moment its dropdown is opened.
    ///
    /// In code-behind because DropDownOpened is an event with no command to bind to, and
    /// this is what code-behind is for: turning a view event into a view-model call.
    /// Loading on row selection instead would be unreliable, since a ComboBox inside a
    /// list row can swallow the press that would have selected the row.
    /// </summary>
    private void OnVersionDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox { DataContext: ModRowViewModel row })
            _ = row.EnsureReleasesAsync();
    }

    /// <summary>
    /// Double-clicking a row opens that mod on ModDB — the obvious thing to want from a
    /// row, and otherwise a small button is the only way to it.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double-click on the row's own controls belongs to them. Without this,
        // double-clicking the version dropdown would also launch a browser.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors()
                .TakeWhile(v => v != sender)
                .Any(v => v is Button or ComboBox))
            return;

        switch ((sender as Control)?.DataContext)
        {
            case ModRowViewModel row:
                row.OpenPageCommand.Execute(null);
                break;
            case SearchHitViewModel hit:
                hit.OpenPageCommand.Execute(null);
                break;
        }
    }
}
