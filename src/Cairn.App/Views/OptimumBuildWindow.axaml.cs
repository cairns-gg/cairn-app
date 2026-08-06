using Avalonia.Controls;
using Avalonia.Interactivity;
using Cairn.App.ViewModels;

namespace Cairn.App.Views;

/// <summary>
/// Runs the build and shows it happening.
///
/// Closes with true only when a client was actually installed, so the caller can point the
/// pack at it without re-checking. Stopping, failing and dismissing all mean no.
/// </summary>
public partial class OptimumBuildWindow : Window
{
    public OptimumBuildWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);

        // Started here rather than in the constructor of the view model so the first lines
        // of output have a window to arrive at; a build that begins before this is on
        // screen spends its first seconds reporting into nothing.
        Opened += async (_, _) =>
        {
            if (DataContext is OptimumBuildViewModel vm) await vm.StartAsync();
        };

        // A build left running behind a closed window would keep a decompiler going for ten
        // minutes with nothing able to stop it.
        Closing += (_, _) => (DataContext as OptimumBuildViewModel)?.Closing();
    }

    private void OnClose(object? sender, RoutedEventArgs e) =>
        Close(DataContext is OptimumBuildViewModel { Succeeded: true });
}
