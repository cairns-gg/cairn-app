using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Escape closes a dialog, whichever dialog it is.
///
/// Most of them get this from <c>IsCancel</c> on their Cancel button, which is a property
/// nobody looks at again once it is typed — and four windows had been added over time
/// without it. Preferences could not have it at all: it is dismissed from the title bar and
/// has no button to hang it on.
///
/// Worth a test rather than an assumption because the failure is invisible to every other
/// kind of check. The window opens, the buttons work, the bindings resolve, and the only
/// symptom is that a key somebody pressed did nothing — which is how it survives review and
/// arrives as "the dialog is stuck".
///
/// The view models are built directly rather than through MainViewModel: each of those
/// starts a catalogue fetch and a poll timer, and enough of them in flight behind the
/// headless session the whole assembly shares trips the teardown race TestAppBuilder
/// describes.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class EscapeClosesTests
{
    /// <summary>
    /// Presses the key, having first checked the window was up.
    ///
    /// The check is not ceremony: without it "closed" and "never opened" are the same
    /// assertion, and a test that passes whatever the window does is worse than none — this
    /// one exists precisely because the failure it guards is invisible.
    /// </summary>
    private static void Escape(Window window)
    {
        Assert.True(window.IsVisible, "the window was not open to begin with");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static PackManifest Pack(params string[] mods) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    [AvaloniaFact]
    public void The_pack_update_dialog_closes()
    {
        var plan = PackUpdatePlan.Between(
            Pack("carryon"), Pack("carryon", "betterruins"), Pack("carryon"),
            fromRevision: 1, toRevision: 2);

        var window = new PackUpdateWindow
        {
            DataContext = new PackUpdateViewModel(plan, "Anego"),
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Escape(window);

        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void The_version_change_dialog_closes()
    {
        var plan = new VersionChangePlan(
            "1.22.5", "1.22.6",
            [new ModVerdict("carryon", "1.0.0", "2.0.0", ModOutcome.Moves, "1.0.0 → 2.0.0")],
            []);

        var window = new VersionChangeWindow
        {
            DataContext = new VersionChangeViewModel(plan),
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Escape(window);

        Assert.False(window.IsVisible);
    }

    /// <summary>
    /// The one that could not use <c>IsCancel</c>, and so the one this was really written
    /// for: it is opened modally over the launcher and had no way out but the title bar.
    /// </summary>
    [AvaloniaFact]
    public void The_preferences_window_closes()
    {
        var http = new HttpClient(new OfflineHandler());

        var model = new PreferencesViewModel(
            new GamesViewModel(http, new GameStore(), new RuntimeStore(), _ => { }, () => { }),
            new PackStore(), new GameStore(), new RuntimeStore(),
            new ModIconCache(http), new ModInfoCache(new ModDbClient(http)));

        var window = new PreferencesWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Escape(window);

        Assert.False(window.IsVisible);
    }
}
