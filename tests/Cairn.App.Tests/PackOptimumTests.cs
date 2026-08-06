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
        var dir = Path.Combine(_home, "games", name);
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
        Directory.CreateDirectory(Path.Combine(_home, "games", $"{Supported}-optimum"));

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
    public void A_new_install_refreshes_the_picker_too()
    {
        // Same staleness, the other direction: the install choices are derived from the
        // library, and nothing told the view when that changed either.
        var (_, _, detail) = Open(Supported);

        var changed = new List<string>();
        detail.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        detail.RefreshGameState();

        Assert.Contains(nameof(detail.InstallChoices), changed);
        Assert.Contains(nameof(detail.HasInstallChoice), changed);
        Assert.Contains(nameof(detail.SelectedInstall), changed);
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
    public void The_picker_offers_the_same_objects_it_reports_as_selected()
    {
        Install($"{Supported}-optimum", "Optimum");
        Install(Supported);

        var (_, _, detail) = Open(Supported);

        // GameInstall compares by reference, and this list used to be rebuilt from disk on
        // every read — so the install SelectedInstall returned was never one the picker was
        // holding, and the box rendered blank with entries in it.
        Assert.Same(detail.InstallChoices, detail.InstallChoices);

        if (detail.SelectedInstall is { } selected)
            Assert.Contains(selected, detail.InstallChoices);
    }

    [AvaloniaFact]
    public void Refreshing_rebuilds_the_choices_rather_than_serving_a_stale_list()
    {
        var (_, _, detail) = Open(Supported);

        var before = detail.InstallChoices;
        detail.RefreshGameState();

        // Cached, but only until something could have changed it — otherwise an install
        // that just finished downloading would never appear.
        Assert.NotSame(before, detail.InstallChoices);
    }

    [AvaloniaFact]
    public void An_install_describes_itself_for_the_picker()
    {
        Install($"{Supported}-optimum", "Optimum");

        var (_, _, detail) = Open(Supported);

        // Bound by the view. As a method it could not be bound at all, and every row
        // rendered as "Cairn.Core.GameInstall".
        foreach (var install in detail.InstallChoices)
            Assert.False(string.IsNullOrWhiteSpace(install.Describe));
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
}
