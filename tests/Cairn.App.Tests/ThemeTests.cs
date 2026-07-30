using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
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

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static MainWindow Show()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
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
        Shot("01-pack");

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
        }

        vm.BeginCreateCommand.Execute(null);
        Shot("02-new-pack");

        vm.CancelCreateCommand.Execute(null);
        vm.ShowGameVersionsCommand.Execute(null);
        Shot("03-games");

        vm.ShowPacksCommand.Execute(null);
        vm.BeginImportCommand.Execute(null);
        Shot("04-import");

        vm.CancelImportCommand.Execute(null);
        vm.RequestDeleteCommand.Execute(null);
        Shot("06-delete-confirm");

        vm.CancelDeleteCommand.Execute(null);
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
