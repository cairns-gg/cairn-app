using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Net;
using Cairn.App.ViewModels;
using Cairn.Core;
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

    private string SystemInstallDir => Path.Combine(_home, "system-install");

    public MainWindowTests()
    {
        WritePack("anego", "Anego Server", "1.22.5", "anego.example.com:42420", ["glassview", "unchisel"]);
        WritePack("vanilla-qol", "Vanilla + QoL", "1.22.5", null, ["glassview"]);
        WritePack("old-pack", "Legacy 1.21 Pack", "1.21.5", null, ["glassview"]);

        // Three installed game versions — a target has to be one the picker offers, because
        // a ComboBox coerces a selection that is not in its list straight back to null.
        // Without these the only source of versions is the
        // catalog, which the tests deliberately keep offline — so anything about offering
        // versions silently depended on the machine happening to have Vintage Story on it,
        // and started failing the day this one did not.
        Games.FakeInstall("1.22.5", Path.Combine(_home, "games", "1.22.5"));
        Games.FakeInstall("1.22.6", Path.Combine(_home, "games", "1.22.6"));
        Games.FakeInstall("1.21.7", Path.Combine(_home, "games", "1.21.7"));

        // And one install Cairn did not make. VINTAGE_STORY is the first thing TryLocate
        // consults, so this pins what "the machine's own install" is instead of inheriting
        // whatever the machine running the tests happens to have.
        Games.FakeInstall("1.22.6", SystemInstallDir);

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
        Environment.SetEnvironmentVariable("VINTAGE_STORY", SystemInstallDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        Environment.SetEnvironmentVariable("VINTAGE_STORY", null);
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

        // The real window confirms destructive actions in a modal dialog, which a headless
        // run cannot answer. Without a confirmer the prompt is left armed instead, which is
        // what these tests inspect; the dialogs have their own tests.
        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        if (vm.Detail is not null) vm.Detail.ConfirmVersionChange = null;

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
    public void An_unusable_game_version_can_no_longer_be_typed_in()
    {
        // "^1.22" used to be accepted into the manifest here and rejected on save. The
        // field is a picker now, so the offer is the validation — GameVersions.IsPlausibleVersion
        // still guards the manifest for packs that arrive by import.
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        var detail = vm.Detail!;

        Assert.All(detail.GameVersionChoices, v => Assert.True(GameVersions.IsPlausibleVersion(v)));

        // And saving the other settings cannot disturb it.
        var before = detail.Manifest.GameVersion;
        detail.EditName = "Renamed";
        detail.SaveSettingsCommand.Execute(null);

        Assert.Equal(before, detail.Manifest.GameVersion);
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
                     "Play", "New pack", "Search", "Save", "Delete pack", "Clear",
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



    /// <summary>
    /// Opens preferences the way the button does, and hands back the view model the app
    /// really builds — the window itself is the view's job, so tests supply the opener.
    /// </summary>
    private static PreferencesViewModel OpenPreferences(MainViewModel vm)
    {
        PreferencesViewModel? captured = null;
        vm.OpenPreferences = p => { captured = p; return Task.CompletedTask; };

        vm.ShowPreferencesCommand.Execute(null);

        Assert.NotNull(captured);
        return captured!;
    }

    [AvaloniaFact]
    public void Game_versions_are_no_longer_a_pack_action()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // They moved into preferences: nothing about them is about the selected pack.
        Assert.DoesNotContain("Game versions", Buttons(window).Keys);
        Assert.Contains("Preferences", Buttons(window).Keys);
    }

    [AvaloniaFact]
    public void Preferences_opens_a_window_of_its_own()
    {
        var (_, vm) = Show();

        var preferences = OpenPreferences(vm);

        var window = new PreferencesWindow { DataContext = preferences };
        window.Show();

        var text = VisibleText(window).ToList();
        Assert.Contains("Storage", text);
        Assert.Contains("Installed", text);
        Assert.Contains("Available", text);
    }

    [AvaloniaFact]
    public void Preferences_still_manages_game_versions_and_runtimes()
    {
        var (_, vm) = Show();
        var window = new PreferencesWindow { DataContext = OpenPreferences(vm) };
        window.Show();

        var buttons = Buttons(window);
        foreach (var label in new[] { "Install", "Refresh list", "Remove", "Install its .NET", "Clean up" })
        {
            Assert.True(buttons.ContainsKey(label), $"no '{label}' button in preferences");
            Assert.NotNull(buttons[label].Command);
        }

        // The caches are swept by Clean up now; a second button for the same idea was one
        // too many for "delete things that come back on their own".
        Assert.DoesNotContain("Clear", buttons.Keys);
    }

    [AvaloniaFact]
    public void Preferences_reports_what_Cairn_is_using()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);

        // The numbers are small here, but they must be present and readable, because
        // "where did my disk go" is the reason this screen exists. Only Cairn's own two
        // installs count — the machine's is not Cairn's disk usage to claim.
        Assert.False(string.IsNullOrWhiteSpace(preferences.TotalSize));
        Assert.False(string.IsNullOrWhiteSpace(preferences.GamesSize));
        Assert.False(string.IsNullOrWhiteSpace(preferences.CacheSize));
        Assert.Equal("3 versions", preferences.GamesDetail);
        Assert.Contains("pack", preferences.PacksDetail);
    }

    [AvaloniaFact]
    public async Task Cleaning_up_asks_before_deleting_and_says_what_goes()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);

        // The fixture installs 1.22.5, 1.22.6 and 1.21.7; packs target 1.22.5 and 1.21.5.
        ConfirmViewModel? asked = null;
        preferences.Confirm = c => { asked = c; return Task.FromResult(false); };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Contains("1.22.6", asked!.Message);
        Assert.Contains("Frees", asked.Message);
        Assert.Equal("Clean up", asked.ConfirmLabel);

        // Said no, so nothing moved.
        Assert.True(Directory.Exists(Path.Combine(_home, "games", "1.22.6")));
    }

    [AvaloniaFact]
    public void A_prompt_raised_from_Preferences_belongs_to_that_window()
    {
        // MainWindow's confirmer is parented to MainWindow, so using it from Preferences
        // dismissed Preferences and brought the main window forward mid-operation.
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);
        preferences.Confirm = null;

        var window = new PreferencesWindow { DataContext = preferences };

        Assert.NotNull(preferences.Confirm);
    }

    [AvaloniaFact]
    public async Task Cleaning_up_reports_progress_and_stops_when_done()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);
        preferences.Confirm = _ => Task.FromResult(true);

        var wasBusy = false;
        var stages = new List<string>();
        preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(preferences.IsCleaningUp) && preferences.IsCleaningUp)
                wasBusy = true;
            if (e.PropertyName == nameof(preferences.CleanupStage) && preferences.CleanupStage.Length > 0)
                stages.Add(preferences.CleanupStage);
        };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        // It said it was working — deleting gigabytes on the UI thread reads as a hang.
        Assert.True(wasBusy, "never reported itself busy");
        Assert.Contains(stages, s => s.Contains("1.22.6"));

        // ...and stopped saying so.
        Assert.False(preferences.IsCleaningUp);
        Assert.Equal("", preferences.CleanupStage);
        Assert.True(preferences.CleanUpCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Cleaning_up_cannot_be_started_twice()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);

        preferences.IsCleaningUp = true;

        Assert.False(preferences.NotCleaningUp);
        Assert.False(preferences.CleanUpCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Cleaning_up_reports_what_it_could_not_remove()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);
        // The confirmation sits between planning and doing, which is exactly the window in
        // which something can disappear. Removing it there must be reported, not thrown.
        preferences.Confirm = _ =>
        {
            Directory.Delete(Path.Combine(_home, "games", "1.22.6"), recursive: true);
            return Task.FromResult(true);
        };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        Assert.Contains("could not remove", preferences.CleanupSummary);
        Assert.Contains("1.22.6", preferences.CleanupSummary);
    }

    [AvaloniaFact]
    public async Task Cleaning_up_keeps_what_packs_target()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);
        preferences.Confirm = _ => Task.FromResult(true);

        await preferences.CleanUpCommand.ExecuteAsync(null);

        // 1.22.5 is the Anego pack's version; 1.22.6 is nobody's.
        Assert.True(Directory.Exists(Path.Combine(_home, "games", "1.22.5")));
        Assert.False(Directory.Exists(Path.Combine(_home, "games", "1.22.6")));
        Assert.Contains("Removed", preferences.CleanupSummary);
    }

    [AvaloniaFact]
    public async Task Cleaning_up_empties_the_caches_too()
    {
        var (_, vm) = Show();

        var icons = Path.Combine(_home, "cache", "icons");
        Directory.CreateDirectory(icons);
        File.WriteAllBytes(Path.Combine(icons, "abc.png"), new byte[4096]);

        var preferences = OpenPreferences(vm);

        ConfirmViewModel? asked = null;
        preferences.Confirm = c => { asked = c; return Task.FromResult(true); };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        // Listed, so nothing is deleted that was not shown first.
        Assert.Contains("cached icons and mod details", asked!.Message);

        // Clear takes the directory with it, so the cached file is gone either way.
        Assert.False(File.Exists(Path.Combine(icons, "abc.png")));
    }

    [AvaloniaFact]
    public async Task Cleaning_up_with_nothing_to_do_says_so_instead_of_asking()
    {
        var (_, vm) = Show();
        var preferences = OpenPreferences(vm);
        preferences.Confirm = _ => Task.FromResult(true);

        await preferences.CleanUpCommand.ExecuteAsync(null);

        // Second run: the unused ones are gone, so there is nothing left to confirm.
        var asked = false;
        preferences.Confirm = _ => { asked = true; return Task.FromResult(true); };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Contains("Nothing to clean up", preferences.CleanupSummary);
    }

    [AvaloniaFact]
    public async Task Cleaning_up_refuses_to_guess_when_a_pack_will_not_load()
    {
        var (_, vm) = Show();
        File.WriteAllText(Path.Combine(_home, "packs", "anego", "pack.json"), "{ not json");

        var preferences = OpenPreferences(vm);
        var asked = false;
        preferences.Confirm = _ => { asked = true; return Task.FromResult(true); };

        await preferences.CleanUpCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Contains("anego", preferences.CleanupSummary);
        Assert.True(Directory.Exists(Path.Combine(_home, "games", "1.22.6")));
    }

    [AvaloniaTheory]
    [InlineData(0, "0 B")]
    [InlineData(900, "900 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(29L * 1024 * 1024, "29 MB")]
    [InlineData(6_334_115_840L, "5.9 GB")]
    public void Sizes_are_reported_the_way_people_read_them(long bytes, string expected)
    {
        // A game version is gigabytes and a cache is kilobytes, so one unit will not do —
        // and "6334115840" answers nobody's question about where their disk went.
        Assert.Equal(expected, PreferencesViewModel.Human(bytes));
    }

    [AvaloniaFact]
    public void An_install_whose_version_cannot_be_read_is_not_offered_as_a_target()
    {
        // The fixture's system install has no readable assembly, so it reports "unknown" —
        // which is a fine thing to list, and not a thing a pack can be pointed at.
        var (_, vm) = Show();

        Assert.Contains(vm.Games.Installed, g => !g.IsManaged);
        Assert.DoesNotContain("unknown", vm.GameVersionChoices);
        Assert.All(vm.GameVersionChoices, v => Assert.True(GameVersions.IsPlausibleVersion(v)));
    }

    /// <summary>A GamesViewModel with a made-up system install, and no network.</summary>
    private Games.Fixture NewGames(GameInstall? system, params string[] packsUsingIt) =>
        new(_home, system, packsUsingIt);

    [AvaloniaFact]
    public void The_machines_own_install_is_listed_next_to_Cairns()
    {
        // The bug this pins: removing a managed 1.22.5 while a system 1.22.5 existed made
        // the version vanish from this list, even though packs went on launching from it —
        // GameLibrary.ForVersion falls back to the system install. A list that disagrees
        // with what actually runs is worse than no list.
        using var games = NewGames(Games.FakeInstall("1.22.5", Path.Combine(_home, "elsewhere")));

        var listed = games.Vm.Installed.Single();

        Assert.Equal("1.22.5", listed.Version);
        Assert.False(listed.IsManaged);
        Assert.Equal("found on this machine", listed.Origin);
    }

    [AvaloniaFact]
    public void Cairn_will_not_delete_an_install_it_did_not_make()
    {
        using var games = NewGames(Games.FakeInstall("1.22.5", Path.Combine(_home, "elsewhere")));
        games.Vm.SelectedInstalled = games.Vm.Installed.Single();

        Assert.False(games.Vm.RequestRemoveCommand.CanExecute(null));

        // And calling it anyway is a no-op rather than a deletion.
        games.Vm.RemoveSelectedCommand.Execute(null);
        Assert.True(Directory.Exists(Path.Combine(_home, "elsewhere")));
    }

    [AvaloniaFact]
    public void A_system_install_is_not_listed_twice_when_Cairn_manages_the_same_directory()
    {
        var root = Path.Combine(_home, "shared-games");
        var dir = Path.Combine(root, "1.21.7");

        // The same directory reached two ways: as a managed install, and as "the system one".
        using var games = new Games.Fixture(_home, Games.FakeInstall("1.21.7", dir), [], storeRoot: root);
        games.AddManaged("1.21.7");

        Assert.Single(games.Vm.Installed);
        Assert.True(games.Vm.Installed.Single().IsManaged);
    }

    [AvaloniaFact]
    public void Removing_a_version_a_pack_uses_names_the_pack()
    {
        using var games = NewGames(system: null, "Anego Server");
        var dir = games.AddManaged("1.21.7");
        games.Vm.SelectedInstalled = games.Managed(dir);

        games.Vm.RequestRemoveCommand.Execute(null);

        // Armed, not done: the files are still there.
        Assert.True(games.Vm.ConfirmingRemove);
        Assert.True(Directory.Exists(dir));

        // Named, not counted — which pack is the actual question.
        Assert.Contains("Anego Server", games.Vm.RemoveConsequence);
        Assert.Contains("download it again", games.Vm.RemoveConsequence);

        games.Vm.RemoveSelectedCommand.Execute(null);

        Assert.False(games.Vm.ConfirmingRemove);
        Assert.False(Directory.Exists(dir));
    }

    [AvaloniaTheory]
    [InlineData(new[] { "A" }, "“A” targets")]
    [InlineData(new[] { "A", "B" }, "“A” and “B” target")]
    [InlineData(new[] { "A", "B", "C", "D" }, "“A”, “B” and 2 more target")]
    public void The_prompt_names_a_few_packs_and_counts_the_rest(string[] packs, string expected)
    {
        using var games = NewGames(system: null, packs);
        games.Vm.SelectedInstalled = games.Managed(games.AddManaged("1.21.7"));

        games.Vm.RequestRemoveCommand.Execute(null);

        Assert.Contains(expected, games.Vm.RemoveConsequence);
    }

    [AvaloniaFact]
    public void Removing_a_version_nothing_uses_says_so_plainly()
    {
        using var games = NewGames(system: null);
        games.Vm.SelectedInstalled = games.Managed(games.AddManaged("1.21.7"));

        games.Vm.RequestRemoveCommand.Execute(null);

        // The prompt gets heavier only when the cost is real.
        Assert.Contains("No pack targets it", games.Vm.RemoveConsequence);
    }

    [AvaloniaFact]
    public void Removal_deletes_the_directory_it_listed_not_one_named_after_the_version()
    {
        // A directory whose name is not a version, so the store cannot fall back to it
        // either: the install reports "unknown", and deriving the path back from that would
        // delete nothing — and still log that it had.
        using var games = NewGames(system: null);
        var dir = games.AddManagedAt("nightly-build");
        var listed = games.Managed(dir);

        Assert.Equal("unknown", listed.Version);

        games.Vm.SelectedInstalled = listed;
        games.Vm.RequestRemoveCommand.Execute(null);
        games.Vm.RemoveSelectedCommand.Execute(null);

        Assert.False(Directory.Exists(dir));
        Assert.Empty(games.Vm.Installed);
    }

    [AvaloniaFact]
    public void Changing_the_selection_disarms_the_confirmation()
    {
        using var games = NewGames(system: null);
        games.AddManaged("1.21.7");
        var second = games.AddManaged("1.22.5");

        games.Vm.SelectedInstalled = games.Vm.Installed[0];
        games.Vm.RequestRemoveCommand.Execute(null);
        Assert.True(games.Vm.ConfirmingRemove);

        // Otherwise the armed prompt would carry over onto a version nobody chose.
        games.Vm.SelectedInstalled = games.Managed(second);
        Assert.False(games.Vm.ConfirmingRemove);
    }

    // ---- the game's own log ----

    private string LogsDirFor(string packId) =>
        Path.Combine(_home, "packs", packId, "data", "Logs");

    private void WriteGameLog(string packId, params string[] lines)
    {
        Directory.CreateDirectory(LogsDirFor(packId));
        File.WriteAllLines(Path.Combine(LogsDirFor(packId), "client-main.log"), lines);
    }

    [AvaloniaFact]
    public void The_log_tab_offers_the_games_log_as_well_as_Cairns()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Log");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var buttons = Buttons(window);

        foreach (var label in new[] { "Clear", "Game log", "Open logs folder" })
        {
            Assert.True(buttons.ContainsKey(label), $"no '{label}' button in the Log tab");
            Assert.NotNull(buttons[label].Command);
        }
    }

    [AvaloniaFact]
    public void Showing_the_game_log_puts_it_in_the_pane()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        WriteGameLog("anego",
            "29.7.2026 20:02:31 [Notification] Loading mods",
            "29.7.2026 20:02:32 [Error] Failed to load mod olla");

        vm.Detail!.ShowGameLogCommand.Execute(null);

        Assert.Contains(vm.Detail.Log, l => l.Contains("client-main.log"));
        Assert.Contains(vm.Detail.Log, l => l.Contains("Failed to load mod olla"));
    }

    [AvaloniaFact]
    public void Asking_for_a_log_that_is_not_there_says_so_rather_than_nothing()
    {
        // Silence would read as "there is nothing wrong", which is the opposite of true.
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        vm.Detail!.ShowGameLogCommand.Execute(null);

        Assert.Contains(vm.Detail.Log, l => l.Contains("not been launched"));
    }

    [AvaloniaFact]
    public void The_logs_button_survives_a_pack_that_has_never_run()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        // No Logs directory at all: this must report, not throw.
        vm.Detail!.OpenLogsFolderCommand.Execute(null);

        Assert.Contains(vm.Detail.Log, l => l.Contains("could not open"));
    }

    // ---- changing the game version ----

    /// <summary>
    /// Serves one mod, "glassview", with a 1.0.0 marked for 1.22.5 and a 2.0.0 for 1.22.6.
    /// Everything else is a 404, so the fixture's other mods read as unavailable.
    /// </summary>
    private sealed class RetargetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (url.Contains("/api/mod/glassview"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GlassviewJson),
                });

            // A mod ModDB does not have: HTTP 200 carrying a status code, which is what
            // ModDB actually answers. Distinct from the endpoint being unreachable, and the
            // difference is what separates "this mod breaks" from "could not check".
            if (url.Contains("/api/mod/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"statuscode":"404"}"""),
                });

            // Everything else — the game catalog — is simply not reachable.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private const string GlassviewJson = """
            {"statuscode":"200","mod":{
              "modid":1,"assetid":2,"name":"Glass View","urlalias":"glassview","side":"client",
              "releases":[
                {"releaseid":2,"fileid":2,"modidstr":"glassview","modversion":"2.0.0",
                 "filename":"glassview_2.0.0.zip",
                 "mainfile":"https://moddbcdn.vintagestory.at/glassview_2.0.0.zip","tags":["1.22.6"]},
                {"releaseid":1,"fileid":1,"modidstr":"glassview","modversion":"1.0.0",
                 "filename":"glassview_1.0.0.zip",
                 "mainfile":"https://moddbcdn.vintagestory.at/glassview_1.0.0.zip","tags":["1.22.5"]}
              ]
            }}
            """;
    }

    private static (MainWindow Window, MainViewModel Vm) ShowWithModDb()
    {
        var vm = new MainViewModel(new RetargetHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // As in Show(): no modal dialogs in a headless run.
        vm.Confirm = null;
        vm.ConfirmVersionChange = null;
        if (vm.Detail is not null) vm.Detail.ConfirmVersionChange = null;

        return (window, vm);
    }

    /// <summary>
    /// Selects a pack and waits for its version picker to fill. The list loads in the
    /// background, and a ComboBox coerces a selection that is not yet in it back to null —
    /// so a test that raced the load would silently be choosing nothing.
    /// </summary>
    private static async Task<PackDetailViewModel> Retargetable(MainViewModel vm, string id = "vanilla-qol")
    {
        vm.SelectedPack = vm.Packs.Single(p => p.Id == id);
        var detail = vm.Detail!;
        detail.ConfirmVersionChange = null;

        await detail.LoadGameVersionsAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return detail;
    }

    [AvaloniaFact]
    public async Task The_version_picker_starts_on_what_the_pack_already_targets()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        Assert.Equal("1.22.5", detail.TargetGameVersion);
        Assert.Contains("1.22.5", detail.GameVersionChoices);

        // Nothing to check until a different version is chosen.
        Assert.False(detail.CanCheckVersion);
        Assert.False(detail.CheckVersionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Checking_a_version_writes_nothing()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);
        var manifestPath = Path.Combine(_home, "packs", "vanilla-qol", "pack.json");
        var before = File.ReadAllText(manifestPath);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        Assert.NotNull(detail.VersionChange);

        // The entire point of the step: the pack is untouched until Apply.
        Assert.Equal(before, File.ReadAllText(manifestPath));
        Assert.Equal("1.22.5", detail.Manifest.GameVersion);
    }

    [AvaloniaFact]
    public async Task The_check_says_which_mods_move_and_which_break()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        var glassview = detail.VersionChange!.Mods.Single(m => m.ModId == "glassview");
        Assert.Equal("updates", glassview.Label);
        Assert.Contains("2.0.0", glassview.Note);
    }

    [AvaloniaFact]
    public async Task A_mod_with_nothing_published_for_the_target_is_shown_first_and_marked()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm, "anego");   // glassview + unchisel

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        var change = detail.VersionChange!;
        Assert.True(change.AnythingBreaks);

        // Worst first: the reason to say no should not need scrolling to.
        Assert.Equal("unchisel", change.Mods[0].ModId);
        Assert.True(change.Mods[0].Breaks);
        Assert.Contains("nothing published for 1.22.6", change.BreakWarning);
    }

    [AvaloniaFact]
    public async Task Applying_is_what_finally_changes_the_pack()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);
        detail.ApplyVersionChangeCommand.Execute(null);

        Assert.Equal("1.22.6", detail.Manifest.GameVersion);
        Assert.Contains("1.22.6",
            File.ReadAllText(Path.Combine(_home, "packs", "vanilla-qol", "pack.json")));

        // The confirmation is gone, and there is nothing left to apply twice.
        Assert.Null(detail.VersionChange);
        Assert.False(detail.CanCheckVersion);
    }

    [AvaloniaFact]
    public async Task Cancelling_leaves_the_pack_where_it_was()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);
        detail.CancelVersionChangeCommand.Execute(null);

        Assert.Null(detail.VersionChange);
        Assert.Equal("1.22.5", detail.Manifest.GameVersion);
    }

    [AvaloniaFact]
    public async Task Choosing_a_different_target_discards_the_answer_about_the_old_one()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);
        Assert.NotNull(detail.VersionChange);

        // Otherwise Apply would commit a version nobody checked.
        detail.TargetGameVersion = "1.21.7";
        Assert.Null(detail.VersionChange);
    }

    [AvaloniaFact]
    public async Task Downgrading_a_pack_with_worlds_warns_about_them()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        // Give the pack its own data, with a world in it.
        var saves = Path.Combine(_home, "packs", "vanilla-qol", "data", "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "Homestead.vcdbs"), new byte[64]);

        detail.TargetGameVersion = "1.21.7";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        var change = detail.VersionChange!;
        Assert.True(change.RisksWorlds);
        Assert.Contains("Homestead", change.WorldWarning);
    }

    [AvaloniaFact]
    public void The_pack_selected_at_startup_can_still_open_the_confirmation()
    {
        // The regression that shipped: MainViewModel's constructor selects a pack, so the
        // first PackDetailViewModel is built before the window assigns its dialog hook.
        // Copying the hook once at construction left that pack — the one you land on —
        // silently unable to confirm anything, and Check just wrote a log line.
        var vm = new MainViewModel(new RetargetHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.NotNull(vm.SelectedPack);
        Assert.NotNull(vm.Detail);
        Assert.NotNull(vm.Detail!.ConfirmVersionChange);
    }

    [AvaloniaFact]
    public async Task Checking_asks_the_view_to_confirm_rather_than_applying_by_itself()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        VersionChangeViewModel? shown = null;
        detail.ConfirmVersionChange = change => { shown = change; return Task.FromResult(true); };

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        // It was handed the plan...
        Assert.NotNull(shown);
        Assert.Equal("1.22.6", shown!.Plan.To);

        // ...and only the "yes" applied it.
        Assert.Equal("1.22.6", detail.Manifest.GameVersion);
    }

    [AvaloniaFact]
    public async Task Saying_no_to_the_confirmation_changes_nothing()
    {
        var (_, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.ConfirmVersionChange = _ => Task.FromResult(false);

        detail.TargetGameVersion = "1.22.6";
        await detail.CheckVersionCommand.ExecuteAsync(null);

        Assert.Equal("1.22.5", detail.Manifest.GameVersion);
        Assert.Null(detail.VersionChange);
    }

    [AvaloniaFact]
    public async Task The_picker_and_its_check_button_sit_in_the_settings_tab()
    {
        var (window, vm) = ShowWithModDb();
        var detail = await Retargetable(vm);

        detail.TargetGameVersion = "1.22.6";
        ShowSettingsTab(window);

        // The verdicts themselves are a dialog now; see VersionChangeWindowTests.
        Assert.True(Buttons(window).ContainsKey("Check…"));
        Assert.Contains(VisibleText(window), t => t.Contains("Game version"));
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
    public void Delete_lives_with_the_pack_it_deletes()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.First();

        // Not in the sidebar: that is for choosing a pack and app-level actions, and a
        // destructive button sitting beside "New pack" was one slip from a bad day.
        Assert.DoesNotContain("Delete", Buttons(window).Keys);

        ShowSettingsTab(window);

        var buttons = Buttons(window);
        Assert.True(buttons.ContainsKey("Delete pack"), "no Delete pack button in Settings");
        Assert.NotNull(buttons["Delete pack"].Command);
    }

    /// <summary>A TabControl only realises the selected tab, so its contents need showing.</summary>
    private static void ShowSettingsTab(Visual window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Settings");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Delete_asks_before_destroying_anything()
    {
        var (window, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        ShowSettingsTab(window);
        vm.RequestDeleteCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Nothing is gone yet — it only armed the confirmation.
        Assert.True(vm.ConfirmingDelete);
        Assert.Equal(3, vm.Packs.Count);
        Assert.True(Directory.Exists(Path.Combine(_home, "packs", "anego")));

        // And it itemises what would go, with what the disk gets back.
        Assert.Equal("Anego Server", vm.DeleteTargetName);
        Assert.Contains("Frees", vm.DeleteConsequence);
        Assert.Contains("cannot be undone", vm.DeleteConsequence);
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
    public void Delete_reaches_a_confirmation_dialog_from_a_freshly_shown_window()
    {
        // The wiring, not the prompt: a hook that never arrives means Delete does nothing
        // at all, which is how the version-change dialog shipped broken.
        var vm = new MainViewModel(new OfflineHandler());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.NotNull(vm.Confirm);
    }

    [AvaloniaFact]
    public async Task Delete_destroys_the_pack_only_when_the_dialog_says_yes()
    {
        var (_, vm) = Show();
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "anego");

        ConfirmViewModel? asked = null;
        vm.Confirm = c => { asked = c; return Task.FromResult(false); };

        await vm.RequestDeleteCommand.ExecuteAsync(null);

        // It asked, naming the pack — and took "no" for an answer.
        Assert.NotNull(asked);
        Assert.Contains("Anego Server", asked!.Title);
        Assert.Equal("Delete pack", asked.ConfirmLabel);
        Assert.True(Directory.Exists(Path.Combine(_home, "packs", "anego")));

        vm.Confirm = _ => Task.FromResult(true);
        await vm.RequestDeleteCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(_home, "packs", "anego")));
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
    public void The_preferences_bar_animates_on_the_same_rule()
    {
        var (_, vm) = Show();
        var window = new PreferencesWindow { DataContext = OpenPreferences(vm) };
        window.Show();

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
