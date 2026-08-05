using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// A followed pack shows its mods and, by default, nothing that changes them.
///
/// Not a rule — Core allows the edit and the CLI still makes it — only a statement about
/// which of two things is the default when the pack is somebody's curation. What is being
/// tested is that the default holds, that there is a way past it, and that the way back
/// depends on whether there is anything to undo.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class PackLockedModsTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-locked-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public PackLockedModsTests()
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

    /// <summary>An imported pack: a follower, with the author's list recorded as the base.</summary>
    private void Follow(params string[] mods)
    {
        var manifest = new PackManifest
        {
            Id = "anego",
            Name = "Anego Server",
            GameVersion = "1.22.5",
            Mods = [.. mods.Select(m => new PackMod { ModId = m })],
        };

        Store.Import(new PackBundle
        {
            Pack = manifest,
            CanonicalUrl = "https://cairns.gg/dizzyd/anego",
            Revision = 1,
        });
    }

    private (MainWindow Window, MainViewModel Vm) Open()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        vm.ConfirmImport = null;

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }

    private static Dictionary<string, Button> Buttons(Visual root) =>
        root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is string)
            .GroupBy(b => (string)b.Content!)
            .ToDictionary(g => g.Key, g => g.First());

    [AvaloniaFact]
    public void A_followed_pack_starts_locked()
    {
        Follow("carryon");

        var (window, vm) = Open();

        Assert.True(vm.Detail!.IsFollowing);
        Assert.True(vm.Detail.IsLocked);
        Assert.False(vm.Detail.CanEditMods);

        // The controls that alter somebody else's curation are the ones that go: adding,
        // removing, repinning, and moving mods to versions they did not choose.
        var buttons = Buttons(window);
        Assert.False(buttons["Search"].IsEffectivelyVisible);
        Assert.False(buttons["Check for mod updates"].IsEffectivelyVisible);
        Assert.True(buttons["Unlock"].IsEffectivelyVisible);

        // Not the way to take their updates, which is the whole point of following.
        Assert.True(buttons["Check for updates"].IsEffectivelyVisible);

        // And not the pack itself. A follower must be able to stop holding one, which is
        // declining to keep somebody's curation rather than editing it — it lives in the
        // Settings tab, which a TabControl does not realise until it is selected.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Settings");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(Buttons(window)["Delete pack"].IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_pack_of_your_own_is_never_locked()
    {
        // It is not "unlocked", it is simply yours — so it carries none of this and says
        // nothing about it.
        new PackManifest { Id = "anego", GameVersion = "1.22.5", Mods = [] }
            .Save(Store.ManifestPath("anego"));

        var (window, vm) = Open();

        Assert.False(vm.Detail!.IsLocked);
        Assert.True(vm.Detail.CanEditMods);
        Assert.False(vm.Detail.ShowUnlockedNote);
        Assert.False(Buttons(window)["Unlock"].IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Unlocking_brings_the_controls_back_and_survives_a_restart()
    {
        Follow("carryon");

        var (window, vm) = Open();
        vm.Detail!.UnlockModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Detail.IsLocked);
        Assert.True(Buttons(window)["Search"].IsEffectivelyVisible);
        Assert.True(Buttons(window)["Check for mod updates"].IsEffectivelyVisible);

        // Sticky, because unlocking to add one mod and being re-locked next launch would
        // be a nuisance rather than a safeguard.
        Assert.True(Store.LoadLocalState("anego").Unlocked);
    }

    [AvaloniaFact]
    public void Lock_again_is_offered_only_while_there_is_nothing_to_undo()
    {
        Follow("carryon");

        var (_, vm) = Open();
        vm.Detail!.UnlockModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Unlocked and unchanged: locking again is honest, because it provably undoes
        // nothing.
        Assert.True(vm.Detail.MatchesUpstream);
        Assert.True(vm.Detail.CanRelock);

        // Now actually diverge.
        vm.Detail.Manifest.Mods.Add(new PackMod { ModId = "myfavourite" });
        vm.Detail.Manifest.Save(Store.ManifestPath("anego"));
        vm.Detail.RefreshLock();

        // A relock here would leave the change in place while implying it had gone. The
        // way back is a reset, which says what it removes and what that costs a world.
        Assert.False(vm.Detail.MatchesUpstream);
        Assert.False(vm.Detail.CanRelock);
        Assert.False(vm.Detail.LockModsCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Locking_again_puts_the_controls_away()
    {
        Follow("carryon");

        var (window, vm) = Open();
        vm.Detail!.UnlockModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.Detail.LockModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Detail.IsLocked);
        Assert.False(Store.LoadLocalState("anego").Unlocked);
        Assert.False(Buttons(window)["Search"].IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void A_locked_row_shows_the_mod_and_no_way_to_change_it()
    {
        Follow("carryon");

        var (_, vm) = Open();

        var row = vm.Detail!.Mods.Single();
        Assert.Equal("carryon", row.ModId);

        // The pin dropdown and the remove are the two ways a row stops matching the
        // author's, so they are what a locked row does without.
        Assert.False(row.CanChange);

        vm.Detail.UnlockModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Detail.Mods.Single().CanChange);
    }
}
