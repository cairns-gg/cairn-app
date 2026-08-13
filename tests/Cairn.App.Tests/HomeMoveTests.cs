using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Moving Cairn's files from the Preferences window.
///
/// What is held here is the wiring, not the move: HomeMigration owns the rules and is tested
/// against real trees in Cairn.Core.Tests. A successful move deliberately cannot be driven
/// from here — it would need CAIRN_HOME unset, and these tests would then be copying, and
/// repointing, the developer's own ~/.cairn.
///
/// So this covers what the window is responsible for: that the button exists and is bound,
/// that a refusal arrives as text on the screen rather than as an exception, that choosing
/// nothing does nothing, and that nothing else that touches files can start while it runs.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class HomeMoveTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-move-ui-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public HomeMoveTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The view model the app really builds, reached the way the button reaches it — rather
    /// than hand-constructed here, which would freeze this test against a constructor that
    /// belongs to the app.
    /// </summary>
    private static PreferencesViewModel Model()
    {
        var main = new MainViewModel(new OfflineHandler());

        PreferencesViewModel? captured = null;
        main.OpenPreferences = p => { captured = p; return Task.CompletedTask; };
        main.ShowPreferencesCommand.Execute(null);

        Assert.NotNull(captured);
        return captured!;
    }

    private static PreferencesWindow ShowOverview(PreferencesViewModel model)
    {
        var window = new PreferencesWindow { DataContext = model };
        window.Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Overview");

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void The_move_button_is_beside_the_home_path()
    {
        // Where the number that makes somebody want it already is.
        var window = ShowOverview(Model());

        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Move…");

        Assert.NotNull(button);
        Assert.True(button!.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void The_hint_no_longer_tells_people_to_set_an_environment_variable()
    {
        // It did, which was the whole feature request: an environment variable does not
        // reach a Start-menu launch, so that advice never worked for the people who needed it.
        var window = ShowOverview(Model());

        var text = string.Join(" ", window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? ""));

        Assert.DoesNotContain("Set CAIRN_HOME", text);
    }

    [AvaloniaFact]
    public async Task Choosing_nothing_does_nothing()
    {
        var model = Model();
        model.PickFolder = () => Task.FromResult<string?>(null);

        await model.MoveHomeCommand.ExecuteAsync(null);

        Assert.Equal("", model.MoveAftermath);
        Assert.False(model.IsMovingHome);
    }

    [AvaloniaFact]
    public async Task A_refusal_is_shown_on_the_screen_rather_than_thrown()
    {
        // These tests run with CAIRN_HOME set, which is itself one of the refusals — the
        // pointer would be written and then ignored. Any of them arrives the same way: as
        // text where the user is looking, because choosing an unsuitable folder is an
        // ordinary thing to do and not an error.
        var model = Model();
        model.PickFolder = () => Task.FromResult<string?>(Path.Combine(_home, "elsewhere"));

        await model.MoveHomeCommand.ExecuteAsync(null);

        Assert.Contains("CAIRN_HOME", model.MoveAftermath);
        Assert.False(model.IsMovingHome);
    }

    [AvaloniaFact]
    public void Nothing_else_that_touches_files_can_start_mid_move()
    {
        // A sweep deleting game versions while a copy is reading them is not a case worth
        // having, so one flag gates all of them.
        var model = Model();

        Assert.True(model.NotCleaningUp);
        Assert.True(model.CleanUpCommand.CanExecute(null));

        model.IsMovingHome = true;

        Assert.False(model.NotCleaningUp);
        Assert.False(model.CleanUpCommand.CanExecute(null));
        Assert.False(model.MoveHomeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void A_refusal_leaves_the_home_path_alone()
    {
        // The failure worth guarding: reporting a new root that was never adopted.
        var model = Model();

        Assert.Equal(CairnPaths.Root, model.CairnHome);
        Assert.Equal(_home, model.CairnHome);
    }
}
