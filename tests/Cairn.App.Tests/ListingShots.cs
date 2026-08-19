using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cairn.App;
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

        // Published, which the site's two shots need and no listing shot wants: the address
        // under the pack's name is the whole of what somebody shares, and cairns.gg says so
        // in as many words. This pack and not Homestead because nothing in 01-11 opens it,
        // so giving it an address changes none of them.
        PublishedLink().Save(Path.Combine(_home, "packs", "vanilla-qol", "cairns.json"));
        Pack("building", "Building & Decor", "1.22.6",
            ["chiseltools", "medievalarchitecture", "vsroofing", "purposefulstorage"]);
        Pack("with-friends", "Server Night", "1.22.6",
            ["carryon", "bettercrates", "farseer"], connect: "play.example.com:42420");
        Pack("hardcore", "Hardcore 1.21", "1.21.7", ["primitivesurvival", "betterruins"]);

        // On the version Optimum is built for, so the optimised-client panel is offered.
        // It targets exactly one Vintage Story version at a time and is absent everywhere
        // else, so without this pack there is nothing to photograph.
        Pack("performance", "Big Base", OptimumSource.Newest.GameVersion,
            ["carryon", "bettercrates", "chiseltools", "farseer", "terraprety"]);

        Games.FakeInstall("1.22.6", Games.DirIn(Path.Combine(_home, "games"), "1.22.6"), bytes: 614 * 1024 * 1024);
        Games.FakeInstall("1.21.7", Games.DirIn(Path.Combine(_home, "games"), "1.21.7"), bytes: 598 * 1024 * 1024);
        Games.FakeInstall(OptimumSource.Newest.GameVersion,
            Games.DirIn(Path.Combine(_home, "games"), OptimumSource.Newest.GameVersion), bytes: 610 * 1024 * 1024);

        // A machine that has built a client, so Storage shows the row for it. It is the
        // largest thing Cairn writes and the one most worth seeing accounted for.
        var tree = Path.Combine(_home, "builds", "optimum");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "working-tree.bin"), new byte[3_300L > 0 ? 8 * 1024 * 1024 : 0]);

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
    public async Task Capture_the_listing_screenshots()
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

        // The same frame again, at twice the pixels, for cairns.gg.
        //
        // ModDB scales its images down to a thumbnail, so a listing shot can be taken at the
        // size the window really is. The site prints them at around 700 CSS pixels on
        // displays with two device pixels for each, where a 1x capture arrives visibly soft.
        //
        // UiScale grows a window's declared size along with its content and open windows
        // follow the setting live, so this is the same window with twice the pixels rather
        // than a bigger one showing more rows. Taken beside its 1x sibling rather than in a
        // block of its own, so the two cannot drift apart: whatever was arranged to make the
        // listing frame worth looking at is still arranged here.
        // unclamp: for a dialog whose height comes from its content under a MaxHeight cap.
        //
        // UiScale scales the content but not the cap, so at 2x the build warning drew twice
        // the text into the same 420 and cut the last line mid-word — a sentence severed at
        // "existing insta" reads as a broken screenshot rather than as a window with more in
        // it. Lifting the cap lets SizeToContent finish the job. Nothing is staged: the
        // window draws text it already contains, and the cap goes back afterwards.
        void SiteShot(Window shown, string name, int settle = 1, bool unclamp = false)
        {
            // Sizes read before scaling and put back after, because scaling up and down
            // again does not land a window on the size it started at.
            //
            // The main window is restored whether or not it is the one being photographed.
            // UiScale is global and every open window follows it, so shooting a *dialog* at
            // 2x silently resizes the main window behind it — which is how the listing
            // frames ended up 1000 wide instead of 1180, from a helper that never appeared
            // to touch them.
            var (width, height) = (shown.Width, shown.Height);
            var (mainWidth, mainHeight) = (window.Width, window.Height);
            var cap = shown.MaxHeight;

            if (unclamp) shown.MaxHeight = double.PositiveInfinity;

            UiScale.Current = 2.0;
            Settle(settle);
            ShotOf(shown, outDir, "site-" + name);

            UiScale.Current = 1.0;
            shown.MaxHeight = cap;
            shown.Width = width;
            shown.Height = height;
            window.Width = mainWidth;
            window.Height = mainHeight;
            Dispatcher.UIThread.RunJobs();
        }

        // ---- the two the site uses ----
        //
        // Kept apart from the listing shots because they answer a different question. A
        // store listing is selling the launcher to somebody who has not met it; cairns.gg
        // has already been reached by somebody holding a link, and what it has to explain
        // is that a pack has an address, and that publishing shows you what it would send
        // before it sends it. Which is why both need a pack that has been published, and
        // none of 01-11 has one.
        //
        // First, because every frame after this leaves the window somewhere: a search in
        // the status bar, the Settings tab selected, a line reading "pinned carryon" that
        // belongs to another pack. None of that is wrong in a listing shot, where it reads
        // as an app in use — but on a page introducing the thing it is just noise.
        vm.SelectedPack = vm.Packs.Single(p => p.Id == "vanilla-qol");
        Settle(6);
        SiteShot(window, "01-a-pack-open", settle: 3);

        // Searching ModDB from inside the launcher, on the published pack rather than on
        // Homestead: the site's other frames are all this pack, and a page whose pictures
        // wander between packs reads as pictures of different programs.
        vm.Detail!.SearchText = "storage";
        vm.Detail.SearchCommand.Execute(null);
        Settle(4);
        SiteShot(window, "03-adding-a-mod", settle: 3);
        vm.Detail.ClearSearchCommand.Execute(null);
        Settle(1);

        var share = SharingVanillaQol();
        SiteShot(share, "02-sharing-a-pack");
        share.Close();

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

        // The Storage tab, which is what this frame is called after. Without selecting it
        // the shot was of the Overview tab — a version string and a scale picker — under a
        // filename promising what is on disk.
        var prefTabs = prefsWindow.GetVisualDescendants().OfType<TabControl>().Single();
        prefTabs.SelectedItem = prefTabs.GetVisualDescendants().OfType<TabItem>()
            .Single(t => (t.Header as string) == "Storage");

        Settle(2);

        using (var frame = prefsWindow.CaptureRenderedFrame())
        {
            using var file = File.Create(Path.Combine(outDir, "05-what-is-on-disk.png"));
            frame!.Save(file, new PngBitmapEncoderOptions());
        }

        // No site shot of this one. The demo home has fake installs and no runtimes beside
        // them, so every row carries a red "no matching .NET runtime — cannot start". That
        // is fine at thumbnail size on ModDB and honest about what the screen does; on a
        // page introducing Cairn it is a wall of errors. The site says what this screen is
        // for in a sentence instead, until the harness can stage runtimes worth showing.
        prefsWindow.Close();

        // ---- the optimised client ----
        //
        // Four frames because it is a sequence, not a feature: what is offered, what it
        // costs, what it looks like while it happens, and what the pack says afterwards.
        // The middle two are windows of their own, built here the way the launcher builds
        // them.

        vm.SelectedPack = vm.Packs.Single(p => p.Id == "performance");
        Settle(4);

        // The version window, on a mod with a few releases behind it.
        var pinRow = vm.Detail!.Mods.First();
        // With tags and dates, because showing those is the whole reason this is a window
        // and not a 120px combo box.
        vm.Detail.CacheReleases(pinRow.ModId,
        [
            Sample.Release(pinRow.ModId, "2.1.0", ["1.22.5", "1.22.6"], DateTime.UtcNow.AddDays(-4)),
            Sample.Release(pinRow.ModId, "2.0.0", ["1.22.0", "1.22.4"], DateTime.UtcNow.AddDays(-40)),
            Sample.Release(pinRow.ModId, "1.9.3", ["1.21.0", "1.21.7"], DateTime.UtcNow.AddDays(-260)),
        ]);

        PinVersionViewModel? pinChoice = null;
        vm.Detail.ChoosePinnedVersion = c => { pinChoice = c; return Task.FromResult(false); };
        await pinRow.TogglePinCommand.ExecuteAsync(null);

        if (pinChoice is not null)
        {
            var pinWindow = new PinVersionWindow { DataContext = pinChoice };
            pinWindow.Show();
            Settle(1);
            ShotOf(pinWindow, outDir, "10-pinning-a-version");
            SiteShot(pinWindow, "04-pinning-a-version");
            pinWindow.Close();
        }


        // One pinned row among unpinned ones, so the two states are visible side by side.
        vm.Detail.CacheReleaseChoices("carryon", ["2.0.0"]);
        vm.Detail.ChoosePinnedVersion = c =>
        {
            c.Selected = c.Choices.First(x => !x.IsTrackNewest);
            return Task.FromResult(true);
        };
        if (vm.Detail.Mods.FirstOrDefault(m => m.ModId == "carryon") is { } toPin)
            await toPin.TogglePinCommand.ExecuteAsync(null);
        Settle(1);
        Shot("11-a-pinned-mod");

        ShowSettings(window);
        Shot("06-build-an-optimised-client");

        var provisioner = new OptimumProvisioner(
            new HttpClient(new OfflineHandler()),
            new Cairn.Core.Games.GameStore(Path.Combine(_home, "games")),
            new Cairn.Core.Runtime.RuntimeStore(Path.Combine(_home, "runtimes")),
            Path.Combine(_home, "builds"));

        var plan = provisioner.Plan(OptimumSource.Newest);

        var confirm = new ConfirmWindow
        {
            DataContext = new ConfirmViewModel("Build Optimum?", plan.Describe(), "Build it"),
        };
        confirm.Show();
        Settle(1);
        ShotOf(confirm, outDir, "07-what-it-will-cost");
        SiteShot(confirm, "05-what-it-will-cost", unclamp: true);
        confirm.Close();

        // A build to look at rather than one to run; see the preview constructor. A real
        // one takes twenty minutes and cannot be photographed from here.
        var build = new OptimumBuildWindow
        {
            DataContext = new OptimumBuildViewModel(
                OptimumSource.Newest,
                phase: "bootstrap",
                detail: "decompiling the game and applying Optimum's patches — this is the long part",
                fraction: 0,
                log: BuildLogSample),
        };
        build.Show();
        Settle(1);
        ShotOf(build, outDir, "08-watching-it-build");
        SiteShot(build, "06-watching-it-build");
        build.Close();

        // Afterwards: the pack says what it runs, and offers the way back.
        vm.Detail!.ChooseInstall(MarkBuiltClient());
        Dispatcher.UIThread.RunJobs();
        ShowSettings(window);
        Settle(1);
        Shot("09-running-with-optimum");
    }

    /// <summary>
    /// The Share window standing over a published pack, for the site.
    ///
    /// Built from a view model rather than driven out of the main window, the same way the
    /// build window above is: pressing Share for real would reach the network, and what the
    /// shot wants is the dialog as it is read — before anything is sent.
    /// </summary>
    private static ShareWindow SharingVanillaQol()
    {
        // The pack's own mods, so the dialog agrees with the window behind it. One pinned,
        // because the pin is the thing this list says that a count could not.
        var plan = new PublishPlan("vanilla-qol",
        [
            new PublishMod("glassview", "1.3.0", Pinned: false, OnModDb: true),
            new PublishMod("keylock", "1.1.1", Pinned: false, OnModDb: true),
            new PublishMod("olla", "1.1.0", Pinned: false, OnModDb: true),
            new PublishMod("packrat", "1.1.0", Pinned: false, OnModDb: true),
            new PublishMod("unchisel", "1.2.0", Pinned: true, OnModDb: true),
        ], Connect: null, LockCovers: true, LockProblem: null);

        var window = new ShareWindow
        {
            DataContext = ShareViewModel.From(plan, "Vanilla + QoL", "dizzyd", PublishedLink()),
        };
        window.Show();

        return window;
    }

    /// <summary>
    /// What a pack published once looks like on disk. Written to the link file rather than
    /// published for real, so the shot needs no server and no account.
    /// </summary>
    private static PackLink PublishedLink() => new()
    {
        Role = PackRole.Author,
        Url = "https://cairns.gg/dizzyd/vanilla-qol",
        Revision = 1,
        Published = new PublishRecord { Visibility = "public", Connect = "stripped" },
    };

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
        var dir = Games.DirIn(Path.Combine(_home, "games"), OptimumSource.Newest.InstallName);

        Games.FakeInstall(OptimumSource.Newest.GameVersion, dir, bytes: 640 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Optimum.exe" : "Optimum"), new byte[128 * 1024]);

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Newest);

        return GameInstall.TryAt(dir)!;
    }

    /// <summary>Builds a release the way ModDB describes one, for the shots.</summary>
    private static class Sample
    {
        public static Cairn.Core.ModDb.ResolvedRelease Release(
            string modId, string version, string[] gameVersions, DateTime created) =>
            new(modId, version, $"{modId}_{version}.zip", "https://example/x.zip", 1, 2,
                Cairn.Core.ModDb.MatchQuality.Exact, "client", null, gameVersions,
                created.ToString("yyyy-MM-dd HH:mm:ss"));
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
