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
        var vm = new MainViewModel();
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

    private static SearchHitViewModel Hit(string modId, string name) =>
        new(new ModDbSearchEntry { Name = name, ModIdStrs = [modId], Side = "client", Downloads = 1 });

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

        // Nothing is installed in the fixture, so every row reports that.
        Assert.All(vm.Detail.Mods, m => Assert.False(m.IsInstalled));
        Assert.Contains("not installed", text);
    }


    // ---- creating packs from the UI ----

    [AvaloniaFact]
    public void A_pack_can_be_created_entirely_through_the_ui()
    {
        var (window, vm) = Show();

        Buttons(window)["New pack"].Command!.Execute(null);
        Assert.True(vm.ShowCreate);
        Assert.False(vm.ShowDetail);

        vm.NewPackId = "fresh-pack";
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
    public void A_pack_id_that_would_escape_the_store_is_refused()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackId = "../../../etc/evil";
        vm.CreatePackCommand.Execute(null);

        Assert.NotNull(vm.NewPackError);
        Assert.True(vm.IsCreating, "should stay on the form so the id can be corrected");
        Assert.DoesNotContain(vm.Packs, p => p.Id.Contains("evil"));
        Assert.False(Directory.Exists(Path.Combine(_home, "packs", "etc")));
    }

    [AvaloniaFact]
    public void A_duplicate_pack_id_is_refused()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackId = "anego";
        vm.CreatePackCommand.Execute(null);

        Assert.NotNull(vm.NewPackError);
        Assert.Contains("already exists", vm.NewPackError!);
        Assert.Equal(3, vm.Packs.Count);
    }

    [AvaloniaFact]
    public void An_unusable_game_version_is_refused_when_creating()
    {
        var (_, vm) = Show();

        vm.BeginCreateCommand.Execute(null);
        vm.NewPackId = "bad-version";
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

        detail.SelectedHit = Hit("olla", "Olla");
        detail.AddSelectedCommand.Execute(null);

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

        detail.SelectedHit = Hit("glassview", "Glassview");
        detail.AddSelectedCommand.Execute(null);

        Assert.NotNull(detail.Error);
        Assert.Single(detail.Mods, m => m.ModId == "glassview");
    }

    [AvaloniaFact]
    public void Removing_a_mod_updates_the_manifest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "unchisel");
        detail.RemoveSelectedCommand.Execute(null);

        Assert.DoesNotContain(detail.Mods, m => m.ModId == "unchisel");
        var onDisk = File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json"));
        Assert.DoesNotContain("unchisel", onDisk);
    }

    [AvaloniaFact]
    public void Pinning_and_unpinning_a_mod_round_trips_through_the_manifest()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        // Versions are normally fetched when the mod is selected; prime the cache so the
        // test does not depend on the network.
        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");

        // Choosing a version pins it — no separate Pin button.
        detail.SelectedRelease = "1.3.0";

        var pinned = detail.Mods.Single(m => m.ModId == "glassview");
        Assert.True(pinned.IsPinned);
        Assert.Equal("1.3.0", pinned.PinDisplay);
        Assert.Contains("1.3.0", File.ReadAllText(Path.Combine(_home, "packs", "anego", "pack.json")));

        // And choosing "newest" unpins it again.
        detail.SelectedRelease = PackDetailViewModel.TrackNewest;

        Assert.Equal("newest", detail.Mods.Single(m => m.ModId == "glassview").PinDisplay);
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
        Assert.Contains("joins newhost:42420", detail.ServerLine);

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
                     "Play", "Sync only", "New pack",
                     "Remove",
                     "Search", "Add selected", "Save", "Delete", "Clear",
                 })
        {
            Assert.True(found.ContainsKey(label), $"no '{label}' button in the window");
            Assert.NotNull(found[label].Command);
        }
    }

    [AvaloniaFact]
    public void All_four_tabs_are_present()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        var headers = window.GetVisualDescendants().OfType<TabItem>()
            .Select(t => t.Header as string)
            .ToList();

        Assert.Contains("Mods", headers);
        Assert.Contains("Add mods", headers);
        Assert.Contains("Settings", headers);
        Assert.Contains("Log", headers);
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

        vm.NewPackId = "fetches";

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
        vm.NewPackId = "no-version";
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
    public void Selecting_a_mod_offers_its_versions_without_a_separate_click()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");

        // "newest" plus the compatible releases, populated by selection alone.
        Assert.Equal([PackDetailViewModel.TrackNewest, "1.3.0", "1.2.0"], detail.ReleaseChoices);
        Assert.Equal(PackDetailViewModel.TrackNewest, detail.SelectedRelease);

        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.IsEffectivelyVisible && c.ItemCount > 0);
        Assert.Equal(PackDetailViewModel.TrackNewest, combo.SelectedItem);
    }

    [AvaloniaFact]
    public void An_already_pinned_mod_shows_its_pin_as_the_selection()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0", "1.2.0"]);
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");
        detail.SelectedRelease = "1.2.0";

        // Re-selecting must show the pin, not reset the dropdown to "newest".
        detail.SelectedMod = null;
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");

        Assert.Equal("1.2.0", detail.SelectedRelease);
        Assert.True(detail.Mods.Single(m => m.ModId == "glassview").IsPinned);
    }

    [AvaloniaFact]
    public void Reselecting_after_a_pin_does_not_go_back_to_the_network()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0"]);
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");
        detail.SelectedRelease = "1.3.0";

        // Pinning rewrites the manifest and rebuilds the rows, which re-selects the mod.
        // Without the cache that would fire a fresh lookup every time.
        Assert.False(detail.LoadingReleases);
        Assert.Contains("1.3.0", detail.ReleaseChoices);
    }

    [AvaloniaFact]
    public void Switching_mods_does_not_pin_the_previous_ones_version()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0"]);
        detail.CacheReleaseChoices("unchisel", ["1.2.0"]);

        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");
        detail.SelectedRelease = "1.3.0";

        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "unchisel");

        // Repopulating the dropdown must not be mistaken for the user choosing a version.
        Assert.False(detail.Mods.Single(m => m.ModId == "unchisel").IsPinned);
        Assert.True(detail.Mods.Single(m => m.ModId == "glassview").IsPinned);
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

        vm.Detail!.SelectedHit = Hit("olla", "Olla");
        vm.Detail.AddSelectedCommand.Execute(null);
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
        vm.NewPackId = "old-pack";
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
    public void Clearing_the_selection_empties_the_dropdown_without_pinning()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");
        var detail = vm.Detail!;

        detail.CacheReleaseChoices("glassview", ["1.3.0"]);
        detail.SelectedMod = detail.Mods.Single(m => m.ModId == "glassview");
        detail.SelectedMod = null;

        Assert.Empty(detail.ReleaseChoices);
        Assert.Null(detail.SelectedRelease);
        Assert.All(detail.Mods, m => Assert.False(m.IsPinned));
    }
}
