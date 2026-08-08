using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Games.Optimum;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Offering to build the Optimum client.
///
/// This is the most expensive thing Cairn can be asked to do — a twenty-minute compile
/// rather than a download — so what is held here is that it is offered only where it would
/// work, that nothing starts without an explicit yes to the cost, and that a build which
/// does not finish leaves the pack alone.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class PackOptimumTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-optimum-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public PackOptimumTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private PackStore Store => new(Path.Combine(_home, "packs"));

    /// <summary>The version Optimum is actually for, so a bumped pin does not fail this.</summary>
    private static string Supported => OptimumSource.Pinned.GameVersion;

    private string Install(string name, string? variant = null)
    {
        var dir = Games.DirIn(Path.Combine(_home, "games"), name);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        if (variant is not null)
            File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), variant);

        return dir;
    }

    private (MainWindow Window, MainViewModel Main, PackDetailViewModel Detail) Open(
        string gameVersion)
    {
        new PackManifest { Id = "anego", Name = "Anego", GameVersion = gameVersion, Mods = [] }
            .Save(Store.ManifestPath("anego"));

        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        vm.ConfirmImport = null;
        vm.RunOptimumBuild = null;
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm, vm.Detail!);
    }

    [AvaloniaFact]
    public void It_is_offered_for_the_version_optimum_is_for()
    {
        var (_, _, detail) = Open(Supported);

        Assert.True(detail.CanBuildOptimum);
        Assert.Contains(OptimumSource.Pinned.Version, detail.BuildOptimumLabel);
    }

    [AvaloniaFact]
    public void It_is_not_offered_for_a_version_it_would_not_run()
    {
        // Optimum targets exactly one game version at a time. Offering it elsewhere is an
        // invitation to spend twenty minutes producing a client the pack cannot use.
        var (_, _, detail) = Open("1.20.0");

        Assert.False(detail.CanBuildOptimum);
    }

    [AvaloniaFact]
    public void It_is_withdrawn_once_a_variant_exists()
    {
        Install($"{Supported}-optimum", "Optimum");

        var (_, _, detail) = Open(Supported);

        // From here it is an install to pick, not a thing to make: a second build would
        // only overwrite the first.
        Assert.False(detail.CanBuildOptimum);
    }

    [AvaloniaFact]
    public void A_broken_install_can_still_be_rebuilt()
    {
        // A directory that is there but is not an install — a cancelled build, a deletion
        // half done. It reports no version, so it is in no picker; if that also hid the
        // button, nothing on screen could put it right.
        Directory.CreateDirectory(Games.DirIn(Path.Combine(_home, "games"), $"{Supported}-optimum"));

        var (_, _, detail) = Open(Supported);

        Assert.True(detail.CanBuildOptimum);
    }

    [AvaloniaFact]
    public void Changing_the_game_version_moves_the_button_at_once()
    {
        var (_, _, detail) = Open("1.20.0");

        var changed = new List<string>();
        detail.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        Assert.False(detail.CanBuildOptimum);

        detail.Manifest.GameVersion = Supported;
        detail.RefreshGameState();

        // Not merely correct when next asked: the pane stays on screen across a version
        // change, so a property nobody was told about reads as a button that does not
        // work until the pack is reselected.
        Assert.True(detail.CanBuildOptimum);
        Assert.Contains(nameof(detail.CanBuildOptimum), changed);
    }

    [AvaloniaFact]
    public void A_new_install_refreshes_what_the_pack_runs_with()
    {
        // Same staleness, the other direction: what a pack runs with is derived from the
        // library, and nothing told the view when that changed either.
        var (_, _, detail) = Open(Supported);

        var changed = new List<string>();
        detail.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        detail.RefreshGameState();

        Assert.Contains(nameof(detail.InstallChoiceLine), changed);
        Assert.Contains(nameof(detail.CanUseOptimum), changed);
        Assert.Contains(nameof(detail.IsUsingVariant), changed);
    }

    [AvaloniaFact]
    public void A_pending_version_change_disables_it_until_the_check_has_run()
    {
        var (window, _, detail) = Open(Supported);

        Assert.True(detail.CanBuildOptimumNow);

        // Through the picker's own list, because that is the only way it moves on screen:
        // the combo box is bound two-way, so a version it is not offering is written
        // straight back as nothing.
        detail.GameVersionChoices.Add("1.22.6");
        detail.TargetGameVersion = "1.22.6";

        // Still offered — the pack has not moved, and hiding the panel the moment somebody
        // touches the picker reads as a bug. Just not startable, because a build now would
        // be for the version the pack still targets rather than the one on screen.
        Assert.True(detail.CanBuildOptimum);
        Assert.False(detail.CanBuildOptimumNow);
        Assert.False(detail.BuildOptimumCommand.CanExecute(null));

        // And it says which version it would have been for, or a greyed button is a puzzle.
        Assert.Contains(Supported, detail.BuildOptimumBlockedNote);
        Assert.Contains("1.22.6", detail.BuildOptimumBlockedNote);
    }

    [AvaloniaFact]
    public void Returning_the_picker_to_the_packs_own_version_enables_it_again()
    {
        var (_, _, detail) = Open(Supported);

        detail.GameVersionChoices.Add("1.22.6");
        detail.TargetGameVersion = "1.22.6";
        Assert.False(detail.CanBuildOptimumNow);

        // Backing out of a version change is not a check, but it does settle the question.
        detail.TargetGameVersion = Supported;

        Assert.True(detail.CanBuildOptimumNow);
        Assert.Equal("", detail.BuildOptimumBlockedNote);
    }

    [AvaloniaFact]
    public void Applying_the_change_settles_it_the_other_way()
    {
        var (_, _, detail) = Open(Supported);

        detail.GameVersionChoices.Add("1.20.0");
        detail.TargetGameVersion = "1.20.0";
        Assert.False(detail.CanBuildOptimumNow);

        // Once the pack really is on a version Optimum is not for, the panel goes rather
        // than sitting there permanently disabled.
        detail.Manifest.GameVersion = "1.20.0";
        detail.RefreshGameState();

        Assert.False(detail.CanBuildOptimum);
        Assert.Equal("", detail.BuildOptimumBlockedNote);
    }

    [AvaloniaFact]
    public async Task Nothing_starts_without_a_yes_to_the_cost()
    {
        var (_, main, detail) = Open(Supported);

        var asked = false;
        var built = false;

        main.Confirm = c => { asked = true; return Task.FromResult(false); };
        main.RunOptimumBuild = _ => { built = true; return Task.FromResult(false); };

        await detail.BuildOptimumCommand.ExecuteAsync(null);

        Assert.True(asked);
        Assert.False(built);
    }

    [AvaloniaFact]
    public async Task The_warning_says_what_it_will_cost()
    {
        var (_, main, detail) = Open(Supported);

        ConfirmViewModel? shown = null;
        main.Confirm = c => { shown = c; return Task.FromResult(false); };
        main.RunOptimumBuild = _ => Task.FromResult(false);

        await detail.BuildOptimumCommand.ExecuteAsync(null);

        Assert.NotNull(shown);

        // Time and space both, because those are the two reasons somebody would say no,
        // and neither is guessable from a button that says "Build Optimum".
        Assert.Contains("minutes", shown.Message);
        Assert.Contains("GB", shown.Message);
    }

    [AvaloniaFact]
    public async Task A_build_that_does_not_finish_leaves_the_pack_alone()
    {
        var (_, main, detail) = Open(Supported);

        main.Confirm = _ => Task.FromResult(true);
        main.RunOptimumBuild = _ => Task.FromResult(false);   // cancelled, or failed

        await detail.BuildOptimumCommand.ExecuteAsync(null);

        // The pack must not end up pointed at something that was never built.
        Assert.Null(detail.ChosenInstall);
        Assert.Null(Store.LoadLocalState("anego").InstallDirectory);
    }

    [AvaloniaFact]
    public void A_built_client_can_be_switched_on_and_off_again()
    {
        Install($"{Supported}-optimum", "Optimum");

        var (_, _, detail) = Open(Supported);

        // Built but not in use: the panel offers using it rather than making it again.
        Assert.False(detail.CanBuildOptimum);
        Assert.True(detail.CanUseOptimum);
        Assert.False(detail.IsUsingVariant);

        detail.UseOptimumCommand.Execute(null);

        Assert.True(detail.IsUsingVariant);
        Assert.False(detail.CanUseOptimum);
        Assert.Contains("Optimum", detail.InstallChoiceLine);

        // And back. Without this, running a modified client would be a decision nothing on
        // screen could undo.
        detail.UseStockGameCommand.Execute(null);

        Assert.False(detail.IsUsingVariant);
        Assert.True(detail.CanUseOptimum);
        Assert.Null(Store.LoadLocalState("anego").InstallDirectory);
    }

    [AvaloniaFact]
    public void Retargeting_the_game_version_stops_a_chosen_variant_applying()
    {
        Install($"{Supported}-optimum", "Optimum");
        Install("1.22.4");

        var (_, _, detail) = Open(Supported);
        detail.UseOptimumCommand.Execute(null);
        Assert.True(detail.IsUsingVariant);

        // The pack moves to a version the build is not for. The choice is a directory and
        // the version is not fixed, so the two come apart — and the pack's mods were
        // resolved against the version it now targets, not the one the client is.
        detail.Manifest.GameVersion = "1.22.4";
        detail.RefreshGameState();

        Assert.False(detail.IsUsingVariant);
        Assert.Null(detail.ChosenInstall);

        // Said out loud, naming both sides: the fix is either to retarget back or to build
        // this version, and which one is not guessable from silence.
        Assert.Contains("1.22.4", detail.InstallChoiceLine);
        Assert.Contains(Supported, detail.InstallChoiceLine);

        // Ignored rather than erased: going back picks it up again, so trying another
        // version for a minute does not throw away a twenty-minute build.
        Assert.Equal(
            Games.DirIn(Path.Combine(_home, "games"), $"{Supported}-optimum"),
            Store.LoadLocalState("anego").InstallDirectory);

        detail.Manifest.GameVersion = Supported;
        detail.RefreshGameState();

        Assert.True(detail.IsUsingVariant);
    }

    [AvaloniaFact]
    public async Task A_version_check_that_fails_outright_puts_the_picker_back()
    {
        var (_, _, detail) = Open(Supported);

        // A lockfile that will not parse: not the offline case — failing to reach ModDB is
        // a verdict on the mods rather than an exception — but the unforeseen one the
        // catch-all exists for. Written after the pack, and read when the check runs.
        File.WriteAllText(Store.LockPath("anego"), "{ not json");

        detail.GameVersionChoices.Add("1.22.6");
        detail.TargetGameVersion = "1.22.6";

        for (var i = 0; i < 200 && detail.HasPendingGameVersion; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // There is no Check button to press again, and picking the same entry twice raises
        // no change to retry from — so a failure that left the target showing would strand
        // the pane on a version the pack is not on and cannot be moved to.
        Assert.Equal(Supported, detail.TargetGameVersion);
        Assert.False(detail.HasPendingGameVersion);
        Assert.True(detail.HasError);

        // And the button it was blocking comes back with it.
        Assert.True(detail.CanBuildOptimumNow);
    }

    [AvaloniaFact]
    public void The_panel_is_absent_where_optimum_does_not_apply()
    {
        // Most packs. An advanced option that is simply not there beats one that is there
        // and does nothing.
        var (_, _, detail) = Open("1.20.0");

        Assert.False(detail.HasOptimumPanel);
    }

    [AvaloniaFact]
    public void The_button_is_on_screen_when_it_is_offered()
    {
        var (window, _, _) = Open(Supported);

        // It lives in the Settings tab, which a TabControl does not realise until it is
        // selected. Asserted on the visual tree because Avalonia resolves bindings at
        // runtime — a wrong path fails silently and the button simply never appears.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Settings");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => b.Name == "BuildOptimum");

        Assert.NotNull(button);
        Assert.True(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_check_button_gives_way_to_its_progress_line()
    {
        var (_, _, detail) = Open(Supported);

        Assert.True(detail.ShowModUpdateCheck);

        detail.CheckingUpdates = true;

        // Hidden rather than greyed: the line beside it says what is happening, and a
        // disabled button next to "checking carryon… (3 of 28)" is a control asking to be
        // pressed and refusing.
        Assert.False(detail.ShowModUpdateCheck);

        // The command guards itself regardless — a hidden button is a courtesy, not a rule,
        // and a second check would double the requests for the same answer.
        Assert.False(detail.CheckUpdatesCommand.CanExecute(null));

        detail.CheckingUpdates = false;

        Assert.True(detail.ShowModUpdateCheck);
        Assert.True(detail.CheckUpdatesCommand.CanExecute(null));
    }
}
