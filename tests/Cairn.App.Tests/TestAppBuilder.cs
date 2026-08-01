using Avalonia;
using Avalonia.Headless;
using Cairn.App;
using Cairn.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Cairn.App.Tests;

/// <summary>
/// Boots the real App class on Avalonia's headless platform so tests exercise the
/// same styles and templates the shipped launcher uses.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Point CAIRN_HOME at somewhere empty before the App class exists.
        // OnFrameworkInitializationCompleted calls UiScale.Load(), once, ahead of every
        // test constructor — so without this the suite reads the developer's real
        // ~/.cairn/settings.json. An interface scale of 125% saved there put every headless
        // window at 1.25, which made the toolbar geometry tests fail on that machine and
        // pass everywhere else.
        Environment.SetEnvironmentVariable(
            "CAIRN_HOME",
            Path.Combine(Path.GetTempPath(), "cairn-session-" + Guid.NewGuid().ToString("n")[..8]));

        return AppBuilder.Configure<App>()
            .UseSkia()
            // Real drawing rather than a stub, so tests can capture actual frames and
            // assert on what was rendered.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
