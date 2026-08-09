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
using Cairn.Core.ModDb;
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
            playedOn,
            gameVersion,
            suggestId: name => (name ?? "").ToLowerInvariant().Replace(' ', '-'));

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
        Assert.Contains("install is 1.21.4", vm.InstallNote);

        // Nothing to pick, and nothing to press.
        Assert.Empty(window.GetVisualDescendants().OfType<ComboBox>());
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Name == "ScanButton");
    }

    [AvaloniaFact]
    public void With_no_install_to_ask_it_says_so_rather_than_naming_one()
    {
        var (_, vm) = Show(Choice(playedOn: null, gameVersion: "1.22.6"));

        Assert.Equal("1.22.6", vm.GameVersion);
        Assert.Contains("No Vintage Story install found", vm.InstallNote);
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
}
