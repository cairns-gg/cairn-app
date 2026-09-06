using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cairn.App.ViewModels;

namespace Cairn.App.Views;

/// <summary>
/// Shows what publishing a pack would send, before it is sent. Closes with true only when
/// Publish was pressed, so dismissing the window any other way — Cancel, the title bar,
/// Escape — publishes nothing.
/// </summary>
public partial class ShareWindow : Window
{
    public ShareWindow()
    {
        InitializeComponent();
        UiScale.Attach(this);

        // Tunnelling and taking handled events too, for the reason MainWindow gives: the
        // focused control gets the key first on the bubbling pass, and the slug box or the
        // button itself may keep it. Neither is marked handled here, so nothing changes for
        // whoever was going to get it next — this only watches.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A key-up that never arrives — Cmd-Tab away with Shift down — would otherwise
        // leave the window offering to force until the next press.
        Deactivated += (_, _) => SetForce(false);
    }

    /// <summary>
    /// Tells the view model whether Shift is down, which is what turns Publish into Force
    /// publish on an unchanged pack — see <see cref="ShareViewModel.ForceHeld"/>. The view
    /// model has no keyboard, so the window keeps it told.
    /// </summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SetForce(true);
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SetForce(false);
    }

    private void SetForce(bool held)
    {
        if (DataContext is ShareViewModel vm) vm.ForceHeld = held;
    }

    private void OnPublish(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
