using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cairn.App.ViewModels;

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

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
