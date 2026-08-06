using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Games.Optimum;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The parts of building a client that can be checked without building one.
///
/// The build itself takes twenty minutes and needs a toolchain, so what is held here is
/// everything around it: that somebody is told the cost before it starts, that the finished
/// directory is recognised whatever the platform's packager called it, and — the one that
/// already bit once — that the install is marked with the launcher to run rather than
/// letting the vanilla executable sitting next to it be picked up.
/// </summary>
public class OptimumBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-build-" + Guid.NewGuid().ToString("n")[..8]);

    public OptimumBuildTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string dir, string name) =>
        File.WriteAllText(Path.Combine(dir, name), "");

    // ---- finding what the packager produced ----

    [Fact]
    public void A_client_directory_is_found_by_what_it_holds()
    {
        var client = Dir("out", "Optimum-v0.3.5-win-x64");
        Touch(client, "VintagestoryAPI.dll");

        // By content, not by name: the three packagers name their output three different
        // ways and one of them is a .app bundle.
        Assert.Equal(client, OptimumProvisioner.FindPackagedClient(Path.Combine(_dir, "out")));
    }

    [Fact]
    public void A_mac_app_bundle_is_a_client_directory_too()
    {
        var app = Dir("out", "Optimum.app");
        Touch(app, "VintagestoryAPI.dll");

        Assert.Equal(app, OptimumProvisioner.FindPackagedClient(Path.Combine(_dir, "out")));
    }

    [Fact]
    public void Staging_copies_are_not_mistaken_for_the_output()
    {
        var output = Dir("out");

        // The packagers leave a second whole copy of the client in staging directories.
        // Picking one up would install something that works and is not what was built.
        var staging = Dir("out", "_dmg-arm64", "Optimum.app");
        Touch(staging, "VintagestoryAPI.dll");

        var real = Dir("out", "Optimum-v0.3.5");
        Touch(real, "VintagestoryAPI.dll");

        Assert.Equal(real, OptimumProvisioner.FindPackagedClient(output));
    }

    [Fact]
    public void An_output_with_no_client_in_it_is_null_rather_than_a_guess()
    {
        var output = Dir("out");
        Touch(output, "Optimum-v0.3.5-win-x64.zip");

        Assert.Null(OptimumProvisioner.FindPackagedClient(output));
        Assert.Null(OptimumProvisioner.FindPackagedClient(Path.Combine(_dir, "never-made")));
    }

    // ---- marking the install ----

    [Fact]
    public void The_marker_names_the_launcher_to_run()
    {
        var dir = Dir("install");
        Touch(dir, "VintagestoryAPI.dll");
        Touch(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory");
        Touch(dir, OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum");

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Pinned);

        var install = GameInstall.TryAt(dir);

        Assert.NotNull(install);
        Assert.Equal("Optimum", install.Variant);

        // The whole point. Optimum's output is vanilla files plus its own launcher, and the
        // vanilla executable is right there in the same directory — an install marked
        // without an executable runs the stock game while Cairn says it is running Optimum.
        Assert.Equal(OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum",
            Path.GetFileName(install.Executable));
    }

    [Fact]
    public void A_bundle_whose_only_executable_is_the_launcher_still_marks()
    {
        // The real macOS shape, which is not the Windows one: the packager renames
        // Vintagestory to Optimum, so the stock executable is not there at all. An install
        // that insisted on finding one would reject a perfectly good bundle.
        var dir = Dir("Optimum.app");
        Touch(dir, "VintagestoryAPI.dll");
        Touch(dir, "Optimum");

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Pinned);

        var install = GameInstall.TryAt(dir);

        Assert.NotNull(install);
        Assert.Equal("Optimum", Path.GetFileName(install.Executable));
    }

    [Fact]
    public void A_package_with_no_launcher_is_refused_rather_than_marked()
    {
        var dir = Dir("no-launcher");
        Touch(dir, "VintagestoryAPI.dll");
        Touch(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory");

        // If their packaging ever stops producing the launcher, the failure has to be here.
        // Marking it anyway would install a stock client under Optimum's name.
        var e = Assert.Throws<OptimumBuildException>(
            () => OptimumProvisioner.WriteMarker(dir, OptimumSource.Pinned));

        Assert.Contains("stock game", e.Message);
    }

    // ---- what somebody is told before it starts ----

    private static OptimumBuildPlan PlanWith(
        long free, bool needsSdk = false, PrereqReport? prereqs = null) => new()
    {
        Prereqs = prereqs ?? new PrereqReport([]),
        NeedsSdk = needsSdk,
        AlreadyBuilt = false,
        Source = OptimumSource.Pinned,
        FreeBytes = free,
    };

    [Fact]
    public void The_warning_says_it_is_a_compile_and_what_it_costs()
    {
        var plan = PlanWith(free: 100L * 1024 * 1024 * 1024);
        var text = plan.Describe();

        Assert.True(plan.CanStart);
        Assert.Contains("compile", text);
        Assert.Contains("minutes", text);
        Assert.Contains("GB", text);

        // That it can be abandoned is the thing that makes starting it a small decision.
        Assert.Contains("cancelled", text);
    }

    [Fact]
    public void A_needed_sdk_is_mentioned_only_when_it_is_needed()
    {
        Assert.Contains("SDK", PlanWith(free: 100L << 30, needsSdk: true).Describe());
        Assert.DoesNotContain("SDK", PlanWith(free: 100L << 30).Describe());
    }

    [Fact]
    public void A_full_disk_stops_it_before_it_starts()
    {
        // Not merely a failure: it fails twenty minutes in, having taken the machine's free
        // space with it, and a decompiler out of room reports something else entirely.
        var plan = PlanWith(free: 2L * 1024 * 1024 * 1024);

        Assert.False(plan.EnoughSpace);
        Assert.False(plan.CanStart);
        Assert.Contains("not enough free space", plan.Describe());
    }

    [Fact]
    public void An_unreadable_volume_does_not_block_the_build()
    {
        // Free space is a courtesy check. Refusing to build because a volume would not
        // answer would be worse than letting it try.
        Assert.True(PlanWith(free: -1).EnoughSpace);
    }

    [Fact]
    public void Missing_tools_replace_the_cost_warning_entirely()
    {
        var missing = new PrereqReport([new BuildTool("git", "applying patches", "install git")]);
        var plan = PlanWith(free: 100L << 30, prereqs: missing);

        Assert.False(plan.CanStart);

        // Nothing about disk or minutes: none of it applies until the tool is there, and
        // burying the one actionable sentence under a cost estimate helps nobody.
        Assert.Contains("git", plan.Describe());
        Assert.DoesNotContain("minutes", plan.Describe());
    }

    // ---- driving the packagers ----

    /// <summary>
    /// Every packager takes a version and means the Vintage Story version by it — that is
    /// what builds the client download URL, and each defaults it from forks.json. Optimum's
    /// own version comes from its VERSION file and is never passed in.
    ///
    /// Tested per platform from one host because that is the only way to check the other
    /// two: passing Optimum's version asked the CDN for a client release numbered 0.3.5 and
    /// got a 404, twenty minutes into a build that had otherwise succeeded, and the same
    /// mistake was sitting in all three branches at once.
    /// </summary>
    [Theory]
    [InlineData(OptimumProvisioner.BuildPlatform.Windows, "-Version")]
    [InlineData(OptimumProvisioner.BuildPlatform.MacOS, "--version")]
    [InlineData(OptimumProvisioner.BuildPlatform.Linux, "--version")]
    public void The_packager_is_told_the_game_version_not_optimums(
        OptimumProvisioner.BuildPlatform platform, string flag)
    {
        var source = OptimumSource.Pinned;
        var (_, args) = OptimumProvisioner.PackagerFor(source, "/tmp/out", platform: platform);

        var value = args[args.IndexOf(flag) + 1];

        Assert.Equal(source.GameVersion, value);
        Assert.NotEqual(source.Version, value);
    }

    [Theory]
    [InlineData(OptimumProvisioner.BuildPlatform.Windows, "package.ps1")]
    [InlineData(OptimumProvisioner.BuildPlatform.MacOS, "package-macos.sh")]
    [InlineData(OptimumProvisioner.BuildPlatform.Linux, "package-linux.sh")]
    public void Each_platform_runs_its_own_packager(
        OptimumProvisioner.BuildPlatform platform, string script)
    {
        var (name, args) = OptimumProvisioner.PackagerFor(
            OptimumSource.Pinned, "/tmp/out", platform: platform);

        Assert.Equal(script, name);
        Assert.Contains("/tmp/out", args);
    }

    [Fact]
    public void Windows_is_pointed_at_the_client_already_on_disk()
    {
        var vanilla = new GameInstall
        {
            Directory = "/games/1.22.5", Executable = "/games/1.22.5/Vintagestory",
            Version = "1.22.5", Architecture = Cairn.Core.Runtime.ExecutableArch.X64,
            RequiredFramework = new Version(10, 0, 0),
        };

        var (_, args) = OptimumProvisioner.PackagerFor(
            OptimumSource.Pinned, "/tmp/out", vanilla,
            OptimumProvisioner.BuildPlatform.Windows);

        // Only Windows' packager takes one; the others fetch their own client, so passing
        // it there would be an unrecognised argument rather than a saving.
        Assert.Contains("-VanillaDir", args);
        Assert.Contains("/games/1.22.5", args);
    }

    [Fact]
    public void The_mac_packager_is_told_which_architecture_to_build()
    {
        var (_, arm) = OptimumProvisioner.PackagerFor(
            OptimumSource.Pinned, "/tmp/out", platform: OptimumProvisioner.BuildPlatform.MacOS,
            arm64: true);

        var (_, intel) = OptimumProvisioner.PackagerFor(
            OptimumSource.Pinned, "/tmp/out", platform: OptimumProvisioner.BuildPlatform.MacOS,
            arm64: false);

        Assert.Equal("arm64", arm[arm.IndexOf("--arch") + 1]);
        Assert.Equal("x64", intel[intel.IndexOf("--arch") + 1]);
    }

    // ---- the pin ----

    [Fact]
    public void The_pinned_source_is_a_commit_not_a_branch()
    {
        // A branch would make somebody else's push into a Cairn feature that stopped
        // working, with no release of Cairn involved and nothing here to bisect.
        Assert.Matches("^[0-9a-f]{40}$", OptimumSource.Pinned.Ref);
    }

    [Fact]
    public void The_install_is_named_for_the_game_version_it_is_for()
    {
        Assert.Equal("1.22.5-optimum", OptimumSource.Pinned with { GameVersion = "1.22.5" } is var s
            ? s.InstallName : "");

        Assert.True(OptimumSource.Pinned.Supports(OptimumSource.Pinned.GameVersion));
        Assert.False(OptimumSource.Pinned.Supports("1.21.0"));
    }

    [Fact]
    public void A_checkout_declaring_another_game_version_is_readable()
    {
        var repo = Dir("repo");
        File.WriteAllText(Path.Combine(repo, "forks.json"), """{"vintageStoryVersion":"1.22.6"}""");
        File.WriteAllText(Path.Combine(repo, "VERSION"), "0.4.0\n");

        // Read rather than trusted: the pin records what somebody tested, this records what
        // the commit says, and a bumped pin with a stale constant is caught by comparing.
        Assert.Equal("1.22.6", OptimumSource.ReadGameVersion(repo));
        Assert.Equal("0.4.0", OptimumSource.ReadVersion(repo));
    }

    [Fact]
    public void A_checkout_that_says_nothing_reads_as_null_rather_than_throwing()
    {
        var repo = Dir("empty-repo");

        Assert.Null(OptimumSource.ReadGameVersion(repo));
        Assert.Null(OptimumSource.ReadVersion(repo));

        File.WriteAllText(Path.Combine(repo, "forks.json"), "not json at all");
        Assert.Null(OptimumSource.ReadGameVersion(repo));
    }

    // ---- planning against a real store ----

    [Fact]
    public void A_build_already_installed_is_reported_as_such()
    {
        var games = new GameStore(Dir("games"));
        var provisioner = new OptimumProvisioner(
            new HttpClient(), games, new Cairn.Core.Runtime.RuntimeStore(Dir("runtimes")),
            Dir("builds"));

        Assert.False(provisioner.Plan("1.22.5").AlreadyBuilt);

        var install = Dir("games", OptimumSource.Pinned.InstallName);
        Touch(install, "VintagestoryAPI.dll");
        Touch(install, OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum");
        OptimumProvisioner.WriteMarker(install, OptimumSource.Pinned);

        Assert.True(provisioner.Plan("1.22.5").AlreadyBuilt);
    }
}
