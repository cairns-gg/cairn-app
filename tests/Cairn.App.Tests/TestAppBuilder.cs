using Xunit;
using Avalonia;
using Avalonia.Headless;
using Cairn.App;
using Cairn.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// xunit v3 runs each test class as its own collection, in parallel. Avalonia's headless
// session cannot take that: tests are dispatched onto the one thread that owns the
// dispatcher, and EnsureIsolatedApplication builds an Application through
// AppBuilder.SetupUnsafe. Reached from the wrong thread, constructing the Compositor calls
// DefaultRenderLoop.Add, which verifies dispatcher access and throws.
//
// It surfaced as a "Test Case Cleanup Failure" naming a different test each run — whichever
// one was last in the class that lost the race — which is why it read as flakiness in
// ConfirmWindowTests rather than as a property of the suite.
//
// Disabling parallelism alone took it from one run in two to one in ten: collections then
// run one at a time, but each boundary still tears the session down and stands it back up,
// and that is where the race lives. Every class therefore shares one collection
// (AvaloniaTests.Collection), so the assembly crosses that boundary once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cairn.App.Tests;

/// <summary>
/// The single collection every Avalonia test class belongs to. Not a fixture — there is
/// no shared state to hand out — purely a way to keep the whole assembly inside one
/// headless session.
/// </summary>
public static class AvaloniaTests
{
    public const string Collection = "avalonia";
}

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
