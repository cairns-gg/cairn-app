using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Cairn.Core;
using Cairn.App.ViewModels;

namespace Cairn.App.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    /// <summary>
    /// Escape closes this, as it closes every other dialog Cairn opens.
    ///
    /// Handled here rather than with <c>IsCancel</c> on a button, which is how the rest of
    /// them do it, because this window has no button to put it on: it is dismissed from the
    /// title bar and nothing else. A window that is opened modally and cannot be dismissed
    /// by the key every other modal answers to is the one that gets reported as stuck.
    ///
    /// Not handled when something else has already taken the key — a combo box with its list
    /// down closes the list on Escape, and swallowing that would shut the window instead.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }

    /// <summary>
    /// Confirms against this window rather than the main one.
    ///
    /// The view model is handed a confirmer by whoever built it, and MainWindow's is
    /// parented to MainWindow — so accepting a prompt raised from here dismissed
    /// Preferences and brought the main window forward mid-operation. A dialog belongs to
    /// the window it was opened from.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not PreferencesViewModel vm) return;

        vm.Confirm = ConfirmAsync;
        vm.PickFolder = PickFolderAsync;
    }

    private Task<bool> ConfirmAsync(ConfirmViewModel confirm) =>
        new ConfirmWindow { DataContext = confirm }.ShowDialog<bool>(this);

    /// <summary>
    /// The platform's own folder chooser, which is the only one that can reach a disk the
    /// user has just plugged in and knows the name of.
    ///
    /// Suggested starting point is where Cairn currently is, so the picker opens somewhere
    /// meaningful rather than at whatever it last remembered — but only if it is still
    /// there, since this same screen is reachable after choosing the default over a
    /// disconnected disk.
    /// </summary>
    private async Task<string?> PickFolderAsync()
    {
        var start = Directory.Exists(CairnPaths.Root)
            ? await StorageProvider.TryGetFolderFromPathAsync(CairnPaths.Root)
            : null;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should Cairn keep its files?",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        // TryGetLocalPath rather than the URI: a folder on a network share or in a sandbox
        // has no local path, and everything below this works in paths.
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }
}
