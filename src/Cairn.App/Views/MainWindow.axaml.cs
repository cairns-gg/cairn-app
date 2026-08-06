using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
        vm.ConfirmPublish = ConfirmPublishAsync;
        vm.ConfirmPackUpdate = ConfirmPackUpdateAsync;
        vm.ConfirmImport = ConfirmImportAsync;
        vm.Confirm = ConfirmAsync;
        vm.RunOptimumBuild = RunOptimumBuildAsync;
        vm.CopyToClipboard = CopyToClipboardAsync;
    }

    /// <summary>
    /// The clipboard belongs to the top level, which a view model does not have. Throws
    /// when there is none to reach — the caller logs that rather than pretending it copied.
    /// </summary>
    private Task CopyToClipboardAsync(string text) =>
        Clipboard?.SetValueAsync(DataFormat.Text, text)
        ?? throw new InvalidOperationException("no clipboard");

    /// <summary>
    /// True only if Publish was pressed. Dismissing the window any other way sends nothing.
    /// </summary>
    private Task<bool> ConfirmPublishAsync(ShareViewModel share) =>
        new ShareWindow { DataContext = share }.ShowDialog<bool>(this);

    /// <summary>True only if the update was applied. Any other dismissal takes nothing.</summary>
    private Task<bool> ConfirmPackUpdateAsync(PackUpdateViewModel update) =>
        new PackUpdateWindow { DataContext = update }.ShowDialog<bool>(this);

    /// <summary>
    /// True only if Add was pressed. Dismissing the window any other way adds nothing.
    /// </summary>
    private Task<bool> ConfirmImportAsync(ImportViewModel offer) =>
        new ImportWindow { DataContext = offer }.ShowDialog<bool>(this);

    private Task ShowPreferencesAsync(PreferencesViewModel preferences) =>
        new PreferencesWindow { DataContext = preferences }.ShowDialog(this);

    /// <summary>
    /// True only if the change was applied. Dismissing the dialog any other way — Cancel,
    /// the title bar — leaves the pack alone.
    /// </summary>
    private Task<bool> ConfirmVersionChangeAsync(VersionChangeViewModel change) =>
        new VersionChangeWindow { DataContext = change }.ShowDialog<bool>(this);

    private Task<bool> RunOptimumBuildAsync(OptimumBuildViewModel build) =>
        new OptimumBuildWindow { DataContext = build }.ShowDialog<bool>(this);

    private Task<bool> ConfirmAsync(ConfirmViewModel confirm) =>
        new ConfirmWindow { DataContext = confirm }.ShowDialog<bool>(this);

    /// <summary>
    /// Commits a settings field as focus leaves it.
    ///
    /// In code-behind for the same reason as the dropdown below: LostFocus is an event
    /// with no command to bind to. Losing focus is the commit point because the detail
    /// pane is rebuilt whenever the selected pack changes — so held edits were discarded
    /// by the act of clicking away from them, which is also the act that looks like
    /// finishing.
    /// </summary>
    private void OnSettingsFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MainViewModel { Detail: { } detail })
            detail.CommitSettings();
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
