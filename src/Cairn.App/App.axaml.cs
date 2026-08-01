using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cairn.App.ViewModels;
using Cairn.App.Views;

namespace Cairn.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window is built, so the first one opens at the chosen size rather
        // than snapping to it a frame later.
        UiScale.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new MainViewModel();

            desktop.MainWindow = new MainWindow { DataContext = model };

            // Both routes a cairn:// link can arrive by — see PackLinks.
            PackLinks.Listen(this, model);

            if (PackLinks.FromArguments(desktop.Args ?? []) is { } link)
                PackLinks.Follow(this, model, link);
        }

        base.OnFrameworkInitializationCompleted();
    }
}