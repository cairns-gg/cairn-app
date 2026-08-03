using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cairn.App.ViewModels;
using Cairn.App.Views;
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

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window is built, so the first one opens at the chosen size rather
        // than snapping to it a frame later.
        UiScale.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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