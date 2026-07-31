using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Drives the real window against a fixture CAIRN_HOME. Avalonia resolves bindings at
/// runtime, so a stale binding path fails silently — these render the window and assert
/// on the visual tree to catch that.
/// </summary>
public class MainWindowTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-uitest-" + Guid.NewGuid().ToString("n")[..8]);

    public MainWindowTests()
    {
        WritePack("anego", "Anego Server", "1.22.5", "anego.example.com:42420", ["glassview", "unchisel"]);
        WritePack("vanilla-qol", "Vanilla + QoL", "1.22.5", null, ["glassview"]);
        WritePack("old-pack", "Legacy 1.21 Pack", "1.21.5", null, ["glassview"]);

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void WritePack(string id, string name, string game, string? connect, string[] mods)
    {
        var dir = Path.Combine(_home, "packs", id);
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));

        File.WriteAllText(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(
            new
            {
                id, name, gameVersion = game, connect,
                mods = mods.Select(m => new { modid = m }).ToArray(),
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (MainWindow Window, MainViewModel Vm) Show()
    {
        // Offline: showing a pack sends its rows to ModDB for names and icons.
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        return (window, vm);
    }

    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!);

    private static Dictionary<string, Button> Buttons(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Content is string s && !string.IsNullOrEmpty(s))
            .GroupBy(b => (string)b.Content!)
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>A result as the pane builds it: wired to the pack, not free-floating.</summary>
    private static SearchHitViewModel HitFor(PackDetailViewModel detail, string modId, string name) =>
        detail.MakeHitForTest(modId, name);

    private static SearchHitViewModel Hit(string modId, string name, bool compatible = true) =>
        new(new ModSearchResult(
                new ModDbSearchEntry { Name = name, ModIdStrs = [modId], Side = "client", Downloads = 1 },
                compatible),
            "1.22.x");

    // ---- listing ----

    [AvaloniaFact]
    public void Every_pack_appears_in_the_sidebar()
    {
        var (window, vm) = Show();

        Assert.Equal(3, vm.Packs.Count);
        Assert.Equal(3, window.GetVisualDescendants().OfType<ListBox>().First().ItemCount);

        var text = VisibleText(window).ToList();
        Assert.Contains("Anego Server", text);
        Assert.Contains("Vanilla + QoL", text);
        Assert.Contains("Legacy 1.21 Pack", text);
    }

    [AvaloniaFact]
    public void Selecting_a_pack_shows_its_mods()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        Assert.NotNull(vm.Detail);
        Assert.Equal(2, vm.Detail!.Mods.Count);

        var text = VisibleText(window).ToList();
        Assert.Contains("glassview", text);
        Assert.Contains("unchisel", text);

        // Whether a mod is downloaded yet is Play's business, not something to caption
        // every row with.
        Assert.DoesNotContain(text, t => t.Contains("not installed"));
    }


    // ---- creating packs from the UI ----

    [AvaloniaFact]
    public void A_pack_can_be_created_entirely_through_the_ui()
    {
        var (window, vm) = Show();

        Buttons(window)["New pack"].Command!.Execute(null);
        Assert.True(vm.ShowCreate);
        Assert.False(vm.ShowDetail);

        vm.NewPackName = "Fresh Pack";
        vm.NewPackGameVersion = "1.22.5";
        vm.NewPackConnect = "example.com:42420";

        vm.CreatePackCommand.Execute(null);

        Assert.Null(vm.NewPackError);
        Assert.False(vm.IsCreating);
        Assert.Contains(vm.Packs, p => p.Id == "fresh-pack");
        Assert.Equal("fresh-pack", vm.SelectedPack!.Id);
        Assert.True(File.Exists(Path.Combine(_home, "packs", "fresh-pack", "pack.json")));
    }

    [AvaloniaFact]
    public void A_name_that_would_escape_the_store_is_slugged_into_something_harmless()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "../../../etc/evil";

        // The traversal never reaches the filesystem: separators are not in the slug
        // alphabet at all, so there is nothing to reject.
        Assert.Equal("etc-evil", vm.NewPackSlug);

        vm.CreatePackCommand.Execute(null);

        Assert.Null(vm.NewPackError);
        Assert.Contains(vm.Packs, p => p.Id == "etc-evil");
        Assert.False(Directory.Exists(Path.Combine(_home, "packs", "etc")));
        Assert.True(File.Exists(Path.Combine(_home, "packs", "etc-evil", "pack.json")));
    }

    [AvaloniaFact]
    public void A_duplicate_name_gets_its_own_id_rather_than_an_error()
    {
        var (_, vm) = Show();

        // Wanting two packs with the same name is reasonable — say, one per game version.
        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "Kitchen Sink";
        vm.NewPackGameVersion = "1.22.5";
        Assert.Equal("kitchen-sink", vm.NewPackSlug);
        vm.CreatePackCommand.Execute(null);

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "Kitchen Sink";
        vm.NewPackGameVersion = "1.22.5";
        Assert.Equal("kitchen-sink-2", vm.NewPackSlug);
        vm.CreatePackCommand.Execute(null);

        Assert.Null(vm.NewPackError);
        Assert.Equal(5, vm.Packs.Count);
        Assert.Contains(vm.Packs, p => p.Id == "kitchen-sink-2");

        // Both keep the name they were given; only the id had to differ.
        Assert.Equal(2, vm.Packs.Count(p => p.Display == "Kitchen Sink"));
    }

    [AvaloniaFact]
    public void The_derived_id_is_shown_before_the_pack_is_created()
    {
        var (window, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        Assert.False(vm.HasNewPackSlug);   // nothing to promise yet

        vm.NewPackName = "Anego's Café 2";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Punctuation and accents are handled rather than rejected, and the result is
        // visible on the form so the id is never a surprise.
        Assert.Equal("anego-s-cafe-2", vm.NewPackSlug);
        Assert.True(vm.HasNewPackSlug);
        Assert.Contains(VisibleText(window), t => t.Contains("Saved as anego-s-cafe-2"));
    }

    [AvaloniaFact]
    public void A_name_with_nothing_sluggable_still_produces_an_id()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "日本語";
        vm.NewPackGameVersion = "1.22.5";
        vm.CreatePackCommand.Execute(null);

        // Nothing survives the ASCII alphabet, so it falls back rather than failing.
        Assert.Null(vm.NewPackError);
        Assert.Contains(vm.Packs, p => p.Id == "pack");
        Assert.Equal("日本語", vm.Packs.Single(p => p.Id == "pack").Display);
    }

    [AvaloniaFact]
    public void Creating_without_a_name_is_refused()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "   ";

        Assert.False(vm.CreatePackCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void An_unusable_game_version_is_refused_when_creating()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "Bad Version";
        vm.NewPackGameVersion = ">=1.22.0";
        vm.CreatePackCommand.Execute(null);

        Assert.NotNull(vm.NewPackError);
        Assert.DoesNotContain(vm.Packs, p => p.Id == "bad-version");
    }

    // ---- editing a pack from the UI ----

    [AvaloniaFact]
    public void Adding_a_searched_mod_writes_it_to_the_manifest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        // The row adds itself; there is no separate "Add selected" button to aim at.
        HitFor(detail, "olla", "Olla").AddCommand.Execute(null);

        Assert.Null(detail.Error);
        Assert.Contains(detail.Mods, m => m.ModId == "olla");

        var onDisk = File.ReadAllText(Path.Combine(_home, "packs", "vanilla-qol", "pack.json"));
        Assert.Contains("olla", onDisk);
    }

    [AvaloniaFact]
    public void Adding_a_mod_already_in_the_pack_is_reported_not_duplicated()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        var hit = HitFor(detail, "glassview", "Glassview");

        // It is already in the pack, so the row says so instead of offering to add it.
        Assert.True(hit.AlreadyInPack);
        Assert.False(hit.CanAdd);
        Assert.False(hit.AddCommand.CanExecute(null));
        Assert.Single(detail.Mods, m => m.ModId == "glassview");
    }

    [AvaloniaFact]
    public void Removing_a_mod_updates_the_manifest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var row = detail.Mods.Single(m => m.ModId == "unchisel");
        row.RequestRemoveCommand.Execute(null);
        row.ConfirmRemoveCommand.Execute(null);

        Assert.DoesNotContain(detail.Mods, m => m.ModId == "unchisel");
        var onDisk = File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json"));
        Assert.DoesNotContain("unchisel", onDisk);
    }

    [AvaloniaFact]
    public async Task Pinning_and_unpinning_a_mod_round_trips_through_the_manifest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        // Versions are normally fetched when the mod is selected; prime the cache so the
        // test does not depend on the network.
        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);

        var row = detail.Mods.Single(m => m.ModId == "glassview");
        await row.EnsureReleasesAsync();

        // Choosing a version pins it — no separate Pin button.
        row.SelectedRelease = "1.3.0";

        var pinned = detail.Mods.Single(m => m.ModId == "glassview");
        Assert.True(pinned.IsPinned);
        Assert.Equal("1.3.0", pinned.PinDisplay);
        Assert.Contains("1.3.0", File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json")));

        // And choosing "latest" unpins it again.
        detail.Mods.Single(m => m.ModId == "glassview").SelectedRelease = PackDetailViewModel.TrackNewest;

        Assert.Equal("latest", detail.Mods.Single(m => m.ModId == "glassview").PinDisplay);
    }

    [AvaloniaFact]
    public void Saving_settings_persists_name_version_and_server()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        detail.EditName = "Renamed Pack";
        detail.EditConnect = "newhost:42420";
        detail.SaveSettingsCommand.Execute(null);

        Assert.Null(detail.Error);
        Assert.Equal("Renamed Pack", detail.Title);
        Assert.Contains("auto-joins newhost:42420", detail.ServerLine);

        var onDisk = File.ReadAllText(Path.Combine(_home, "packs", "vanilla-qol", "pack.json"));
        Assert.Contains("Renamed Pack", onDisk);
        Assert.Contains("newhost:42420", onDisk);
    }

    [AvaloniaFact]
    public void Saving_an_unusable_game_version_surfaces_an_error()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        detail.EditGameVersion = "^1.22";
        detail.SaveSettingsCommand.Execute(null);

        Assert.NotNull(detail.Error);
        Assert.True(detail.HasError);
    }

    [AvaloniaFact]
    public void Deleting_a_pack_removes_it_from_disk_and_the_list()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "old-pack");

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);

        Assert.DoesNotContain(vm.Packs, p => p.Id == "old-pack");
        Assert.False(Directory.Exists(Path.Combine(_home, "packs", "old-pack")));
        Assert.Equal(2, vm.Packs.Count);
    }

    // ---- wiring ----

    [AvaloniaFact]
    public void Every_action_button_is_bound_to_a_command()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // A TabControl only realises the selected tab's content, so each tab has to be
        // visited before its buttons exist in the visual tree.
        var found = new Dictionary<string, Button>();
        var tabControl = window.GetVisualDescendants().OfType<TabControl>().Single();

        foreach (var tab in tabControl.GetVisualDescendants().OfType<TabItem>().ToList())
        {
            tabControl.SelectedItem = tab;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            foreach (var (label, button) in Buttons(window))
                found.TryAdd(label, button);
        }

        foreach (var label in new[]
                 {
                     // Pane-level actions, plus the per-row ones the Mods list realises.
                     "Play", "New pack", "Search", "Save", "Delete", "Clear",
                     "View", "✕",
                 })
        {
            Assert.True(found.ContainsKey(label), $"no '{label}' button in the window");
            Assert.NotNull(found[label].Command);
        }
    }

    [AvaloniaFact]
    public void Adding_mods_is_not_a_separate_place_from_the_pack()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        var headers = window.GetVisualDescendants().OfType<TabItem>()
            .Select(t => t.Header as string)
            .ToList();

        // One list serves the pack and the results you build it from.
        Assert.Contains("Mods", headers);
        Assert.DoesNotContain("Add mods", headers);
        Assert.Contains("Settings", headers);
        Assert.Contains("Log", headers);
    }

    [AvaloniaFact]
    public void The_list_shows_the_pack_until_a_search_replaces_it()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        Assert.False(detail.ShowingSearch);
        Assert.Equal("2 mods in this pack", detail.ListHeading);

        // The same entry point a completed search uses.
        detail.ShowResults("olla", [HitFor(detail, "olla", "Olla")]);

        Assert.Equal("1 result for “olla”", detail.ListHeading);

        detail.ClearSearchCommand.Execute(null);

        // Clearing puts the pack back rather than leaving an empty list.
        Assert.False(detail.ShowingSearch);
        Assert.Empty(detail.SearchHits);
        Assert.Equal("", detail.SearchText);
        Assert.Equal("2 mods in this pack", detail.ListHeading);
    }

    [AvaloniaFact]
    public void Adding_from_a_result_shows_on_that_row_without_leaving_it()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        var hit = HitFor(detail, "olla", "Olla");
        Assert.False(hit.AlreadyInPack);

        hit.AddCommand.Execute(null);

        // The row stays on screen, so it has to stop offering to add it again.
        Assert.True(hit.AlreadyInPack);
        Assert.False(hit.CanAdd);
        Assert.Contains(detail.Mods, m => m.ModId == "olla");
    }

    [AvaloniaFact]
    public void Removing_a_mod_offers_it_again_in_the_results_on_screen()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var hit = HitFor(detail, "glassview", "Glassview");
        detail.ShowResults("glass", [hit]);
        Assert.True(hit.AlreadyInPack);

        var packRow = detail.Mods.Single(m => m.ModId == "glassview");
        packRow.RequestRemoveCommand.Execute(null);
        packRow.ConfirmRemoveCommand.Execute(null);

        // Both lists are the same screen now, so they cannot disagree.
        Assert.False(hit.AlreadyInPack);
        Assert.True(hit.CanAdd);
    }

    // ---- game versions ----



    [AvaloniaFact]
    public void The_games_pane_can_be_opened_and_closed()
    {
        var (window, vm) = Show();

        vm.ShowGameVersionsCommand.Execute(null);
        Assert.True(vm.ShowGames);

        var text = VisibleText(window).ToList();
        Assert.Contains("Game versions", text);
        Assert.Contains("Installed", text);
        Assert.Contains("Available", text);

        vm.ShowPacksCommand.Execute(null);
        Assert.False(vm.ShowGames);
    }

    [AvaloniaFact]
    public void Games_pane_buttons_are_bound()
    {
        var (window, vm) = Show();
        vm.ShowGameVersionsCommand.Execute(null);

        var buttons = Buttons(window);
        foreach (var label in new[] { "Install", "Refresh list", "Remove", "Back to packs" })
        {
            Assert.True(buttons.ContainsKey(label), $"no '{label}' button in the games pane");
            Assert.NotNull(buttons[label].Command);
        }
    }

    [AvaloniaFact]
    public void Nothing_is_installed_in_a_fresh_store()
    {
        var (_, vm) = Show();
        Assert.Empty(vm.Games.Installed);
    }

    [AvaloniaFact]
    public void The_games_pane_exposes_private_runtime_management()
    {
        var (window, vm) = Show();
        vm.ShowGameVersionsCommand.Execute(null);

        var buttons = Buttons(window);
        Assert.True(buttons.ContainsKey("Install its .NET"), "no runtime install button");
        Assert.NotNull(buttons["Install its .NET"].Command);

        // Nothing managed in a fresh store.
        Assert.Empty(vm.Games.ManagedRuntimes);
    }

    // ---- sharing ----

    [AvaloniaFact]
    public void A_pack_can_be_exported_from_the_ui()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.Detail!.ExportCommand.Execute(null);

        Assert.Null(vm.Detail.Error);
        Assert.True(vm.Detail.HasExported);
        Assert.True(File.Exists(vm.Detail.ExportedPath!));
        Assert.Contains("\"pack\"", vm.Detail.ExportedJson);
    }

    [AvaloniaFact]
    public void An_exported_pack_can_be_imported_back_under_a_new_id()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.Detail!.ExportCommand.Execute(null);
        var shared = vm.Detail.ExportedJson;

        vm.BeginImportCommand.Execute(null);
        Assert.True(vm.ShowImport);

        vm.ImportText = shared;
        vm.ImportAsId = "anego-copy";
        vm.ImportPackCommand.Execute(null);

        Assert.Null(vm.ImportError);
        Assert.False(vm.IsImporting);
        Assert.Contains(vm.Packs, p => p.Id == "anego-copy");
        Assert.Equal("anego-copy", vm.SelectedPack!.Id);
    }

    [AvaloniaFact]
    public void Importing_junk_reports_an_error_and_stays_on_the_form()
    {
        var (_, vm) = Show();

        vm.BeginImportCommand.Execute(null);
        vm.ImportText = "this is not a pack";
        vm.ImportPackCommand.Execute(null);

        Assert.NotNull(vm.ImportError);
        Assert.True(vm.IsImporting);
        Assert.Equal(3, vm.Packs.Count);
    }

    [AvaloniaFact]
    public void Importing_onto_an_existing_id_is_refused()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.Detail!.ExportCommand.Execute(null);
        var shared = vm.Detail.ExportedJson;

        vm.BeginImportCommand.Execute(null);
        vm.ImportText = shared;          // same id as the existing pack
        vm.ImportPackCommand.Execute(null);

        Assert.NotNull(vm.ImportError);
        Assert.Contains("already exists", vm.ImportError!);
        Assert.Equal(3, vm.Packs.Count);
    }

    [AvaloniaFact]
    public void The_import_pane_is_reachable_and_bound()
    {
        var (window, vm) = Show();
        vm.BeginImportCommand.Execute(null);

        var buttons = Buttons(window);
        Assert.True(buttons.ContainsKey("Import"), "no Import button");
        Assert.NotNull(buttons["Import"].Command);
        Assert.Contains(VisibleText(window), t => t.Contains("Import a pack"));
    }

    [AvaloniaFact]
    public void The_new_pack_form_offers_versions_rather_than_asking_you_to_type_one()
    {
        var (window, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Installed versions are listed without needing the network; the catalog is
        // appended asynchronously on top.
        Assert.NotEmpty(vm.GameVersionChoices);
        Assert.False(string.IsNullOrWhiteSpace(vm.NewPackGameVersion));
        Assert.Contains(vm.NewPackGameVersion, vm.GameVersionChoices);

        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.IsEffectivelyVisible);

        Assert.NotNull(combo);
        Assert.True(combo!.ItemCount > 0);

        // Assert on the control, not just the view model: a ComboBox bound to an empty
        // collection silently coerces its selection to null, which left the form showing
        // "choose a version" while the view model held a perfectly good value.
        Assert.NotNull(combo.SelectedItem);
        Assert.Equal(vm.NewPackGameVersion, combo.SelectedItem);
    }

    // ---- deleting ----

    [AvaloniaFact]
    public void Delete_is_reachable_from_the_sidebar_not_buried_in_a_tab()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // No tab switching required: it sits with New pack and Import.
        var buttons = Buttons(window);
        Assert.True(buttons.ContainsKey("Delete"), "no Delete button in the sidebar");
        Assert.NotNull(buttons["Delete"].Command);
    }

    [AvaloniaFact]
    public void Delete_asks_before_destroying_anything()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.RequestDeleteCommand.Execute(null);

        // Nothing is gone yet — it only armed the confirmation.
        Assert.True(vm.ConfirmingDelete);
        Assert.Equal(3, vm.Packs.Count);
        Assert.True(Directory.Exists(Path.Combine(_home, "packs", "anego")));
        Assert.Contains(VisibleText(window), t => t.Contains("Anego Server") && t.Contains("Delete"));
    }

    [AvaloniaFact]
    public void Declining_the_confirmation_keeps_the_pack()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.RequestDeleteCommand.Execute(null);
        vm.CancelDeleteCommand.Execute(null);

        Assert.False(vm.ConfirmingDelete);
        Assert.Equal(3, vm.Packs.Count);
        Assert.True(Directory.Exists(Path.Combine(_home, "packs", "anego")));
    }

    [AvaloniaFact]
    public void Confirming_removes_the_pack_and_its_mods()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);

        Assert.False(vm.ConfirmingDelete);
        Assert.DoesNotContain(vm.Packs, p => p.Id == "anego");
        Assert.False(Directory.Exists(Path.Combine(_home, "packs", "anego")));
    }

    [AvaloniaFact]
    public void Changing_the_selection_disarms_a_pending_confirmation()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.RequestDeleteCommand.Execute(null);

        // Otherwise the armed prompt would now be aimed at a different pack.
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");

        Assert.False(vm.ConfirmingDelete);
        Assert.Equal(3, vm.Packs.Count);
    }

    [AvaloniaFact]
    public void The_pack_detail_delete_command_also_routes_through_confirmation()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.Detail!.DeletePackCommand.Execute(null);

        Assert.True(vm.ConfirmingDelete);
        Assert.True(Directory.Exists(Path.Combine(_home, "packs", "anego")));
    }



    [AvaloniaFact]
    public void Downloading_takes_over_the_pane_so_nothing_can_be_edited()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "old-pack");
        Assert.True(vm.ShowDetail);

        vm.ProvisioningVersion = "1.21.5";
        vm.Provisioning = true;

        // Only the progress pane; the editable pack pane is gone.
        Assert.True(vm.ShowProvisioning);
        Assert.False(vm.ShowDetail);
        Assert.False(vm.ShowCreate);
        Assert.False(vm.ShowGames);
        Assert.False(vm.ShowImport);
        Assert.False(vm.ShowEmpty);

        var text = VisibleText(window).ToList();
        Assert.Contains("Getting things ready", text);
        Assert.DoesNotContain(text, t => t == "Add mods");

        vm.Provisioning = false;
        Assert.True(vm.ShowDetail);
        Assert.False(vm.ShowProvisioning);
    }

    [AvaloniaFact]
    public void The_sidebar_is_disabled_while_downloading()
    {
        var (window, vm) = Show();

        Assert.True(vm.NotProvisioning);
        vm.Provisioning = true;

        // Otherwise a second pack could be created or deleted mid-install.
        Assert.False(vm.NotProvisioning);

        var sidebar = window.GetVisualDescendants().OfType<ListBox>().First();
        Assert.False(sidebar.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Downloading_can_be_cancelled_rather_than_trapping_the_user()
    {
        var (window, vm) = Show();
        vm.Provisioning = true;

        Assert.True(vm.CanCancelProvision);

        var cancel = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == "Cancel" && b.IsEffectivelyVisible);

        Assert.NotNull(cancel);
        Assert.NotNull(cancel!.Command);

        vm.CancelProvisionCommand.Execute(null);
        Assert.Contains("cancel", vm.ProvisionStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ---- progress feedback ----

    private static ProgressBar Bar(Visual root, string name) =>
        root.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void The_provisioning_bar_animates_while_a_step_cannot_report_a_fraction()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();
        vm.ProvisioningVersion = "1.22.5";
        vm.Provisioning = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var bar = Bar(window, "ProvisionProgress");

        // Unpacking, and running the Windows installer, report nothing for minutes. A bar
        // pinned at zero the whole time reads as a hang.
        Assert.True(vm.ProvisionIndeterminate);
        Assert.True(bar.IsIndeterminate);

        // And a step that does know its position goes back to showing it.
        vm.ProvisionIndeterminate = false;
        vm.ProvisionFraction = 0.42;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(bar.IsIndeterminate);
        Assert.Equal(0.42, bar.Value, 3);
    }

    [AvaloniaFact]
    public void The_games_pane_bar_animates_on_the_same_rule()
    {
        var (window, vm) = Show();
        vm.ShowGameVersionsCommand.Execute(null);
        vm.Games.IsBusy = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var bar = Bar(window, "GamesProgress");
        Assert.True(bar.IsIndeterminate);

        vm.Games.ProgressIndeterminate = false;
        vm.Games.ProgressFraction = 0.5;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(bar.IsIndeterminate);
    }

    // ---- launch feedback ----

    [AvaloniaFact]
    public void There_is_no_install_banner_any_more()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // What will launch is settled by pressing Play; a standing summary was noise.
        var text = VisibleText(window).ToList();
        Assert.DoesNotContain(text, t => t.StartsWith("Vintage Story 1.22.5 for"));
        Assert.DoesNotContain(text, t => t.Contains("at /usr/local/share/dotnet"));
    }

    [AvaloniaFact]
    public void Play_reports_progress_and_cannot_be_pressed_twice()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();
        var detail = vm.Detail!;

        Assert.Equal("Play", detail.PlayLabel);
        Assert.True(detail.CanLaunch);
        Assert.False(detail.IsShowingLaunchStage);

        // Simulate the in-flight state; the real path also downloads mods.
        detail.IsLaunching = true;
        detail.LaunchStage = "Starting Vintage Story…";

        Assert.False(detail.CanLaunch);
        Assert.False(detail.PlayCommand.CanExecute(null));
        Assert.Equal("Working…", detail.PlayLabel);
        Assert.True(detail.IsShowingLaunchStage);
        Assert.Contains(VisibleText(window), t => t == "Starting Vintage Story…");

        var play = window.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Working…");
        Assert.False(play.IsEffectivelyEnabled);

        detail.IsLaunching = false;
        detail.LaunchStage = "";

        Assert.True(detail.CanLaunch);
        Assert.Equal("Play", detail.PlayLabel);
        Assert.False(detail.IsShowingLaunchStage);
    }

    [AvaloniaFact]
    public void A_missing_game_version_is_not_nagged_about_it_is_just_fetched_by_Play()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "old-pack");   // 1.21.5, absent here
        var detail = vm.Detail!;

        Assert.Null(detail.ResolvedInstall);

        // No warning banner, no "Install it" — Play is the one way in, and it stays
        // enabled precisely so it can do the download.
        var text = VisibleText(window).ToList();
        Assert.DoesNotContain(text, t => t.Contains("is not installed"));
        Assert.DoesNotContain(text, t => t.Contains("Install it"));
        Assert.True(detail.CanLaunch);
        Assert.True(detail.PlayCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Creating_a_pack_downloads_nothing_until_Play_is_pressed()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.NewPackName = "Fetches";

        // Pick from the offered list, as the dropdown requires.
        vm.GameVersionChoices.Add("1.21.5");
        vm.NewPackGameVersion = "1.21.5";

        vm.CreatePackCommand.Execute(null);

        Assert.Null(vm.NewPackError);
        Assert.Contains(vm.Packs, p => p.Id == "fetches");

        // Nothing is fetched yet: creating a pack should not commit you to a several
        // hundred megabyte download. Play does that, lazily, when you actually want it.
        Assert.False(vm.Provisioning);
        Assert.Null(vm.ProvisioningVersion);
        Assert.False(vm.ShowProvisioning);
    }

    [AvaloniaFact]
    public void Creating_without_choosing_a_version_is_refused_rather_than_crashing()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "No Version";
        vm.NewPackGameVersion = null!;      // what the ComboBox does with an unlisted value

        vm.CreatePackCommand.Execute(null);

        Assert.Equal("Choose a game version.", vm.NewPackError);
        Assert.DoesNotContain(vm.Packs, p => p.Id == "no-version");
    }

    [AvaloniaFact]
    public void The_version_dropdown_is_ordered_newest_first()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Installed versions used to be prepended, so an older installed version could
        // sit above newer published ones.
        var shown = vm.GameVersionChoices.ToList();
        var expected = Cairn.Core.GameVersionComparer.Descending(shown).ToList();

        Assert.Equal(expected, shown);
        Assert.Equal(shown.Distinct().Count(), shown.Count);
    }

    // ---- version pinning ----

    [AvaloniaFact]
    public async Task Opening_a_rows_dropdown_fetches_that_mods_versions()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;
        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);

        var row = detail.Mods.Single(m => m.ModId == "glassview");

        // Before opening it holds only what the row already knows — fetching every row's
        // versions to draw the pack would be one ModDB call per mod.
        Assert.Equal([PackDetailViewModel.TrackNewest], row.ReleaseChoices);
        Assert.False(row.ReleasesLoaded);

        await row.EnsureReleasesAsync();

        Assert.Equal([PackDetailViewModel.TrackNewest, "1.3.0", "1.2.0"], row.ReleaseChoices);
        Assert.Equal(PackDetailViewModel.TrackNewest, row.SelectedRelease);
    }

    [AvaloniaFact]
    public async Task A_pinned_mod_shows_its_pin_before_and_after_loading()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;
        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);

        var row = detail.Mods.Single(m => m.ModId == "glassview");
        await row.EnsureReleasesAsync();
        row.SelectedRelease = "1.2.0";

        // Pinning rewrites the manifest and rebuilds the rows, so this is a new row.
        var rebuilt = detail.Mods.Single(m => m.ModId == "glassview");
        Assert.True(rebuilt.IsPinned);

        // It must read as pinned straight away, not as "latest" until someone opens it.
        Assert.Equal("1.2.0", rebuilt.SelectedRelease);

        await rebuilt.EnsureReleasesAsync();
        Assert.Equal("1.2.0", rebuilt.SelectedRelease);
    }

    [AvaloniaFact]
    public async Task Reopening_a_dropdown_after_a_pin_does_not_go_back_to_the_network()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;
        detail.CacheReleaseChoices("glassview", ["1.3.0"]);

        var row = detail.Mods.Single(m => m.ModId == "glassview");
        await row.EnsureReleasesAsync();
        row.SelectedRelease = "1.3.0";

        // Pinning rebuilds the rows; without the cache that would fire a fresh lookup
        // every time a dropdown was opened again.
        var rebuilt = detail.Mods.Single(m => m.ModId == "glassview");
        await rebuilt.EnsureReleasesAsync();

        Assert.False(rebuilt.LoadingReleases);
        Assert.Contains("1.3.0", rebuilt.ReleaseChoices);
    }

    [AvaloniaFact]
    public async Task Filling_one_rows_dropdown_does_not_pin_anything()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0"]);
        detail.CacheReleaseChoices("unchisel", ["1.2.0"]);

        detail.Mods.Single(m => m.ModId == "glassview").SelectedRelease = "1.3.0";

        // Populating a list must never be mistaken for the user choosing from it.
        await detail.Mods.Single(m => m.ModId == "unchisel").EnsureReleasesAsync();

        Assert.False(detail.Mods.Single(m => m.ModId == "unchisel").IsPinned);
        Assert.True(detail.Mods.Single(m => m.ModId == "glassview").IsPinned);
    }

    // ---- what a pack row calls its mod ----

    [AvaloniaFact]
    public void A_row_shows_the_mod_id_until_its_real_name_is_known()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        var row = vm.Detail!.Mods.Single(m => m.ModId == "glassview");

        // A manifest holds ids, so the id is all a row can honestly show at first.
        Assert.Null(row.Name);
        Assert.Equal("glassview", row.Title);

        // The name arrives with the same lookup that fetches the icon.
        row.Name = "Glassview";
        Assert.Equal("Glassview", row.Title);
    }

    [AvaloniaFact]
    public void A_mod_with_no_name_on_ModDB_still_reads_as_something()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        var row = vm.Detail!.Mods.Single(m => m.ModId == "glassview");
        row.Name = "   ";

        Assert.Equal("glassview", row.Title);
    }

    // ---- updates are asked for, not applied ----

    [AvaloniaFact]
    public void A_pack_reports_what_is_installed_now_that_it_cannot_change_by_itself()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        var row = vm.Detail!.Mods.First();

        // Nothing is locked in the fixture, so there is nothing to report yet.
        Assert.False(row.HasInstalledVersion);
        Assert.Equal("", row.InstalledVersion);
    }

    [AvaloniaFact]
    public void A_row_offers_an_update_only_once_one_is_known()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;
        var row = detail.Mods.First();

        Assert.False(row.HasUpdate);
        Assert.False(detail.AnyUpdates);
        Assert.False(detail.UpdateAllCommand.CanExecute(null));

        // What a completed check leaves behind.
        row.UpdateAvailable = "1.4.0";

        Assert.True(row.HasUpdate);
        Assert.Equal("→ 1.4.0", row.UpdateNote);
    }

    [AvaloniaFact]
    public void Unpinned_now_reads_as_latest_rather_than_newest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        // "newest" implied it would move on its own, which is exactly what it no
        // longer does.
        Assert.Equal("latest", vm.Detail!.Mods.First().PinDisplay);
        Assert.Equal("latest", PackDetailViewModel.TrackNewest);
    }

    // ---- removing a mod ----

    [AvaloniaFact]
    public void Removing_a_mod_asks_before_doing_anything()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var row = detail.Mods.Single(m => m.ModId == "unchisel");
        row.RequestRemoveCommand.Execute(null);

        // Armed only — the button is one character next to a dropdown.
        Assert.True(row.ConfirmingRemove);
        Assert.Equal("Remove unchisel from this pack?", row.RemovePrompt);
        Assert.Contains(detail.Mods, m => m.ModId == "unchisel");
        Assert.Contains("unchisel", File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json")));
    }

    [AvaloniaFact]
    public void Declining_keeps_the_mod()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var row = detail.Mods.Single(m => m.ModId == "unchisel");
        row.RequestRemoveCommand.Execute(null);
        row.CancelRemoveCommand.Execute(null);

        Assert.False(row.ConfirmingRemove);
        Assert.Contains(detail.Mods, m => m.ModId == "unchisel");
    }

    [AvaloniaFact]
    public void Only_one_row_asks_at_a_time()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var first = detail.Mods.Single(m => m.ModId == "glassview");
        var second = detail.Mods.Single(m => m.ModId == "unchisel");

        first.RequestRemoveCommand.Execute(null);
        second.RequestRemoveCommand.Execute(null);

        // Otherwise two rows sit armed and a stray Enter could hit the wrong one.
        Assert.False(first.ConfirmingRemove);
        Assert.True(second.ConfirmingRemove);
    }

    // ---- searching for what will actually install ----

    [AvaloniaFact]
    public void The_range_that_counts_as_compatible_is_the_whole_minor()
    {
        var (_, vm) = Show();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");        // 1.22.5
        Assert.Equal("1.22.x", vm.Detail!.CompatibleVersionRange);

        // A mod marked for 1.21.0 installs fine on 1.21.5, so the range says so.
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "old-pack");     // 1.21.5
        Assert.Equal("1.21.x", vm.Detail!.CompatibleVersionRange);
    }

    [AvaloniaFact]
    public void A_mod_with_no_usable_release_is_shown_but_cannot_be_added()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        var stale = FullHit("ancientmod", "Ancient Mod", 999, compatible: false);

        // Listed rather than hidden — "why can I not find X" is worse than seeing why.
        Assert.True(stale.Incompatible);
        Assert.False(stale.CanAdd);
        Assert.Equal("no 1.22.x release", stale.NoReleaseNote);

        Assert.False(stale.AddCommand.CanExecute(null));

        // ...and one that is usable still can be.
        Assert.True(FullHit("olla", "Olla", 34157, "olla").AddCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void An_incompatible_result_still_links_to_its_page()
    {
        // Following it to ModDB is exactly what you would want to do next.
        var stale = FullHit("ancientmod", "Ancient Mod", 999, compatible: false);

        Assert.True(stale.HasPage);
        Assert.Equal("https://mods.vintagestory.at/show/mod/999", stale.PageUrl);
    }

    // ---- browsing ModDB ----

    private static SearchHitViewModel FullHit(string modId, string name, int assetId,
        string? alias = null, string? logo = null, bool compatible = true) =>
        new(new ModSearchResult(
                new ModDbSearchEntry
                {
                    Name = name, ModIdStrs = [modId], Side = "client", Downloads = 2172,
                    Author = "dizzyd", AssetId = assetId, UrlAlias = alias, Logo = logo,
                    Tags = ["Technology", "QoL"], Summary = "Ancient irrigation technology",
                },
                compatible),
            "1.22.x");

    [AvaloniaFact]
    public void A_result_knows_where_its_page_and_icon_live()
    {
        var hit = FullHit("olla", "Olla", 34157, "olla",
            "https://moddbcdn.vintagestory.at/olla_9b063fc6.png");

        Assert.True(hit.HasPage);
        Assert.Equal("https://mods.vintagestory.at/olla", hit.PageUrl);
        Assert.Equal("https://moddbcdn.vintagestory.at/olla_9b063fc6.png", hit.LogoUrl);
        Assert.Equal("by dizzyd", hit.Author);
        Assert.Equal("Technology · QoL", hit.Tags);
    }

    [AvaloniaFact]
    public void A_result_with_no_alias_still_gets_a_page()
    {
        // About a quarter of mods have no url alias, so the link is keyed on asset id.
        var hit = FullHit("telescopemod", "Furio's Telescope", 61959);

        Assert.True(hit.HasPage);
        Assert.Equal("https://mods.vintagestory.at/show/mod/61959", hit.PageUrl);
    }

    [AvaloniaFact]
    public void A_row_starts_without_an_icon_and_is_not_broken_by_never_getting_one()
    {
        // Roughly one mod in ten has no icon at all, and the rest arrive after the row
        // has already been drawn.
        var hit = FullHit("nologo", "No Logo", 1);

        Assert.Null(hit.Icon);
        Assert.False(hit.HasIcon);
        Assert.Null(hit.LogoUrl);
    }

    [AvaloniaFact]
    public void Every_row_can_reach_its_own_page()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        // No selection involved: the button belongs to the row it acts on.
        Assert.NotNull(FullHit("olla", "Olla", 34157, "olla").OpenPageCommand);
        Assert.NotNull(detail.Mods.First().OpenPageCommand);
    }

    // ---- what "connect" actually means ----

    /// <summary>
    /// A pack without a server used to be labelled "singleplayer", which is not what the
    /// field means. "connect" only decides whether launching skips the main menu; it does
    /// not restrict the pack, and multiplayer stays available from the menu either way.
    /// </summary>
    [AvaloniaFact]
    public void A_pack_with_no_server_is_not_called_singleplayer()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");   // no connect

        Assert.DoesNotContain(VisibleText(window), t => t.Contains("singleplayer"));

        // Nothing is claimed at all: the line is simply absent.
        Assert.False(vm.SelectedPack.HasServer);
        Assert.Equal("", vm.SelectedPack.ServerLine);
        Assert.False(vm.Detail!.HasServer);
    }

    [AvaloniaFact]
    public void A_pack_with_a_server_says_it_will_join_it()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SelectedPack.HasServer);
        Assert.Equal("auto-joins anego.example.com:42420", vm.SelectedPack.ServerLine);

        // Worth knowing before pressing Play that it will drop you straight into a server.
        Assert.Contains(VisibleText(window), t => t.Contains("auto-joins anego.example.com"));
    }

    [AvaloniaFact]
    public void Clearing_the_server_removes_the_line_rather_than_relabelling_it()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.Detail!.EditConnect = "";
        vm.Detail.SaveSettingsCommand.Execute(null);

        Assert.False(vm.Detail.HasServer);
        Assert.False(vm.SelectedPack!.HasServer);
        Assert.Equal("", vm.SelectedPack.ServerLine);
    }

    // ---- the sidebar tracks its packs ----

    /// <summary>
    /// There used to be a Refresh button, and this is what it was really for: the sidebar
    /// row and the detail pane share one manifest instance, and nothing told the row it
    /// had been edited. Refresh rebuilt the list from disk and the staleness disappeared,
    /// which made a missing notification look like a feature.
    /// </summary>
    [AvaloniaFact]
    public void Renaming_a_pack_updates_the_sidebar_without_a_manual_refresh()
    {
        var (window, vm) = Show();
        var sidebar = window.GetVisualDescendants().OfType<ListBox>().First();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        vm.Detail!.EditName = "Renamed Pack";
        vm.Detail.SaveSettingsCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Asserted against the sidebar subtree specifically: the new name is also in the
        // heading and the settings text box, which would satisfy a window-wide search.
        Assert.Contains("Renamed Pack", VisibleText(sidebar));
        Assert.DoesNotContain("Vanilla + QoL", VisibleText(sidebar));
    }

    [AvaloniaFact]
    public void Adding_a_mod_updates_the_sidebars_count()
    {
        var (window, vm) = Show();
        var sidebar = window.GetVisualDescendants().OfType<ListBox>().First();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        Assert.Contains(VisibleText(sidebar), t => t.Contains("1 mod"));

        HitFor(vm.Detail!, "olla", "Olla").AddCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains(VisibleText(sidebar), t => t.Contains("2 mods"));
    }

    [AvaloniaFact]
    public void There_is_no_refresh_button_to_press()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // Every list change now comes from the UI itself, so nothing can go stale.
        Assert.DoesNotContain("Refresh", Buttons(window).Keys);
    }

    // ---- logs ----

    [AvaloniaFact]
    public void Each_pack_keeps_its_own_log()
    {
        var (_, vm) = Show();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.Detail!.SaveSettingsCommand.Execute(null);
        Assert.Contains(vm.Detail.Log, l => l.Contains("anego"));

        // The Log tab sits inside a pack, so one shared collection showed every pack's
        // launches under all of them.
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        Assert.DoesNotContain(vm.Detail!.Log, l => l.Contains("anego"));

        vm.Detail.SaveSettingsCommand.Execute(null);
        Assert.Contains(vm.Detail.Log, l => l.Contains("vanilla-qol"));
    }

    [AvaloniaFact]
    public void A_packs_log_survives_switching_away_and_back()
    {
        var (_, vm) = Show();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.Detail!.SaveSettingsCommand.Execute(null);

        // The detail view model is rebuilt on every selection change, so the logs are
        // held by MainViewModel rather than by it.
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        Assert.Contains(vm.Detail!.Log, l => l.Contains("anego"));
        Assert.DoesNotContain(vm.Detail.Log, l => l.Contains("vanilla-qol"));
    }

    [AvaloniaFact]
    public void A_deleted_packs_log_does_not_haunt_its_replacement()
    {
        var (_, vm) = Show();

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "old-pack");
        vm.Detail!.SaveSettingsCommand.Execute(null);
        Assert.NotEmpty(vm.Detail.Log);

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackName = "old-pack";
        vm.GameVersionChoices.Add("1.22.5");
        vm.NewPackGameVersion = "1.22.5";
        vm.CreatePackCommand.Execute(null);

        Assert.Equal("old-pack", vm.SelectedPack!.Id);
        Assert.Empty(vm.Detail!.Log);
    }

    [AvaloniaFact]
    public void The_log_tab_shows_the_selected_packs_lines()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        vm.Detail!.SaveSettingsCommand.Execute(null);

        // Avalonia resolves binding paths at runtime, so Detail.Log going stale would
        // simply render an empty tab rather than fail anywhere.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Log");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains(VisibleText(window), t => t.Contains("saved settings for 'anego'"));
    }

    [AvaloniaFact]
    public async Task Loading_a_dropdown_leaves_an_unpinned_mod_unpinned()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0"]);
        await detail.Mods.Single(m => m.ModId == "glassview").EnsureReleasesAsync();

        Assert.All(detail.Mods, m => Assert.False(m.IsPinned));
    }
}
