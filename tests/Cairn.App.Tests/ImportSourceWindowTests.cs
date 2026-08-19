using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.ModDb;
using Cairn.Core.Runtime;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The dialog that asks where a pack is coming from.
///
/// The window is rendered rather than the view model poked, because Avalonia resolves
/// bindings at runtime: a renamed property leaves the control blank and the test green. What
/// these check is that each of the three ways in shows its own body and nothing else's, and
/// that the button says what pressing it will do.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ImportSourceWindowTests
{
    private static ImportSourceViewModel Choice(
        string? modsDir = null, string? playedOn = "1.22.6", string gameVersion = "1.22.6",
        string? savesDir = null) =>
        new(new InstallImport(new ModDbClient(new HttpClient(new OfflineHandler()))),
            modsDir ?? Path.Combine(Path.GetTempPath(), "cairn-no-such-install", "Mods"),
            savesDir ?? Path.Combine(Path.GetTempPath(), "cairn-no-such-install", "Saves"),
            new HashSet<string>(),
            playedOn is null ? null : Install(playedOn),
            gameVersion,
            suggestId: name => (name ?? "").ToLowerInvariant().Replace(' ', '-'));

    /// <summary>
    /// An install the dialog can read a version off. Not a real one on disk — nothing here
    /// launches it, and what the dialog wants from an install is the version it reports.
    /// </summary>
    private static GameInstall Install(string version) => new()
    {
        Directory = Path.Combine(Path.GetTempPath(), "cairn-no-such-install"),
        Executable = Path.Combine(Path.GetTempPath(), "cairn-no-such-install", "Vintagestory"),
        Version = version,
        Architecture = ExecutableArch.X64,
        RequiredFramework = new Version(10, 0, 0),
    };

    private static (ImportSourceWindow Window, ImportSourceViewModel Vm) Show(
        ImportSourceViewModel? vm = null)
    {
        vm ??= Choice();
        var window = new ImportSourceWindow { DataContext = vm };
        window.Show();

        return (window, vm);
    }

    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!);

    private static IEnumerable<TextBox> VisibleBoxes(Visual root) =>
        root.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible);

    [AvaloniaFact]
    public void All_three_ways_in_are_offered_at_once()
    {
        var (window, _) = Show();

        var offered = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .Select(r => r.Content as string)
            .ToList();

        Assert.Contains("From your Vintage Story install", offered);
        Assert.Contains("From a link", offered);
        Assert.Contains("From pasted text or a file", offered);
    }

    [AvaloniaFact]
    public void It_opens_on_the_install_because_that_is_what_most_people_have()
    {
        var (window, vm) = Show();

        Assert.Equal(ImportSource.Install, vm.Source);
        Assert.Contains(VisibleText(window), t => t.Contains("mods you already have"));

        // And says which folder it will read, since that is the claim being made.
        Assert.Contains(VisibleText(window), t => t.Contains(vm.ModsDir));
    }

    /// <summary>
    /// An empty bordered scroll area is indistinguishable from a text box you are not
    /// allowed to type in, and the dialog opens with nothing in it — so the first thing it
    /// showed was a dead field above a button that could not be pressed. There is no frame
    /// until there are rows to put in it.
    /// </summary>
    [AvaloniaFact]
    public void There_is_no_empty_list_frame()
    {
        var (window, vm) = Show();

        Assert.False(vm.HasRows);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<ScrollViewer>(),
            s => s.Name == "ModList" && s.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Choosing_a_link_shows_the_link_box_and_puts_the_others_away()
    {
        var (window, vm) = Show();

        vm.FromLink = true;

        Assert.Contains(VisibleBoxes(window), b => b.Name == "UrlBox");
        Assert.DoesNotContain(VisibleBoxes(window), b => b.Name == "PasteBox");

        // Naming the pack belongs to the install: a shared pack arrives with a name.
        Assert.DoesNotContain(VisibleBoxes(window), b => b.Name == "NameBox");
        Assert.Contains(VisibleBoxes(window), b => b.Name == "IdBox");
    }

    [AvaloniaFact]
    public void Choosing_paste_shows_the_paste_box()
    {
        var (window, vm) = Show();

        vm.FromPaste = true;

        Assert.Contains(VisibleBoxes(window), b => b.Name == "PasteBox");
        Assert.DoesNotContain(VisibleBoxes(window), b => b.Name == "UrlBox");
    }

    /// <summary>
    /// The three sources are not the same size — a link is one text box, an install is a
    /// list of forty mods — so the window follows its content instead of being tall enough
    /// for the longest of them. Fixed, the short ones were a field, a button and a lot of
    /// nothing, which reads as something that failed to load.
    /// </summary>
    [AvaloniaFact]
    public void The_window_is_only_as_tall_as_what_is_in_it()
    {
        var (window, vm) = Show();

        vm.FromLink = true;
        var asLink = Height(window);

        vm.FromPaste = true;
        Assert.True(Height(window) > asLink, "a paste box needs more room than a link");

        vm.FromInstall = true;
        var empty = Height(window);

        foreach (var i in Enumerable.Range(0, 6))
            vm.Mods.Add(new ImportRowViewModel(new ImportCandidate(
                new InstalledMod($"mod{i}.zip", $"mod{i}.zip", $"mod{i}", $"Some Mod {i}", "1.0.0", null),
                ImportVerdict.Ready, null, "1.0.0")));

        Assert.True(Height(window) > empty, "a scanned list should grow the window");

        // And not past the screen: a big install scrolls, which is what the scroll area
        // inside it was always for.
        foreach (var i in Enumerable.Range(0, 60))
            vm.Mods.Add(new ImportRowViewModel(new ImportCandidate(
                new InstalledMod($"more{i}.zip", $"more{i}.zip", $"more{i}", $"Another Mod {i}", "1.0.0", null),
                ImportVerdict.Ready, null, "1.0.0")));

        Assert.True(Height(window) <= window.MaxHeight, "the window outgrew its own maximum");
    }

    private static double Height(Window window)
    {
        // Bindings and the size-to-content pass both settle on later layout passes;
        // measuring straight after a state change reads a half-updated window.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return window.ClientSize.Height;
    }

    /// <summary>
    /// The pack is built for the game the mods are being run on, and nobody is asked.
    ///
    /// There was a dropdown here, defaulted from the newest version Cairn knew about and
    /// labelled "Scan for game 1.22.6" — which read as a filter on the scan, and asked a
    /// question with one sensible answer. Moving a pack to another game version is its own
    /// step in Settings, where it comes with a preview of what it does to every mod.
    /// </summary>
    [AvaloniaFact]
    public void The_pack_is_built_for_the_game_the_mods_are_run_on()
    {
        var (window, vm) = Show(Choice(playedOn: "1.21.4", gameVersion: "1.22.6"));

        Assert.Equal("1.21.4", vm.GameVersion);
        Assert.Equal("1.21.4", vm.GameLine);

        // Nothing to pick, and nothing to press.
        Assert.Empty(window.GetVisualDescendants().OfType<ComboBox>());
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Name == "ScanButton");
    }

    /// <summary>
    /// And says what that costs, in the row itself.
    ///
    /// Not merely that nothing was found: with no install there is no version to measure
    /// against, so a mod marked for nothing like the pack's target is left out rather than
    /// taken on the strength of somebody running it. That consequence used to be invisible,
    /// which is how three perfectly good mods went missing from an import with only a line
    /// about an install to explain it.
    /// </summary>
    [AvaloniaFact]
    public void With_no_install_to_ask_the_row_says_so_and_what_it_costs()
    {
        var (_, vm) = Show(Choice(playedOn: null, gameVersion: "1.22.6"));

        Assert.Equal("1.22.6", vm.GameVersion);
        Assert.Equal("not found", vm.GameLine);
        Assert.Contains("1.22.6", vm.GameDetail);
        Assert.Contains("unmarked", vm.GameDetail);
    }

    [AvaloniaFact]
    public void The_button_says_what_pressing_it_will_do()
    {
        var (_, vm) = Show();

        Assert.Equal("Create pack", vm.ImportLabel);

        vm.FromLink = true;
        Assert.Equal("Import", vm.ImportLabel);
    }

    [AvaloniaFact]
    public void Nothing_can_be_imported_until_there_is_something_to_import()
    {
        var (_, vm) = Show();

        // An install nobody has scanned yet has no plan, so there is nothing to create.
        Assert.False(vm.CanImport);

        vm.FromLink = true;
        Assert.False(vm.CanImport);
        vm.Url = "https://cairns.gg/someone/pack";
        Assert.True(vm.CanImport);

        vm.FromPaste = true;
        Assert.False(vm.CanImport);
        vm.Text = "{}";
        Assert.True(vm.CanImport);
    }

    [AvaloniaFact]
    public async Task Scanning_a_folder_with_no_mods_says_so_rather_than_offering_a_pack()
    {
        var (_, vm) = Show();

        await vm.ScanAsync();

        Assert.Contains("No mod zips", vm.Summary);
        Assert.False(vm.CanImport);
    }

    /// <summary>
    /// A row exists as soon as its zip has been read, and says "checking…" until its lookup
    /// lands. The folder is on disk and reading it is instant — only ModDB takes a moment,
    /// and waiting on it to show somebody their own mods made it look as though Cairn were
    /// off finding them rather than reading them.
    /// </summary>
    [AvaloniaFact]
    public void A_mod_is_listed_before_it_has_been_checked()
    {
        var mod = new InstalledMod("olla_1.2.0.zip", "olla_1.2.0.zip", "olla", "Olla", "1.2.0", null);
        var row = new ImportRowViewModel(mod);

        Assert.Equal("Olla 1.2.0", row.Name);
        Assert.Equal("checking…", row.Verdict);

        // Not dimmed while it waits: there is nothing wrong with it yet.
        Assert.Equal(1.0, row.RowOpacity);

        row.Decide(new ImportCandidate(mod, ImportVerdict.Unknown, null, "ModDB has no mod"));

        Assert.Equal("not on ModDB", row.Verdict);
        Assert.False(row.Included);
        Assert.Equal(0.5, row.RowOpacity);
    }

    /// <summary>
    /// Reading the install is what choosing it means, so it happens on its own. Pressing a
    /// button to make the thing you just chose happen is a step that answers nothing.
    /// </summary>
    [AvaloniaFact]
    public void Opening_on_the_install_reads_it_without_being_asked()
    {
        var (_, vm) = Show();

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The folder in these tests holds nothing, so the scan is over by the time it is
        // looked at — and it says so, which is the whole output of a scan with no mods.
        Assert.Contains("No mod zips", vm.Summary);
    }

    /// <summary>
    /// Forty mods is forty ModDB lookups. Somebody who opened this to paste a link should
    /// not be waiting on them, so walking away from the install stops the reading.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_another_source_stops_reading_the_install()
    {
        var (_, vm) = Show();

        vm.FromLink = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Busy);
        Assert.Empty(vm.Mods);

        // And coming back asks for it again.
        vm.FromInstall = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("No mod zips", vm.Summary);
    }

    // ---- pointing Cairn at the install it could not find ----

    /// <summary>
    /// A directory real enough for GameInstall.TryAt, made under a fresh temp root so the
    /// tests below cannot see each other's.
    /// </summary>
    private static string RealInstall(string name = "Vintagestory")
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "cairn-vs-" + Guid.NewGuid().ToString("n")[..8], name);

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(
            Path.Combine(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory"),
            new byte[64]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        return dir;
    }

    /// <summary>
    /// Why this control lives here rather than in Preferences, where it was first built.
    ///
    /// An install decides the version a pack targets and whether a mod marked for nothing
    /// like it may be taken on the strength of somebody running it. Both of those are only
    /// ever visible on this list — so a setting two windows away silently changed what a scan
    /// concluded, and nothing connected the two.
    /// </summary>
    /// <summary>
    /// An install whose VintagestoryAPI.dll carries no readable version is refused, and says
    /// why.
    ///
    /// It is no use for either thing the answer decides: the pack takes its version from
    /// here, and a pack launches from an install only when the two versions match. Accepted
    /// in silence it produced the worst possible screen — a folder chosen, no complaint
    /// anywhere, and the same "no Vintage Story install found" line still underneath it.
    /// </summary>
    [AvaloniaFact]
    public void An_install_with_no_readable_version_is_refused_and_says_why()
    {
        CairnSettings.Update(s => s.GameInstallPath = null);

        var (_, vm) = Show(Choice(playedOn: null, gameVersion: "1.22.5"));

        var dir = RealInstall();
        vm.PickFolder = () => Task.FromResult<string?>(dir);
        vm.ChooseInstallCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(CairnSettings.Load().GameInstallPath);
        Assert.Null(vm.PlayedOn);
        Assert.Contains(dir, vm.InstallProblem);
        Assert.Contains("version", vm.InstallProblem);
    }

    /// <summary>
    /// The mods folder, asked for as the folder people can name rather than as the data path
    /// Cairn needs. Both ends of the same answer are accepted, and the worlds beside it
    /// follow — fixing the mods and leaving the world list reading somewhere else would be
    /// half a repair.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_a_mods_folder_moves_what_gets_scanned()
    {
        CairnSettings.Update(s => s.GameDataPath = null);

        var data = Path.Combine(Path.GetTempPath(), "cairn-data-" + Guid.NewGuid().ToString("n")[..8]);
        var mods = Path.Combine(data, "Mods");
        Directory.CreateDirectory(mods);

        var (_, vm) = Show(Choice(playedOn: null));

        // The Mods folder itself, which is what somebody has in front of them.
        vm.PickFolder = () => Task.FromResult<string?>(mods);
        vm.ChooseModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(mods, vm.ModsDir);
        Assert.Equal(data, CairnSettings.Load().GameDataPath);
        Assert.True(vm.ModsAreChosen);

        // And the folder holding it, which is what a picker lands on just as often.
        vm.PickFolder = () => Task.FromResult<string?>(data);
        vm.ChooseModsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(mods, vm.ModsDir);
        Assert.Equal(data, CairnSettings.Load().GameDataPath);

        CairnSettings.Update(s => s.GameDataPath = null);
        Directory.Delete(data, recursive: true);
    }

    /// <summary>
    /// Refused where it is picked rather than stored. A path that is not an install would
    /// otherwise sit in settings.json being skipped by the search — which looks exactly like
    /// the bug this control exists to fix, and would be blamed on the same thing.
    /// </summary>
    [AvaloniaFact]
    public void A_folder_that_is_not_an_install_is_refused_and_says_so()
    {
        CairnSettings.Update(s => s.GameInstallPath = null);

        var (window, vm) = Show(Choice(playedOn: null));

        var empty = Path.Combine(Path.GetTempPath(), "cairn-empty-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(empty);

        vm.PickFolder = () => Task.FromResult<string?>(empty);
        vm.ChooseInstallCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(CairnSettings.Load().GameInstallPath);
        Assert.Null(vm.PlayedOn);
        Assert.Contains(empty, vm.InstallProblem);

        // On screen, not only on the view model: the binding is what a person reads.
        Assert.Contains(vm.InstallProblem, VisibleText(window));

        Directory.Delete(empty);
    }

    /// <summary>
    /// The folder holding the install, which on macOS is the only thing a picker can select
    /// — it will not enter Vintagestory.app. What gets recorded is the install, not what was
    /// picked, or every later start-up would search for it again.
    /// </summary>
    /// <summary>
    /// The folder holding the install is reached, which on macOS is the only thing a picker
    /// can select — it will not enter Vintagestory.app. Told apart from a folder with nothing
    /// in it by which refusal comes back: this one got as far as reading the install.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_above_an_install_is_looked_into()
    {
        CairnSettings.Update(s => s.GameInstallPath = null);

        var dir = RealInstall("Vintagestory.app");
        var (_, vm) = Show(Choice(playedOn: null));

        vm.PickFolder = () => Task.FromResult<string?>(Path.GetDirectoryName(dir)!);
        vm.ChooseInstallCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Named in the refusal, so what was found is the install rather than what was picked.
        Assert.Contains(dir, vm.InstallProblem);
    }
}
