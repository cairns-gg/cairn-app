using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Packs;

namespace Cairn.App;

public partial class App : Application
{
    private MainViewModel? _model;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// The macOS About item. Opens Preferences, which is where the version is — rather
    /// than a window of its own that would exist to say one line.
    /// </summary>
    private void OnAbout(object? sender, EventArgs e) =>
        _model?.ShowPreferencesCommand.Execute(null);

    /// <summary>
    /// Puts the problem in front of the user and reports whether to carry on regardless.
    ///
    /// Shown with ShowDialog against no owner and pumped by its own lifetime, because this
    /// runs before there is a main window to own anything. Choosing the default clears the
    /// pointer, so the resolution that follows finds ~/.cairn — no restart needed, since the
    /// root is worked out on every read rather than once at startup.
    /// </summary>
    private static bool AcceptedDefault(IClassicDesktopStyleApplicationLifetime desktop, string problem)
    {
        var window = new HomeProblemWindow
        {
            DataContext = new HomeProblemViewModel(problem, CairnHome.Resolve().Root),
        };

        // The window is the application until it closes: without this the lifetime has no
        // main window, so nothing pumps and the dialog never appears.
        desktop.MainWindow = window;
        window.ShowDialog(window);

        if (!window.UseDefault) return false;

        CairnHome.SetPointer(null);
        return true;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window is built, so the first one opens at the chosen size rather
        // than snapping to it a frame later.
        UiScale.Load();

        // And in the right language, for the same reason: a window built before this would
        // open in English and rebind a frame later.
        var (language, _) = LanguageChoice.Resolve(CairnSettings.Load().Language);
        Lang.Use(language, LanguageChoice.OverrideDir);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Before the model, which reads packs the moment it is built. If the setting
            // names a directory that is not there — an unplugged disk, a share that is down —
            // starting anyway would show an empty launcher, and an empty launcher does not
            // read as "that disk is not connected". It reads as "everything is gone", and
            // the next thing offered is downloading the game again beside data that is fine.
            //
            // cairn-cli refuses the same way. It can print a line and exit; here it takes a
            // window, because there is nowhere else to say it.
            if (CairnHome.Preflight() is { } problem && !AcceptedDefault(desktop, problem)) return;

            var model = new MainViewModel();
            _model = model;

            desktop.MainWindow = new MainWindow { DataContext = model };

            // Both routes a cairn:// link can arrive by — see PackLinks.
            PackLinks.Listen(this, model);

            // And the half that is not about receiving one: telling Windows and Linux that
            // the scheme is ours at all. Off the startup path because it shells out to
            // desktop-integration helpers on Linux, and the window opening in 38 ms is
            // worth more than registering a link a few milliseconds sooner.
            //
            // A thread of its own rather than the pool: this waits on child processes, and
            // blocking a pool thread for that is what the pool is not for.
            new Thread(PackLinkHandler.Register)
            {
                IsBackground = true,
                Name = "cairn-url-handler",
            }.Start();

            if (PackLinks.FromArguments(desktop.Args ?? []) is { } link)
                PackLinks.Follow(this, model, link);

            // After the window exists, so the dialog has an owner and something to open in
            // front of. Not awaited: on most launches it decides in microseconds that a
            // check is not due, and on the rest it must not hold up the window.
            //
            // And it keeps checking. A launcher is left open — it is the thing you press
            // Play from — so a startup-only check would miss every release that happened
            // while it was running.
            model.StartUpdateChecks();
        }

        base.OnFrameworkInitializationCompleted();
    }
}