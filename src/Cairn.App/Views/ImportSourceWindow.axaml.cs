using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cairn.App.ViewModels;
using Cairn.Core;

namespace Cairn.App.Views;

/// <summary>
/// Asks where a pack is coming from — an install already on this machine, a link, or text
/// somebody sent — and collects what that answer needs.
///
/// Closes with true only when the button was pressed, so Cancel, Escape and the title bar
/// all leave the library alone. Nothing here creates anything: the caller reads the choice
/// back off the view model and acts on it.
/// </summary>
public partial class ImportSourceWindow : Window
{
    public ImportSourceWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);
    }

    /// <summary>
    /// Reads the install as soon as the dialog is up, since that is the source it opens on.
    ///
    /// In code-behind because "the window appeared" is a view event with no command to bind
    /// to, which is what code-behind is for. Not awaited: the scan reports its own progress
    /// and the dialog is usable — and cancellable, by choosing another source — while it
    /// runs.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is ImportSourceViewModel { FromInstall: true } vm)
            vm.ScanCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is ImportSourceViewModel vm) vm.PickFolder = PickFolderAsync;
    }

    /// <summary>
    /// The platform's own folder chooser, opened at the install Cairn already found so
    /// somebody correcting the wrong one of two starts beside it rather than at whatever the
    /// picker last remembered.
    ///
    /// On macOS this cannot be used to select Vintagestory.app itself — a picker will not
    /// enter a bundle — so the folder holding it is what gets chosen, and
    /// <see cref="GameInstall.Choose"/> looks one level down.
    /// </summary>
    private async Task<string?> PickFolderAsync()
    {
        var current = (DataContext as ImportSourceViewModel)?.InstallDirectory;

        var start = current is not null && Directory.Exists(current)
            ? await StorageProvider.TryGetFolderFromPathAsync(current)
            : null;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Lang.Get("importsrc-install-choose"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        // TryGetLocalPath rather than the URI: a folder on a network share or in a sandbox
        // has no local path, and everything below this works in paths.
        return picked.Count == 0 ? null : picked[0].TryGetLocalPath();
    }

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
