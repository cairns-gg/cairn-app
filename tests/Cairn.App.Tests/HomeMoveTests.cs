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
    private static PreferencesViewModel Model() => Windows().Preferences;

    /// <summary>
    /// Both halves, for the tests that care what the launcher behind the dialog does with a
    /// move — the pack pane is drawn from the root and does not close when Preferences does.
    /// </summary>
    private static (MainViewModel Main, PreferencesViewModel Preferences) Windows()
    {
        var main = new MainViewModel(new OfflineHandler());

        PreferencesViewModel? captured = null;
        main.OpenPreferences = p => { captured = p; return Task.CompletedTask; };
        main.ShowPreferencesCommand.Execute(null);

        Assert.NotNull(captured);
        return (main, captured!);
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

    private static Button Find(PreferencesWindow window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    /// <summary>Something at the root, so the button is about moving it.</summary>
    private void SomethingToMove()
    {
        Directory.CreateDirectory(CairnPaths.PacksRoot);
        File.WriteAllText(Path.Combine(CairnPaths.PacksRoot, "something.json"), "{}");
    }

    [AvaloniaFact]
    public void The_move_button_is_beside_the_home_path()
    {
        // Where the number that makes somebody want it already is.
        SomethingToMove();

        var window = ShowOverview(Model());

        Assert.Equal("Move…", Find(window, "MoveHomeButton").Content);
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

    /// <summary>
    /// A pack for the move to carry, and for the window to draw. A real manifest rather than
    /// an empty directory: the window lists packs on the way up and PackStore refuses an
    /// id-less one, so it has to be the kind a move would actually pick up.
    /// </summary>
    private string APack(string id = "demo")
    {
        Directory.CreateDirectory(Path.Combine(_home, "packs", id));
        File.WriteAllText(Path.Combine(_home, "packs", id, "pack.json"),
            $"{{\"id\":\"{id}\",\"name\":\"Demo\",\"gameVersion\":\"1.22.5\",\"mods\":[]}}");
        return id;
    }

    [AvaloniaFact]
    public async Task The_launcher_behind_the_dialog_moves_with_it()
    {
        // The bug this was reported as: everything arrived at the new root and the launcher
        // went on showing the old one — the pack pane naming a directory on the disk just
        // moved off, a new pack offered the same, and Play re-downloading every mod into the
        // tree the move had emptied. It came right on the next start, which is what said the
        // files were fine and the window was drawn from paths read at start-up.
        //
        // Asserted on the screen rather than on the view model, because the view model was
        // never the half that was wrong: its paths read through CairnPaths and answer for
        // the new root the moment the pointer moves. What lasted until a restart is the text
        // already drawn, which only changes if something says the pane is stale.
        APack();

        var target = Path.Combine(
            Path.GetTempPath(), "cairn-move-to-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(target);

        var main = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = main };
        window.Show();

        // A TabControl only realises the tab it is showing, and the paths live in Settings.
        // Selected again after the move, because rebuilding the pane from the new root
        // rebuilds the tabs with it and leaves the first one showing.
        void ShowSettingsTab()
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
                .Single(t => (t.Header as string) == "Settings");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        ShowSettingsTab();

        string Paths() => string.Join(" ", window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").Where(t => t.Contains("packs")));

        Assert.Contains(_home, Paths());

        PreferencesViewModel? preferences = null;
        main.OpenPreferences = p => { preferences = p; return Task.CompletedTask; };
        main.ShowPreferencesCommand.Execute(null);

        Assert.NotNull(preferences);
        preferences!.PickFolder = () => Task.FromResult<string?>(target);

        try
        {
            await preferences.MoveHomeCommand.ExecuteAsync(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // The pack is still selected and still the same pack: it is where it lives that
            // changed, and rebuilding the pane must not lose the selection on the way.
            Assert.Equal("demo", main.SelectedPack?.Id);

            ShowSettingsTab();

            var shown = Paths();
            Assert.Contains(target, shown);
            Assert.DoesNotContain(_home, shown);
        }
        finally
        {
            try { Directory.Delete(target, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_move_driven_from_the_window_relocates_everything()
    {
        // The whole thing end to end, through the view model the button uses. Possible only
        // because the sandbox moves the default root instead of overriding it — the class
        // doc used to say a successful move could not be driven from here, and that was a
        // consequence of how the suite was set up rather than anything about the feature.
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{}");
        APack();

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

    // ---- saying where before there is anything to move ----

    /// <summary>
    /// The button says which of the two things it will do.
    ///
    /// Nothing creates the root until Cairn writes something, so a fresh install has none —
    /// and "Move…" over a directory that is not there promises a copy that is not going to
    /// happen, above a confirmation offering to copy 0 files and 0 bytes.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_at_the_root_the_button_offers_to_choose_rather_than_move()
    {
        var window = ShowOverview(Model());

        Assert.Equal("Choose…", Find(window, "MoveHomeButton").Content);
    }

    [AvaloniaFact]
    public void And_says_move_once_there_is_something_to_move()
    {
        SomethingToMove();

        var window = ShowOverview(Model());

        Assert.Equal("Move…", Find(window, "MoveHomeButton").Content);
    }

    /// <summary>
    /// The whole of it, on a machine that has never run Cairn: the folder is made, the root
    /// moves there, and the default is left holding the one line that says so.
    ///
    /// That last part is the one worth pinning. DeleteOldRoot runs after the repoint, and
    /// the repoint has just created the default directory in order to write the pointer into
    /// it — so the tidy-up is walking a directory whose only occupant is the file that makes
    /// the move findable. Taking it would send Cairn back to a default root that is now
    /// empty: the move undone by its own housekeeping.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_home_on_a_fresh_install_leaves_the_pointer_behind()
    {
        var chosen = Path.Combine(_home, "..", "chosen-" + Guid.NewGuid().ToString("n")[..8]);
        chosen = Path.GetFullPath(chosen);
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);

        // Genuinely absent, which is what a fresh install looks like: this class makes the
        // sandbox root in its constructor, and nothing else does — Cairn creates it only
        // when it first writes something.
        Directory.Delete(_home, recursive: true);
        Assert.False(Directory.Exists(CairnPaths.Root));

        var model = Model();
        model.PickFolder = () => Task.FromResult<string?>(chosen);
        model.Confirm = _ => Task.FromResult(true);

        await model.MoveHomeCommand.ExecuteAsync(null);

        Assert.Equal(chosen, CairnPaths.Root);

        var pointer = Path.Combine(_home, CairnHome.PointerName);
        Assert.True(File.Exists(pointer), "the default should hold the pointer");
        Assert.Equal(chosen, File.ReadAllText(pointer).Trim());

        // And nothing else: the directory exists to carry that one line.
        Assert.Equal([pointer], Directory.EnumerateFileSystemEntries(_home));

        Directory.Delete(chosen, recursive: true);
    }
}
