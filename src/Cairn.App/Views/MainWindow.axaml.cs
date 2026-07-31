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
        UiScale.Attach(this);
    }

    /// <summary>
    /// Hands the view model ways to open a window. Knowing how to show a window is the
    /// view's job; the view model only decides when.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not MainViewModel vm) return;

        vm.OpenPreferences = ShowPreferencesAsync;
        vm.ConfirmVersionChange = ConfirmVersionChangeAsync;
        vm.Confirm = ConfirmAsync;
    }

    private Task ShowPreferencesAsync(PreferencesViewModel preferences) =>
        new PreferencesWindow { DataContext = preferences }.ShowDialog(this);

    /// <summary>
    /// True only if the change was applied. Dismissing the dialog any other way — Cancel,
    /// the title bar — leaves the pack alone.
    /// </summary>
    private Task<bool> ConfirmVersionChangeAsync(VersionChangeViewModel change) =>
        new VersionChangeWindow { DataContext = change }.ShowDialog<bool>(this);

    private Task<bool> ConfirmAsync(ConfirmViewModel confirm) =>
        new ConfirmWindow { DataContext = confirm }.ShowDialog<bool>(this);

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
