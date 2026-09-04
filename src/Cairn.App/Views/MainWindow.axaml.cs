using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.Core;

namespace Cairn.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);

        // Tunnelling, and taking handled events too. Overriding OnKeyDown looks like the
        // same thing and is not: that is a class handler on the bubbling pass, which runs
        // after the focused control has had the key and only if it did not take it. Capture
        // starts from a click on a button, so the button holds focus — and a button eats
        // Space and Enter. Those two were unbindable, and the row sat on "Press a key…"
        // for ever, because the press it was waiting for never left the button.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
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
        vm.ChooseImportSource = ChooseImportSourceAsync;
        vm.ChooseWorlds = ChooseWorldsAsync;
        vm.Confirm = ConfirmAsync;
        vm.RunOptimumBuild = RunOptimumBuildAsync;
        vm.PickClientFolder = PickClientFolderAsync;
        vm.ChoosePinnedVersion = ChoosePinnedVersionAsync;
        vm.CopyToClipboard = CopyToClipboardAsync;
    }

    /// <summary>
    /// Feeds a keypress to a hotkey row that is waiting for one.
    ///
    /// Here rather than on the row, because a view model has no keyboard: the press arrives
    /// at the window, and the window is also the only thing that can stop it going on to be
    /// somebody's shortcut. Registered to tunnel from the top — see the constructor — so a
    /// key a focused control would otherwise swallow, Space on a button or Tab moving focus,
    /// still reaches the binding somebody is in the middle of setting.
    ///
    /// Every branch marks the event handled, which on the tunnelling pass means nothing
    /// below sees it at all. That is the point: while a row is waiting, the keyboard belongs
    /// to it and to nothing else.
    /// </summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { Detail: { CapturingRow: not null } detail }) return;

        // A modifier on its own is the first half of a combination, not a binding.
        if (KeyCodes.IsModifier(e.Key)) { e.Handled = true; return; }

        if (KeyCodes.Of(e.Key) is { } code)
        {
            var modifiers = e.KeyModifiers;

            detail.CaptureHotkey(
                code,
                ctrl: modifiers.HasFlag(KeyModifiers.Control),
                alt: modifiers.HasFlag(KeyModifiers.Alt),
                shift: modifiers.HasFlag(KeyModifiers.Shift));

            e.Handled = true;
            return;
        }

        // A key the game cannot name binds nothing, and giving up on the capture is
        // better than leaving the row waiting for a key that will never work.
        detail.CancelHotkeyCapture();
        e.Handled = true;
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

    /// <summary>
    /// True only if the button was pressed. Dismissing it any other way imports nothing —
    /// including from an install, where the scan has already run and produced a plan.
    /// </summary>
    private Task<bool> ChooseImportSourceAsync(ImportSourceViewModel choice) =>
        new ImportSourceWindow { DataContext = choice }.ShowDialog<bool>(this);

    /// <summary>True only if Copy was pressed. Somebody's saves are not copied on a dismissal.</summary>
    private Task<bool> ChooseWorldsAsync(WorldPickerViewModel picker) =>
        new WorldImportWindow { DataContext = picker }.ShowDialog<bool>(this);

    private Task ShowPreferencesAsync(PreferencesViewModel preferences) =>
        new PreferencesWindow { DataContext = preferences }.ShowDialog(this);

    /// <summary>
    /// True only if the change was applied. Dismissing the dialog any other way — Cancel,
    /// the title bar — leaves the pack alone.
    /// </summary>
    private Task<bool> ConfirmVersionChangeAsync(VersionChangeViewModel change) =>
        new VersionChangeWindow { DataContext = change }.ShowDialog<bool>(this);

    private Task<bool> ChoosePinnedVersionAsync(PinVersionViewModel choice) =>
        new PinVersionWindow { DataContext = choice }.ShowDialog<bool>(this);

    private Task<bool> RunOptimumBuildAsync(OptimumBuildViewModel build) =>
        new OptimumBuildWindow { DataContext = build }.ShowDialog<bool>(this);

    private Task<bool> ConfirmAsync(ConfirmViewModel confirm) =>
        new ConfirmWindow { DataContext = confirm }.ShowDialog<bool>(this);

    /// <summary>
    /// The platform's own folder chooser, opened beside the last client somebody pointed
    /// Cairn at so re-pointing after a rebuild starts where the last one was.
    ///
    /// On macOS this cannot be used to select a <c>.app</c> — a picker will not enter a
    /// bundle — so the folder holding it is what gets chosen, and ClientAdoption looks one
    /// level down. Same arrangement as the install picker in the import window.
    /// </summary>
    private async Task<string?> PickClientFolderAsync()
    {
        var last = (DataContext as MainViewModel)?.LastClientFolder;

        var start = last is not null && Directory.Exists(last)
            ? await StorageProvider.TryGetFolderFromPathAsync(last)
            : null;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Lang.Get("adopt-choose"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        // TryGetLocalPath rather than the URI: a folder on a network share or in a sandbox
        // has no local path, and everything below this works in paths.
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }

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

    /// <summary>
    /// Double-clicking a mod config row shows that row's file in the file manager — the
    /// same bargain the mod list makes above, and the only way to a file kept in a subfolder
    /// of ModConfig without going hunting through the folder for it.
    /// </summary>
    private void OnModConfigRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The row's tick belongs to the tick. A CheckBox is a Button, so this is the same
        // guard as the mod list's.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors()
                .TakeWhile(v => v != sender)
                .Any(v => v is Button or ComboBox))
            return;

        if ((sender as Control)?.DataContext is ModConfigRowViewModel row
            && (DataContext as MainViewModel)?.Detail is { } detail)
            detail.RevealModConfigFile(row);
    }
}
