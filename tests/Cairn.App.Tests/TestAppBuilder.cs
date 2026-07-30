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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            // Real drawing rather than a stub, so tests can capture actual frames and
            // assert on what was rendered.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
