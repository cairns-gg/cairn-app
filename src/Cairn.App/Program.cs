using Avalonia;
using System;
using System.Threading.Tasks;
using Cairn.Core;

namespace Cairn.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // First, so a crash anywhere after this leaves a line saying what it was. Without it
        // the launcher simply vanishes, and the only witness is a console nobody launched it
        // from. Plain .NET, so safe before Avalonia exists; the dispatcher's own hook waits
        // for App, which is the first point it can be reached.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CairnLog.Write($"crashed: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CairnLog.Error("unobserved task failure", e.Exception);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
