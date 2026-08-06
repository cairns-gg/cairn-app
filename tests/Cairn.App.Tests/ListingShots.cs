using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cairn.App.ViewModels;
using Cairn.App.Views;
using Cairn.Core;
using Cairn.Core.Games.Optimum;
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
    /// <summary>
    /// Short and anonymous on purpose. The Settings tab prints the pack's mods and data
    /// paths, and a macOS temp directory is sixty characters of machine noise across the
    /// bottom of a picture meant for a store page.
    /// </summary>
    private readonly string _home =
        Path.Combine("/tmp", "cairn-demo-" + Guid.NewGuid().ToString("n")[..4]);

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

        // On the version Optimum is built for, so the optimised-client panel is offered.
        // It targets exactly one Vintage Story version at a time and is absent everywhere
        // else, so without this pack there is nothing to photograph.
        Pack("performance", "Big Base", OptimumSource.Pinned.GameVersion,
            ["carryon", "bettercrates", "chiseltools", "farseer", "terraprety"]);

        Games.FakeInstall("1.22.6", Path.Combine(_home, "games", "1.22.6"), bytes: 614 * 1024 * 1024);
        Games.FakeInstall("1.21.7", Path.Combine(_home, "games", "1.21.7"), bytes: 598 * 1024 * 1024);
        Games.FakeInstall(OptimumSource.Pinned.GameVersion,
            Path.Combine(_home, "games", OptimumSource.Pinned.GameVersion), bytes: 610 * 1024 * 1024);

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

        // ---- the optimised client ----
        //
        // Four frames because it is a sequence, not a feature: what is offered, what it
        // costs, what it looks like while it happens, and what the pack says afterwards.
        // The middle two are windows of their own, built here the way the launcher builds
        // them.

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "performance");
        Settle(4);
        ShowSettings(window);
        Shot("06-build-an-optimised-client");

        var provisioner = new OptimumProvisioner(
            new HttpClient(new OfflineHandler()),
            new Cairn.Core.Games.GameStore(Path.Combine(_home, "games")),
            new Cairn.Core.Runtime.RuntimeStore(Path.Combine(_home, "runtimes")),
            Path.Combine(_home, "builds"));

        var plan = provisioner.Plan(OptimumSource.Pinned.GameVersion);

        var confirm = new ConfirmWindow
        {
            DataContext = new ConfirmViewModel("Build Optimum?", plan.Describe(), "Build it"),
        };
        confirm.Show();
        Settle(1);
        ShotOf(confirm, outDir, "07-what-it-will-cost");
        confirm.Close();

        // A build to look at rather than one to run; see the preview constructor. A real
        // one takes twenty minutes and cannot be photographed from here.
        var build = new OptimumBuildWindow
        {
            DataContext = new OptimumBuildViewModel(
                OptimumSource.Pinned,
                phase: "bootstrap",
                detail: "decompiling the game and applying Optimum's patches — this is the long part",
                fraction: 0,
                log: BuildLogSample),
        };
        build.Show();
        Settle(1);
        ShotOf(build, outDir, "08-watching-it-build");
        build.Close();

        // Afterwards: the pack says what it runs, and offers the way back.
        vm.Detail!.ChooseInstall(MarkBuiltClient());
        Dispatcher.UIThread.RunJobs();
        ShowSettings(window);
        Settle(1);
        Shot("09-running-with-optimum");
    }

    /// <summary>Selects the Settings tab, which a TabControl does not realise until then.</summary>
    private static void ShowSettings(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Settings");
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ShotOf(Window window, string outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        using var file = File.Create(Path.Combine(outDir, name + ".png"));
        frame!.Save(file, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// A built client, so the last frame shows the state a pack is in afterwards.
    ///
    /// Named the way Cairn names one, because the version is read back off the directory
    /// when an install's metadata cannot be parsed — and a directory of empty files has no
    /// metadata to parse.
    /// </summary>
    private GameInstall MarkBuiltClient()
    {
        var dir = Path.Combine(_home, "games", OptimumSource.Pinned.InstallName);

        Games.FakeInstall(OptimumSource.Pinned.GameVersion, dir, bytes: 640 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Optimum.exe" : "Optimum"), new byte[128 * 1024]);

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Pinned);

        return GameInstall.TryAt(dir)!;
    }

    /// <summary>Real lines from a real build, so the log reads as one.</summary>
    private static readonly string[] BuildLogSample =
    [
        "$ git clone --quiet https://github.com/dizzyd/Optimum.git",
        "$ bootstrap.sh --version 1.22.5",
        "Downloading https://cdn.vintagestory.at/gamefiles/stable/vs_client_osx-arm64_1.22.5.tar.gz",
        "Extracting client archive",
        "Decompiling VintagestoryLib.dll with ilspycmd",
        "Decompiling Vintagestory.dll with ilspycmd",
        "Cloning anegostudios/vsapi at 324ccf9e",
        "Cloning anegostudios/vssurvivalmod",
        "Applying post-decompile fixups",
        "Applying patches/VintagestoryLib/Client/ClientMain.cs.patch",
        "Applying patches/VintagestoryLib/Client/SystemRenderOITLayers.cs.patch",
        "Applying patches/VintagestoryApi/Common/EntityHeadController.cs.patch",
        "Applying patches/VSSurvivalMod/Systems/Cooking/CookingRecipe.cs.patch",
        "Applying patches/VSSurvivalMod/Item/ItemShears.cs.patch",
        "Patches: 95 applied, 0 skipped, 0 failed (filter: all)",
        "Synced sources/ into working tree.",
        "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release",
    ];
}
