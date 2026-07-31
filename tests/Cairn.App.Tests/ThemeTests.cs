using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.Core.ModDb;
using Cairn.App.Views;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Checks the Vintage Story styling is actually in effect. A style whose selector matches
/// nothing fails silently — the app still runs, just wearing default Fluent — so these
/// assert on resolved brushes rather than on the styles existing.
///
/// Colours come from the game's Vintagestory.API.Client.GuiStyle.
/// </summary>
public class ThemeTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-theme-" + Guid.NewGuid().ToString("n")[..8]);

    public ThemeTests()
    {
        var dir = Path.Combine(_home, "packs", "anego");
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));
        File.WriteAllText(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(new
        {
            id = "anego", name = "Anego Server", gameVersion = "1.22.5",
            connect = "host:42420", mods = new[] { new { modid = "glassview" } },
        }));

        // A downloaded mod and a couple of worlds, so the delete prompt shows the case
        // worth seeing rather than an empty pack.
        File.WriteAllBytes(Path.Combine(dir, "Mods", "glassview_1.3.0.zip"), new byte[86_000]);
        var saves = Path.Combine(dir, "data", "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "Homestead.vcdbs"), new byte[412 * 1024 * 1024 / 2]);
        File.WriteAllBytes(Path.Combine(saves, "Test Flats.vcdbs"), new byte[9_000_000]);

        // Two versions to move between, so the screenshots show a real picker.
        Games.FakeInstall("1.22.5", Path.Combine(_home, "games", "1.22.5"));
        Games.FakeInstall("1.22.6", Path.Combine(_home, "games", "1.22.6"));

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static MainWindow Show()
    {
        var window = new MainWindow { DataContext = new MainViewModel(new OfflineHandler()) };
        window.Show();
        return window;
    }

    private static Color? Of(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    /// <summary>
    /// The colour a button actually paints. Asserting on Button.Background is not enough:
    /// Fluent sets the background on the template's ContentPresenter, so a button can
    /// report rust and render blue.
    /// </summary>
    private static Color? RenderedBackground(Button button) =>
        Of(button.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter")?.Background);

    [AvaloniaFact]
    public void The_window_uses_the_games_dark_brown_rather_than_fluent_default()
    {
        var window = Show();

        // #241C19 — between GuiStyle's ColorRot4 and ColorRot5.
        Assert.Equal(Color.Parse("#241C19"), Of(window.Background));
    }

    [AvaloniaFact]
    public void Buttons_use_the_games_button_colours()
    {
        var window = Show();

        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == "New pack");

        // GuiStyle.ButtonBackColor rgb(69,52,36); ButtonTextColor rgb(224,207,187).
        Assert.Equal(Color.Parse("#453424"), RenderedBackground(button));
        Assert.Equal(Color.Parse("#E0CFBB"), Of(button.Foreground));

        // GuiStyle sets ElementBGRadius to 1 — the game's corners are square.
        Assert.True(button.CornerRadius.TopLeft <= 1);
    }

    [AvaloniaFact]
    public void The_primary_action_uses_the_rust_accent()
    {
        var window = Show();
        var vm = (MainViewModel)window.DataContext!;
        vm.SelectedPack = vm.Packs.First();

        var play = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == "Play");

        // Derived from GuiStyle.ColorRust1 #D05B0C. Asserted on what is painted, because
        // Fluent's own .accent style targets the presenter and would otherwise win.
        Assert.Equal(Color.Parse("#A34509"), RenderedBackground(play));
    }

    [AvaloniaFact]
    public void Body_text_is_the_games_parchment_ink()
    {
        var window = Show();

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text));

        // GuiStyle.DialogDefaultTextColor #E9DDCE.
        Assert.Equal(Color.Parse("#E9DDCE"), Of(text.Foreground));
    }

    [AvaloniaFact]
    public void Headings_use_the_decorative_serif_the_game_uses()
    {
        var window = Show();
        var vm = (MainViewModel)window.DataContext!;
        vm.SelectedPack = vm.Packs.First();

        var heading = window.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("heading") && t.IsEffectivelyVisible);

        // GuiStyle.DecorativeFontName is "Lora"; the rest are fallbacks for machines
        // without it, since the game's font files are not redistributed here.
        Assert.Contains("Lora", heading.FontFamily.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static SearchHitViewModel Hit(
        string modId, string name, string summary, Avalonia.Media.Imaging.Bitmap? icon,
        bool compatible = true, bool alreadyInPack = false)
        => new(new ModSearchResult(Entry(modId, name, summary), compatible),
               "1.22.x", alreadyInPack) { Icon = icon };

    private static ModDbSearchEntry Entry(string modId, string name, string summary) => new()
    {
        Name = name, ModIdStrs = [modId], Side = "client", Downloads = 2172,
        Author = "dizzyd", AssetId = 34157, UrlAlias = modId, Summary = summary,
        Tags = ["Technology", "QoL"],
    };

    [AvaloniaFact]
    public void The_window_renders_a_frame()
    {
        var window = Show();
        var vm = (MainViewModel)window.DataContext!;

        // Set CAIRN_SHOT_DIR to keep the renders for eyeballing.
        var outDir = Environment.GetEnvironmentVariable("CAIRN_SHOT_DIR") ?? _home;
        Directory.CreateDirectory(outDir);

        void Shot(string name)
        {
            // Bindings settle on the next layout pass; capturing straight after a state
            // change photographs a half-updated window.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var file = File.Create(Path.Combine(outDir, name + ".png"));
            frame!.Save(file, new PngBitmapEncoderOptions());
        }

        vm.SelectedPack = vm.Packs.First();

        // Pack rows fetch their icons from ModDB, which the tests do not reach, so stand
        // one in to show the row as it renders.
        using (var packIcon = Avalonia.Platform.AssetLoader.Open(
                   new Uri("avares://cairn/Assets/cairn.ico")))
        {
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(packIcon, 96);
            foreach (var row in vm.Detail!.Mods)
            {
                row.Icon = bitmap;
                row.Name = row.ModId switch
                {
                    "glassview" => "Glassview",
                    "unchisel" => "unchisel",
                    _ => row.ModId,
                };
            }
        }

        Shot("01-pack");

        // What a completed update check leaves behind.
        vm.Detail!.Mods.First().UpdateAvailable = "1.4.0";
        vm.Detail.UpdateSummary = "1 update available.";
        Shot("11-updates");
        vm.Detail.Mods.First().UpdateAvailable = null;
        vm.Detail.UpdateSummary = null;

        // Search results, with icons standing in for ModDB's. Real ones are 480x480 PNGs
        // decoded down on the way in; these come from the app's own icon so the shot shows
        // rows as they render rather than empty wells.
        using (var iconStream = Avalonia.Platform.AssetLoader.Open(
                   new Uri("avares://cairn/Assets/cairn.ico")))
        {
            var icon = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(iconStream, 96);
            var detail = vm.Detail!;

            detail.SearchText = "farming";
            detail.ShowResults("farming",
            [
                Hit("olla", "Olla", "Ancient irrigation for your farmland", icon),
                Hit("glassview", "Glassview", "See through your glass blocks", icon,
                    alreadyInPack: true),
                Hit("ancientmod", "Ancient Mod", "Not updated since 1.19", null,
                    compatible: false),
            ]);

            Shot("09-search");

            detail.ClearSearchCommand.Execute(null);
        }

        // The destructive-action dialog, now that the delete prompt is not inline.
        {
            var confirm = new ConfirmWindow
            {
                // The real strings, so this cannot drift from what the app says.
                DataContext = new ConfirmViewModel(
                    $"Delete \u201c{vm.DeleteTargetName}\u201d?", vm.DeleteConsequence, "Delete pack"),
            };
            confirm.Show();

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using (var frame = confirm.CaptureRenderedFrame())
            {
                using var file = File.Create(Path.Combine(outDir, "06-delete-confirm.png"));
                frame!.Save(file, new PngBitmapEncoderOptions());
            }

            confirm.Close();
        }

        // Armed removal: only ever visible mid-interaction.
        vm.Detail!.Mods.First().RequestRemoveCommand.Execute(null);
        Shot("10-remove-confirm");
        vm.Detail.Mods.First().CancelRemoveCommand.Execute(null);

        vm.Detail!.IsLaunching = true;
        vm.Detail.LaunchStage = "Mods: glassview 1.3.0";
        Shot("08-launching");
        vm.Detail.IsLaunching = false;
        vm.Detail.LaunchStage = "";

        // Settings holds Save / Delete pack / Export.
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        var settings = tabs?.GetVisualDescendants().OfType<TabItem>()
            .FirstOrDefault(t => (t.Header as string) == "Settings");
        if (tabs is not null && settings is not null)
        {
            tabs.SelectedItem = settings;
            Shot("05-settings");

            // The retarget confirmation: the whole point of Check → Apply.
            vm.Detail.LoadGameVersionsAsync().GetAwaiter().GetResult();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            vm.Detail.TargetGameVersion =
                vm.Detail.GameVersionChoices.FirstOrDefault(v => v != vm.Detail.Manifest.GameVersion);

            if (vm.Detail.TargetGameVersion is not null)
            {
                // The check opens a modal dialog in the real app, which would block here.
                vm.Detail.ConfirmVersionChange = null;
                vm.Detail.CheckVersionCommand.Execute(null);

                if (vm.Detail.VersionChange is not null)
                {
                    var dialog = new VersionChangeWindow { DataContext = vm.Detail.VersionChange };
                    dialog.Show();

                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    using (var frame = dialog.CaptureRenderedFrame())
                    {
                        using var file = File.Create(Path.Combine(outDir, "05b-version-change.png"));
                        frame!.Save(file, new PngBitmapEncoderOptions());
                    }

                    dialog.Close();
                }
            }
        }

        vm.BeginCreateCommand.Execute(null);
        Shot("02-new-pack");

        vm.CancelCreateCommand.Execute(null);

        // Preferences is its own window now, so it needs its own shot.
        PreferencesViewModel? preferences = null;
        vm.OpenPreferences = pref => { preferences = pref; return System.Threading.Tasks.Task.CompletedTask; };
        vm.ShowPreferencesCommand.Execute(null);

        if (preferences is not null)
        {
            var prefs = new PreferencesWindow { DataContext = preferences };
            prefs.Show();

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using (var frame = prefs.CaptureRenderedFrame())
            {
                using var file = File.Create(Path.Combine(outDir, "03-preferences.png"));
                frame!.Save(file, new PngBitmapEncoderOptions());
            }

            prefs.Close();
        }

        vm.BeginImportCommand.Execute(null);
        Shot("04-import");

        vm.CancelImportCommand.Execute(null);
        vm.ProvisioningVersion = vm.Detail?.Manifest.GameVersion;
        vm.Provisioning = true;
        vm.ProvisionStatus = "downloading Vintage Story 1.22.5 — 214 MB (34%)";
        vm.ProvisionIndeterminate = false;
        vm.ProvisionFraction = 0.34;
        Shot("07-provisioning");

        // The step after the download knows no fraction and runs for minutes.
        vm.ProvisionStatus = "installing Vintage Story 1.22.5 — 412 MB written";
        vm.ProvisionIndeterminate = true;
        Shot("09-provisioning-indeterminate");

        Assert.True(File.Exists(Path.Combine(outDir, "01-pack.png")));
    }
}
