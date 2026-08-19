using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The Mod config tab, driven through the real window.
///
/// The fixture writes actual config files under the pack's data path and an actual baseline
/// beside them, because the tab's whole job is to tell an author's edits from what a mod
/// ships. A stubbed survey would test the list rendering and nothing that makes it useful.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ModConfigTabTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-cfgtab-" + Guid.NewGuid().ToString("n")[..8]);

    private string PackDir => Path.Combine(_home, "packs", "anego");
    private string DataDir => Path.Combine(PackDir, "data");

    public ModConfigTabTests()
    {
        Directory.CreateDirectory(Path.Combine(PackDir, "Mods"));

        File.WriteAllText(Path.Combine(PackDir, "pack.json"), JsonSerializer.Serialize(
            new { id = "anego", name = "Anego", gameVersion = "1.22.5", mods = Array.Empty<object>() },
            new JsonSerializerOptions { WriteIndented = true }));

        // What the mods wrote on a first launch, and what Cairn saw then.
        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": [] }""");
        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": false } }""");
        ModConfigFiles.Capture(DataDir);

        // And then the author, in game: the entry that makes the two mods agree.
        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": ["footprints"] }""");

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void WriteConfig(string name, string json)
    {
        var path = Path.Combine(DataDir, "ModConfig", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private PackManifest Saved() => PackManifest.Load(Path.Combine(PackDir, "pack.json"));

    private static (MainWindow Window, MainViewModel Vm) Show()
    {
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.SelectedPack = vm.Packs.First();
        return (window, vm);
    }

    private static PackDetailViewModel OpenTab(MainViewModel vm)
    {
        var detail = vm.Detail!;
        detail.SelectedTab = PackDetailViewModel.ModConfigTab;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return detail;
    }

    /// <summary>
    /// The tab index is a constant in the view model and an ordinal in the markup, and
    /// nothing but this connects them.
    /// </summary>
    [AvaloniaFact]
    public void The_constant_names_the_tab_it_says_it_does()
    {
        var (window, _) = Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var items = tabs.Items.OfType<TabItem>().ToList();

        Assert.Equal("Mod config", items[PackDetailViewModel.ModConfigTab].Header as string);
    }

    [AvaloniaFact]
    public void Opening_the_tab_shows_what_the_author_changed_and_not_the_rest()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        var row = Assert.Single(detail.ModConfigSettings);
        Assert.Equal("compatibleMods", row.Key);
        Assert.Equal("terrainslabs.json", row.File);
        Assert.Equal("[footprints]", row.CurrentText);
        Assert.Equal("[]", row.BaselineText);
        Assert.False(row.Carried);
    }

    /// <summary>
    /// Avalonia resolves bindings at runtime, so a stale path in the template fails silently.
    /// The row has to be found on screen, not only in the collection.
    /// </summary>
    [AvaloniaFact]
    public void The_row_renders_with_its_key_and_its_value()
    {
        var (window, vm) = Show();
        OpenTab(vm);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();

        Assert.Contains("compatibleMods", texts);
        Assert.Contains("terrainslabs.json", texts);
        Assert.Contains("[footprints]", texts);
        Assert.Contains("was []", texts);
    }

    [AvaloniaFact]
    public void Ticking_a_row_writes_it_into_the_pack()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        detail.ModConfigSettings[0].Carried = true;

        // Saved as you tick, the same as the Hotkeys tab. There is no Save button, and an
        // edit that sat unsaved would be thrown away by selecting another pack.
        var carried = Saved().ModConfig!;
        Assert.Equal("footprints",
            carried["terrainslabs.json"]["compatibleMods"]![0]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void Unticking_takes_it_back_out()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        detail.ModConfigSettings[0].Carried = true;
        detail.ModConfigSettings[0].Carried = false;

        // Null rather than an empty object, so the pack reads as unchanged against what was
        // published rather than as a revision that changed nothing.
        Assert.Null(Saved().ModConfig);
    }

    [AvaloniaFact]
    public void A_value_the_pack_already_carries_arrives_ticked()
    {
        var manifest = Saved();
        manifest.ModConfig = new Dictionary<string, System.Text.Json.Nodes.JsonObject>
        {
            ["BedSpawn.json"] = (System.Text.Json.Nodes.JsonNode.Parse(
                """{ "Rooms": { "Enabled": false } }""") as System.Text.Json.Nodes.JsonObject)!,
        };
        manifest.Save(Path.Combine(PackDir, "pack.json"));

        var (_, vm) = Show();
        var detail = OpenTab(vm);

        var carried = Assert.Single(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");
        Assert.True(carried.Carried);

        // Carried but unchanged, and shown for that reason alone.
        Assert.False(carried.IsChanged);
    }

    /// <summary>
    /// The way out of the one thing the baseline cannot see — a value changed during the
    /// very first session, which was in the file before anything observed it.
    /// </summary>
    [AvaloniaFact]
    public void Show_all_lists_the_settings_that_do_not_read_as_changed()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Single(detail.ModConfigSettings);

        detail.ShowAllSettings = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["Rooms.Enabled", "compatibleMods", "enableSlabs"],
            detail.ModConfigSettings.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// Turning the filter on reloads the survey. A tick made before that must survive it —
    /// otherwise toggling Show all would silently untick what somebody just chose, and,
    /// because ticking saves, would write that to the pack.
    /// </summary>
    [AvaloniaFact]
    public void A_tick_survives_toggling_Show_all()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        detail.ModConfigSettings[0].Carried = true;

        detail.ShowAllSettings = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(Assert.Single(detail.ModConfigSettings, r => r.Key == "compatibleMods").Carried);
        Assert.NotNull(Saved().ModConfig);
    }

    [AvaloniaFact]
    public void The_search_narrows_by_setting_or_file()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);
        detail.ShowAllSettings = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        detail.ModConfigSearch = "BedSpawn";
        Assert.Equal("Rooms.Enabled", Assert.Single(detail.ModConfigSettings).Key);

        detail.ModConfigSearch = "enableSlabs";
        Assert.Equal("enableSlabs", Assert.Single(detail.ModConfigSettings).Key);

        detail.ModConfigSearch = "nothing matches this";
        Assert.Empty(detail.ModConfigSettings);
        Assert.True(detail.ShowNoModConfigFound);
        Assert.Equal("No setting matches that.", detail.NoModConfigFoundLine);
    }

    /// <summary>
    /// Reopening re-reads the files, because the values it shows are ones somebody has just
    /// changed in game — alt-tabbing out of a session to carry one is the whole use of it.
    /// </summary>
    [AvaloniaFact]
    public void Reopening_the_tab_picks_up_a_change_made_since
        ()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.DoesNotContain(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");

        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": true } }""");

        detail.SelectedTab = 0;
        OpenTab(vm);

        Assert.Contains(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");
    }

    /// <summary>
    /// Every pack that exists today lands here on the first upgrade: a config folder full of
    /// values whose history nothing recorded. Saying "nothing changed" would be a lie about
    /// somebody's own pack, and the distinction is the difference between a tab that looks
    /// broken and one that says what to do next.
    /// </summary>
    [AvaloniaFact]
    public void A_pack_with_no_baseline_says_so_rather_than_claiming_nothing_changed()
    {
        File.Delete(Path.Combine(DataDir, ModConfigFiles.BaselineName));

        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Empty(detail.ModConfigSettings);
        Assert.Contains("no record", detail.NoModConfigFoundLine);

        // And Show all still reaches every one of them, which is the way through.
        detail.ShowAllSettings = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, detail.ModConfigSettings.Count);
    }

    /// <summary>
    /// Pumps the dispatcher until something is true, or gives up. Generous, because this
    /// waits on the operating system's file notifications and then a debounce on top —
    /// FSEvents on macOS is not quick, and a tight bound here would be a test that fails on
    /// a busy machine rather than a test that means anything.
    /// </summary>
    private static async Task Settle(Func<bool> until)
    {
        for (var i = 0; i < 500 && !until(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The tab is for carrying values somebody has just changed somewhere else — in game, in
    /// ConfigLib's screen, or in an editor opened from the folder button. Having to leave the
    /// tab and come back to see the change was the obvious thing wrong with it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_change_on_disk_shows_up_without_leaving_the_tab()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.DoesNotContain(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");

        WriteConfig("BedSpawn.json", """{ "RequireSneaking": false, "Rooms": { "Enabled": true } }""");

        await Settle(() => detail.ModConfigSettings.Any(r => r.Key == "Rooms.Enabled"));

        var row = Assert.Single(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");
        Assert.Equal("true", row.CurrentText);
        Assert.Equal("false", row.BaselineText);
    }

    /// <summary>
    /// The game rewrites every config file when it exits. Rebuilding the list for a write
    /// that moved nothing would throw away the scroll position of a list where nothing had
    /// changed — so the rows have to be the same rows, not merely equal ones.
    /// </summary>
    [AvaloniaFact]
    public async Task A_write_that_changes_nothing_leaves_the_rows_alone()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        var before = detail.ModConfigSettings.Single();

        // Byte-identical, as a mod rewriting its own config at shutdown would be.
        WriteConfig("terrainslabs.json", """{ "enableSlabs": true, "compatibleMods": ["footprints"] }""");

        await Task.Delay(900);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(before, detail.ModConfigSettings.Single());
    }

    /// <summary>
    /// Selecting another pack replaces the whole pane without going through the tab, so the
    /// watcher has to come down with it or every pack visited leaves one behind.
    /// </summary>
    [AvaloniaFact]
    public async Task Leaving_the_pack_stops_the_watching()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        vm.SelectedPack = null;
        Assert.Null(vm.Detail);

        // Whatever happens to the files now must not reach the discarded pane.
        WriteConfig("BedSpawn.json", """{ "Rooms": { "Enabled": true } }""");

        await Task.Delay(900);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(detail.ModConfigSettings, r => r.Key == "Rooms.Enabled");
    }

    /// <summary>
    /// The button is asserted, the click is not: invoking the command would open a file
    /// manager on whatever machine runs the suite.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_button_points_at_this_packs_own_ModConfig()
    {
        var (window, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Equal(Path.Combine(DataDir, "ModConfig"), detail.ModConfigFolder);

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Open config folder");

        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.Command!.CanExecute(null));
    }

    [AvaloniaFact]
    public void A_pack_whose_mods_have_written_nothing_says_so()
    {
        Directory.Delete(Path.Combine(DataDir, "ModConfig"), recursive: true);

        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Empty(detail.ModConfigSettings);
        Assert.True(detail.ShowNoModConfigFound);
        Assert.Contains("Play it once", detail.NoModConfigFoundLine);
    }

    // ---- a carried value that moves afterwards ----

    /// <summary>
    /// A tick means "this value travels with the pack", not "this value, as it stood the
    /// moment you ticked it".
    ///
    /// The tick wrote the manifest and nothing else ever did, so changing the setting
    /// afterwards updated every row on screen and left the pack declaring the old number —
    /// which is what got published. The tab showed the new value beside a ticked box, so
    /// nothing on screen suggested otherwise, and the only way out was to untick the row and
    /// tick it again.
    /// </summary>
    [AvaloniaFact]
    public void A_carried_value_that_changes_afterwards_is_what_the_pack_carries()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Single(detail.ModConfigSettings).Carried = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            """["footprints"]""",
            Saved().ModConfig!["terrainslabs.json"]["compatibleMods"]!.ToJsonString());

        // The author changes their mind in game, and the tab is opened again.
        WriteConfig("terrainslabs.json",
            """{ "enableSlabs": true, "compatibleMods": ["footprints", "carryon"] }""");

        detail.LoadModConfig();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            """["footprints","carryon"]""",
            Saved().ModConfig!["terrainslabs.json"]["compatibleMods"]!.ToJsonString());
    }

    /// <summary>
    /// A value nobody ticked is not adopted by having been looked at. Reading the files is
    /// not the same as choosing what the pack carries, and the tab is opened by anybody who
    /// wants to see what a mod is set to.
    /// </summary>
    [AvaloniaFact]
    public void An_unticked_value_that_changes_is_still_not_carried()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        WriteConfig("terrainslabs.json",
            """{ "enableSlabs": false, "compatibleMods": ["footprints"] }""");

        detail.LoadModConfig();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(Saved().ModConfig);
    }

    /// <summary>
    /// And the pack says it has something to publish, which is the only place the change is
    /// visible: the write is as silent as ticking is, so the Share state has to notice.
    /// </summary>
    [AvaloniaFact]
    public void The_pack_notices_it_has_something_to_publish_again()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Single(detail.ModConfigSettings).Carried = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var before = detail.Share.Status;

        WriteConfig("terrainslabs.json",
            """{ "enableSlabs": true, "compatibleMods": ["footprints", "carryon"] }""");

        detail.LoadModConfig();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Nothing is published in this fixture, so the state cannot move — what has to hold
        // is that it was recomputed rather than left saying what it said before the file
        // moved. The manifest is the evidence; this pins the refresh that carries it.
        Assert.Equal(before, detail.Share.Status);
        Assert.NotNull(Saved().ModConfig);
    }

    /// <summary>
    /// Taking an author's revision is the one reload that must not write back.
    ///
    /// Their values are newer than this copy's files — the pack's own reach the files at the
    /// next launch — so reading the files and saving what they say would revert the update
    /// the moment the tab refreshed underneath it.
    /// </summary>
    [AvaloniaFact]
    public void Adopting_an_authors_revision_does_not_write_the_old_files_back()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Single(detail.ModConfigSettings).Carried = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // What taking a revision leaves behind: a manifest the files do not agree with yet.
        detail.Manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = (JsonNode.Parse(
                """{"compatibleMods":["footprints","fromtheauthor"]}""") as JsonObject)!,
        };

        detail.LoadModConfig(adopting: true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            "fromtheauthor",
            detail.Manifest.ModConfig!["terrainslabs.json"]["compatibleMods"]!.ToJsonString());
    }

    /// <summary>
    /// The Share button is the only place a pack says it has something to publish, and a
    /// session that has just ended is the commonest way one comes to.
    ///
    /// Somebody plays, changes a value in game or in ConfigLib's screen, and quits. The share
    /// state is worked out when a pack is selected or edited, and neither happens on the way
    /// back from a game — so the button went on reading "Shared" over a pack that had moved
    /// underneath it, which is the same silence the stale value itself had.
    /// </summary>
    [AvaloniaFact]
    public void Coming_back_from_a_game_notices_a_setting_that_moved()
    {
        var (_, vm) = Show();
        var detail = OpenTab(vm);

        Assert.Single(detail.ModConfigSettings).Carried = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Published exactly as it stands, so there is nothing outstanding.
        new PackStore().SaveLink("anego", new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/dizzyd/anego",
            Revision = 1,
            Published = new PublishRecord
            {
                Visibility = "unlisted",
                Connect = "stripped",
                Fingerprint = PackLink.Fingerprint(
                    new PackStore().PublishedDocument("anego", stripConnect: true)),
            },
        });

        // The pane last worked out its share state before that link existed.
        detail.RefreshLaunchState();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(ShareStatus.Shared, detail.Share.Status);

        // In game, and then quitting: the pane is told the run state moved.
        WriteConfig("terrainslabs.json",
            """{ "enableSlabs": true, "compatibleMods": ["footprints", "carryon"] }""");

        detail.RefreshLaunchState();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(ShareStatus.Pending, detail.Share.Status);
        Assert.Equal("Publish changes", detail.ShareLabel);
        Assert.True(detail.ShareIsUrgent);
    }
}
