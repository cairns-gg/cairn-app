using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cairn.App.ViewModels;
using Cairn.Core.Packs;

namespace Cairn.App;

/// <summary>
/// Wires up "Open in Cairn" on a pack page.
///
/// The link reaches us by two different routes, and both are needed. macOS hands a running
/// app the URL through an activation event, because it reuses the instance already open.
/// Windows and Linux launch the handler afresh with the URL as an argument, so on those it
/// arrives in <c>argv</c> — including when Cairn is already running, which starts a second
/// copy. Handling only the event would do nothing off macOS; handling only the argument
/// would miss every click while the app is open on macOS.
/// </summary>
public static class PackLinks
{
    /// <summary>
    /// A link passed on the command line, if there is one.
    ///
    /// Scanning rather than taking args[0] because the OS is not the only caller — a
    /// person may well type other arguments, and on macOS the system appends its own.
    /// </summary>
    public static string? FromArguments(IEnumerable<string> args) =>
        args.FirstOrDefault(a => PackUri.TryGetDocumentUrl(a, out _));

    /// <summary>
    /// Subscribes to activation, where the platform supports it. Desktop lifetimes do not
    /// implement this themselves — it comes from the windowing backend, and is absent on
    /// the platforms that deliver links as arguments instead.
    /// </summary>
    public static void Listen(Application app, MainViewModel model)
    {
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime activatable)
            return;

        activatable.Activated += (_, e) =>
        {
            if (e is not ProtocolActivatedEventArgs protocol) return;
            if (e.Kind != ActivationKind.OpenUri) return;

            Follow(app, model, protocol.Uri.ToString());
        };
    }

    /// <summary>
    /// Shows the pack the link names, and brings the window forward — a click in a browser
    /// that quietly filled in a field behind another window would look like nothing
    /// happened.
    /// </summary>
    public static void Follow(Application app, MainViewModel model, string link) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!model.FollowLink(link))
            {
                Trace($"refused {link}");
                return;
            }

            Trace($"opened {link}");

            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                { MainWindow: { } window })
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
            }
        });

    /// <summary>
    /// The only trace of a link there is.
    ///
    /// "I clicked the button and nothing happened" has three quite different causes — the
    /// OS handed the link to nobody, it reached us and was refused, or it worked and the
    /// window is behind something. Silence looks identical in all three. One line
    /// separates them, and on macOS it can be read back with
    /// <c>open --stdout /tmp/cairn.log</c>.
    /// </summary>
    private static void Trace(string what)
    {
        try { Console.Error.WriteLine($"cairn: link {what}"); }
        catch (IOException) { /* no console; nothing worth failing a click over */ }
    }
}
