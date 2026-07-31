using System;
using System.Threading.Tasks;
using Avalonia.Controls;
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

        if (DataContext is PreferencesViewModel vm) vm.Confirm = ConfirmAsync;
    }

    private Task<bool> ConfirmAsync(ConfirmViewModel confirm) =>
        new ConfirmWindow { DataContext = confirm }.ShowDialog<bool>(this);
}
