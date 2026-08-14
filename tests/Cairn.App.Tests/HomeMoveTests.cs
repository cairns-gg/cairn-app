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
/// HomeMigration owns the rules and is tested against real trees in Cairn.Core.Tests; what
/// is held here is what the window is responsible for — the button, the refusals arriving as
/// text rather than exceptions, and one confirmation being the whole of it: the original is
/// gone when the move reports finished, not left as a second thing to press.
///
/// The sandbox moves the default root rather than overriding it with CAIRN_HOME, which is
/// what dev.sh does and for the same reason: CAIRN_HOME outranks the pointer file, so a suite
/// built on it could only ever watch this feature refuse itself. With the default moved, a
/// real move runs end to end here and lands in a temp directory.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class HomeMoveTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-move-ui-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previousHome = Environment.GetEnvironmentVariable("CAIRN_HOME");
    private readonly string? _previousDefault = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");

    /// <summary>
    /// Sandboxed by moving the default root rather than by overriding it, which is what
    /// dev.sh does and for the same reason: CAIRN_HOME outranks the pointer, so a suite built
    /// on it could only ever watch this feature refuse. With the default moved, a real move
    /// can be driven end to end and it lands in a temp directory.
    ///
    /// CAIRN_HOME is cleared as well, because the other classes in this collection set it
    /// and would otherwise decide the answer here.
    /// </summary>
    public HomeMoveTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previousHome);
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", _previousDefault);
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
    }

    [AvaloniaFact]
    public void CAIRN_HOME_disables_the_button_and_says_why_before_the_picker()
    {
        // The variable outranks the pointer, so a move from here would change nothing. It
        // used to be enabled — you chose a folder, waited for the dialog, and were told
        // afterwards.
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);

        var model = Model();
        var window = ShowOverview(model);

        Assert.True(model.HomeIsFromEnvironment);
        Assert.False(model.CanMoveHome);
        Assert.False(model.MoveHomeCommand.CanExecute(null));

        var note = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "EnvironmentNote");

        Assert.True(note.IsEffectivelyVisible);
        Assert.Contains("CAIRN_HOME", note.Text);
    }

    [AvaloniaFact]
    public void Without_CAIRN_HOME_the_button_is_live()
    {
        var model = Model();

        Assert.False(model.HomeIsFromEnvironment);
        Assert.True(model.CanMoveHome);
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
        // Choosing the directory Cairn is already using is an ordinary thing to do with a
        // folder picker, and arrives as text where the user is looking rather than as an
        // exception.
        var model = Model();
        model.PickFolder = () => Task.FromResult<string?>(_home);

        await model.MoveHomeCommand.ExecuteAsync(null);

        Assert.Contains("already where Cairn keeps its state", model.MoveAftermath);
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
    public void The_progress_bar_shows_only_while_moving()
    {
        // Copying gigabytes takes minutes. A line of text that changes every few seconds
        // does not read as something running.
        var model = Model();
        var window = ShowOverview(model);

        var bar = window.GetVisualDescendants().OfType<ProgressBar>()
            .Single(b => b.Name == "MoveProgressBar");

        // Effectively, not IsVisible: the bar sits inside the panel that is bound, so its
        // own property stays true while the panel above it is collapsed.
        Assert.False(bar.IsEffectivelyVisible);

        model.IsMovingHome = true;
        model.MovePercent = 42;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(42, bar.Value);
    }

    [AvaloniaFact]
    public void A_refusal_leaves_the_home_path_alone()
    {
        // The failure worth guarding: reporting a new root that was never adopted.
        var model = Model();

        Assert.Equal(CairnPaths.Root, model.CairnHome);
        Assert.Equal(_home, model.CairnHome);
    }

    [AvaloniaFact]
    public async Task A_move_driven_from_the_window_relocates_everything()
    {
        // The whole thing end to end, through the view model the button uses. Possible only
        // because the sandbox moves the default root instead of overriding it — the class
        // doc used to say a successful move could not be driven from here, and that was a
        // consequence of how the suite was set up rather than anything about the feature.
        // A real manifest, because the window lists packs on the way up and an id-less one
        // is refused by PackStore — the pack has to be the kind a move would actually carry.
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_home, "packs", "demo"));
        File.WriteAllText(Path.Combine(_home, "packs", "demo", "pack.json"),
            "{\"id\":\"demo\",\"name\":\"Demo\",\"gameVersion\":\"1.22.5\",\"mods\":[]}");

        var target = Path.Combine(
            Path.GetTempPath(), "cairn-move-to-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(target);

        var model = Model();
        model.PickFolder = () => Task.FromResult<string?>(target);

        try
        {
            await model.MoveHomeCommand.ExecuteAsync(null);

            // Arrived, and Cairn is looking at it.
            Assert.True(File.Exists(Path.Combine(target, "packs", "demo", "pack.json")));
            Assert.Equal(target, CairnPaths.Root);
            Assert.Equal(target, model.CairnHome);

            // One confirmation, one outcome: the original is gone, not left as a chore.
            Assert.False(File.Exists(Path.Combine(_home, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(_home, "packs")));
            Assert.Contains("removed the original", model.MoveAftermath);

            // Except the pointer, which lives in the old root and is what makes the new
            // location work — taking it would have undone the move.
            Assert.True(File.Exists(Path.Combine(_home, Cairn.Core.CairnHome.PointerName)));
            Assert.Equal(target, CairnPaths.Root);
        }
        finally
        {
            try { Directory.Delete(target, recursive: true); } catch (IOException) { }
        }
    }
}
