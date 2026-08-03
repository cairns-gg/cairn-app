using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Captures the screenshots a store listing needs — the ModDB entry, the site — rather
/// than the ones a theme change needs. <see cref="ThemeTests"/> already renders frames,
/// but from the single sparse pack a theme assertion wants: one pack in the sidebar, one
/// mod in the list, and most of the window empty. That is honest and it sells nothing.
///
/// So this builds a library worth photographing — several packs, a full mod set with the
/// dependencies underneath the mods that pulled them in — and reaches the real ModDB for
/// names and icons, because rows reading <c>carryon</c> against a blank square look like a
/// mock-up of the app rather than the app.
///
/// A tool rather than a test, and skipped unless <c>CAIRN_SHOT_DIR</c> says where to put
/// the output: it needs the network, and nothing here asserts anything a regression would
/// trip. Run it deliberately:
///
///   CAIRN_SHOT_DIR=$PWD/artifacts/screenshots \
///     dotnet tests/Cairn.App.Tests/bin/Debug/net10.0/Cairn.App.Tests.dll -method '*listing*'
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class ListingShots : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-shots-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previousHome = Environment.GetEnvironmentVariable("CAIRN_HOME");

    /// <summary>
    /// A real pack, so the shot shows what a pack actually looks like after a while: more
    /// mods than fit, in no particular order, with names nobody made up.
    /// </summary>
    private static readonly string[] Homestead =
    [
        "statushudcont", "olla", "carryon", "aculinaryartillery", "efmealsmodule",
        "efchefstricks", "expandedfoods", "betterruins", "bettertraders", "bettercrates",
        "chiseltools", "foodshelves", "purposefulstorage", "fromgoldencombs", "animalcages",
        "bedspawnv2", "vsroofing", "tankardsandgoblets", "shelfobsessed", "farseer",
        "packrat", "knapster", "terraprety", "watersheds", "wool", "tailorsdelight",
        "stepupadvanced", "keylock", "medievalarchitecture", "seafarer",
    ];

    public ListingShots()
    {
        // A sidebar with one row in it says the app holds one pack. Several say what it is
        // for, and the versions differing is the whole point of keeping them apart.
        Pack("homestead", "Homestead", "1.22.6", Homestead);
        Pack("vanilla-qol", "Vanilla + QoL", "1.22.6",
            ["olla", "unchisel", "keylock", "packrat", "glassview"]);
        Pack("building", "Building & Decor", "1.22.6",
            ["chiseltools", "medievalarchitecture", "vsroofing", "purposefulstorage"]);
        Pack("with-friends", "Server Night", "1.22.6",
            ["carryon", "bettercrates", "farseer"], connect: "play.example.com:42420");
        Pack("hardcore", "Hardcore 1.21", "1.21.7", ["primitivesurvival", "betterruins"]);

        Games.FakeInstall("1.22.6", Path.Combine(_home, "games", "1.22.6"), bytes: 614 * 1024 * 1024);
        Games.FakeInstall("1.21.7", Path.Combine(_home, "games", "1.21.7"), bytes: 598 * 1024 * 1024);

        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previousHome);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void Pack(string id, string name, string game, string[] mods, string? connect = null)
    {
        var dir = Path.Combine(_home, "packs", id);
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));

        File.WriteAllText(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(new
        {
            id, name, gameVersion = game, connect,
            mods = mods.Select(m => new { modid = m }).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        // Installed, so the pack reads as one somebody has actually played rather than one
        // just created — the sizes drive the delete and cleanup prompts too.
        foreach (var mod in mods)
            File.WriteAllBytes(Path.Combine(dir, "Mods", $"{mod}_1.0.0.zip"), new byte[420_000]);

        var saves = Path.Combine(dir, "data", "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "Homestead.vcdbs"), new byte[180_000_000]);
    }

    [AvaloniaFact]
    public void Capture_the_listing_screenshots()
    {
        if (Environment.GetEnvironmentVariable("CAIRN_SHOT_DIR") is not { Length: > 0 } outDir)
        {
            Assert.Skip("Set CAIRN_SHOT_DIR to capture listing screenshots.");
            return;
        }

        Directory.CreateDirectory(outDir);

        // The real handler, deliberately: this is where the mod names and icons come from,
        // and offline the rows fall back to bare ids rather than failing.
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        window.Show();

        void Shot(string name)
        {
            // Twice, as ThemeTests does: bindings settle on the next layout pass, so one
            // pass photographs a half-updated window.
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            using var file = File.Create(Path.Combine(outDir, name + ".png"));
            frame!.Save(file, new PngBitmapEncoderOptions());
        }

        // Names and icons arrive per row from ModDB, so the window is worth photographing
        // only once they have. Pumped rather than awaited: the dispatcher is this thread.
        void Settle(int seconds)
        {
            for (var i = 0; i < seconds * 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
        }

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "homestead");
        Settle(6);
        Shot("01-a-pack-and-its-mods");

        vm.Detail!.SearchText = "storage";
        vm.Detail.SearchCommand.Execute(null);
        Settle(4);
        Shot("02-adding-a-mod-from-moddb");

        vm.Detail.ClearSearchCommand.Execute(null);
        Settle(1);

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "with-friends");
        Settle(3);
        Shot("03-a-pack-that-joins-a-server");

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "hardcore");
        Settle(3);
        Shot("04-a-pack-on-an-older-game-version");

        // Preferences is its own window, and the view model comes from the command rather
        // than being built here — the app assembles it with the stores it already has.
        PreferencesViewModel? prefs = null;
        vm.OpenPreferences = p => { prefs = p; return Task.CompletedTask; };
        vm.ShowPreferencesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var prefsWindow = new PreferencesWindow { DataContext = prefs };
        prefsWindow.Show();
        Settle(2);

        using (var frame = prefsWindow.CaptureRenderedFrame())
        {
            using var file = File.Create(Path.Combine(outDir, "05-what-is-on-disk.png"));
            frame!.Save(file, new PngBitmapEncoderOptions());
        }

        prefsWindow.Close();
    }
}
