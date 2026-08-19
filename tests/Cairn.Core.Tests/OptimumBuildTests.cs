using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Games.Optimum;
using Cairn.Core.Runtime;
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

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Newest);

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

        OptimumProvisioner.WriteMarker(dir, OptimumSource.Newest);

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
            () => OptimumProvisioner.WriteMarker(dir, OptimumSource.Newest));

        Assert.Contains("stock game", e.Message);
    }

    // ---- what somebody is told before it starts ----

    private static OptimumBuildPlan PlanWith(
        long free, bool needsSdk = false, PrereqReport? prereqs = null) => new()
    {
        Prereqs = prereqs ?? new PrereqReport([]),
        NeedsSdk = needsSdk,
        AlreadyBuilt = false,
        Source = OptimumSource.Newest,
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
    public void The_packagers_own_archive_counts_toward_the_peak()
    {
        // It exists beside the folder it was made from, so it is part of the peak even
        // though Cairn deletes it moments later. Left out, a real Linux build overran the
        // estimate by most of a gigabyte and finished with 1.5 GB to spare.
        var plan = PlanWith(free: 100L << 30);

        Assert.Equal(
            OptimumBuildPlan.BuildTreeBytes + OptimumBuildPlan.InstalledBytes
            + OptimumBuildPlan.RedistributableBytes,
            plan.RequiredBytes);

        if (!OperatingSystem.IsWindows())
            Assert.True(OptimumBuildPlan.RedistributableBytes > 0,
                "Linux and macOS packagers both emit an archive next to the client");
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
        var source = OptimumSource.Newest;
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
            OptimumSource.Newest, "/tmp/out", platform: platform);

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
            OptimumSource.Newest, "/tmp/out", vanilla,
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
            OptimumSource.Newest, "/tmp/out", platform: OptimumProvisioner.BuildPlatform.MacOS,
            arm64: true);

        var (_, intel) = OptimumProvisioner.PackagerFor(
            OptimumSource.Newest, "/tmp/out", platform: OptimumProvisioner.BuildPlatform.MacOS,
            arm64: false);

        Assert.Equal("arm64", arm[arm.IndexOf("--arch") + 1]);
        Assert.Equal("x64", intel[intel.IndexOf("--arch") + 1]);
    }

    // ---- the builds Cairn knows ----

    [Fact]
    public void Every_known_build_is_pinned_to_a_commit_not_a_branch()
    {
        // A branch would make somebody else's push into a Cairn feature that stopped
        // working, with no release of Cairn involved and nothing here to bisect.
        Assert.All(OptimumSource.Known, s => Assert.Matches("^[0-9a-f]{40}$", s.Ref));
    }

    [Fact]
    public void Every_known_build_names_a_game_version_a_pack_could_target()
    {
        // The same gate a manifest goes through. A build for ">=1.22" would be offered to
        // no pack at all, since no pack is allowed to say that.
        Assert.All(OptimumSource.Known, s => Assert.True(GameVersions.IsPlausibleVersion(s.GameVersion)));
    }

    /// <summary>
    /// Two entries for one game version make <see cref="OptimumSource.ForGame"/> a coin
    /// toss — and the loser is silent, since both produce an install by the same name.
    /// </summary>
    [Fact]
    public void No_two_builds_claim_the_same_game_version()
    {
        var versions = OptimumSource.Known.Select(s => s.GameVersion).ToList();

        Assert.Equal(versions.Count, versions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_pack_gets_the_build_for_its_own_version_or_none()
    {
        foreach (var known in OptimumSource.Known)
            Assert.Same(known, OptimumSource.ForGame(known.GameVersion));

        // The common case by far: most versions have no Optimum, and saying so is what
        // keeps the offer off packs that cannot use it.
        Assert.Null(OptimumSource.ForGame("1.19.8"));
    }

    /// <summary>
    /// Newest by comparison, not by position. Declaring the list out of order is an easy
    /// mistake and would otherwise make a bare <c>cairn-cli optimum build</c> quietly
    /// produce an old client.
    /// </summary>
    [Fact]
    public void The_newest_build_is_the_one_for_the_newest_game_version()
    {
        Assert.All(OptimumSource.Known, s => Assert.True(
            GameVersionComparer.Ascending.Compare(OptimumSource.Newest.GameVersion, s.GameVersion) >= 0));
    }

    [Fact]
    public void The_install_is_named_for_the_game_version_it_is_for()
    {
        Assert.Equal("1.22.5-optimum",
            (OptimumSource.Newest with { GameVersion = "1.22.5" }).InstallName);

        // Which is what lets two of them sit in the library at once.
        Assert.Equal(OptimumSource.Known.Count,
            OptimumSource.Known.Select(s => s.InstallName).Distinct().Count());

        Assert.True(OptimumSource.Newest.Supports(OptimumSource.Newest.GameVersion));
        Assert.False(OptimumSource.Newest.Supports("1.21.0"));
    }

    // ---- the environment the build runs in ----

    /// <summary>
    /// A build that failed after twenty minutes of downloading, printing nothing: bare
    /// DOTNET_ROOT applies to an apphost of any architecture, and bootstrap runs one that
    /// need not match the SDK — ilspycmd, installed globally by whatever .NET happened to
    /// install it. Sent to a root of the wrong architecture it cannot start, and bootstrap
    /// reads its version with stderr discarded, so the failure has no words in it.
    /// </summary>
    [Theory]
    [InlineData(ExecutableArch.X64, "DOTNET_ROOT_X64")]
    [InlineData(ExecutableArch.Arm64, "DOTNET_ROOT_ARM64")]
    [InlineData(ExecutableArch.X86, "DOTNET_ROOT_X86")]
    public void The_sdk_is_named_for_its_own_architecture_only(
        ExecutableArch arch, string variable)
    {
        var sdk = new DotnetSdk("/sdks/ten", [new Version(10, 0, 100)]);
        var env = OptimumProvisioner.BuildEnv(sdk, arch);

        Assert.Equal("/sdks/ten", env[variable]);
        Assert.False(env.ContainsKey("DOTNET_ROOT"));

        // Still first on PATH, which is how bootstrap's own `dotnet` calls find it.
        Assert.StartsWith("/sdks/ten" + Path.PathSeparator, env["PATH"]);
    }

    [Fact]
    public void An_sdk_of_unreadable_architecture_names_no_root_at_all()
    {
        // hostfxr falls back to the machine's own install, which is a better answer than a
        // guess — the same reason the launcher sets neither variable when it has no match.
        var env = OptimumProvisioner.BuildEnv(
            new DotnetSdk("/sdks/ten", [new Version(10, 0, 100)]), ExecutableArch.Unknown);

        Assert.DoesNotContain(env.Keys, k => k.StartsWith("DOTNET_ROOT"));
    }

    // ---- reusing the working tree ----

    private OptimumProvisioner Provisioner() => new(
        new HttpClient(), new GameStore(Dir("games")),
        new Cairn.Core.Runtime.RuntimeStore(Dir("runtimes")), Dir("builds"));

    /// <summary>
    /// The tree is kept between builds because reusing it is what makes a rebuild minutes
    /// rather than a fresh decompile — and it holds sources cloned at the refs of whichever
    /// revision was built into it. Handing those to another revision produces a client made
    /// of two, which nothing downstream could detect.
    /// </summary>
    [Fact]
    public void A_tree_built_for_another_revision_is_refreshed()
    {
        var provisioner = Provisioner();
        var source = OptimumSource.Newest;

        // Nothing has been built here, and a tree that cannot say what it holds is assumed
        // to hold the wrong thing.
        Assert.True(provisioner.TreeIsStaleFor(source));

        provisioner.RecordBootstrap(source);
        Assert.False(provisioner.TreeIsStaleFor(source));

        // Same game version, different commit: the case a check on the version alone
        // cannot see, and the one a re-pin produces.
        Assert.True(provisioner.TreeIsStaleFor(source with { Ref = new string('a', 40) }));

        foreach (var other in OptimumSource.Known.Where(s => s != source))
            Assert.True(provisioner.TreeIsStaleFor(other));
    }

    [Fact]
    public void Cleaning_the_tree_takes_the_note_about_it_too()
    {
        var provisioner = Provisioner();

        provisioner.RecordBootstrap(OptimumSource.Newest);
        provisioner.Clean();

        // Otherwise the note outlives the tree and the next build reuses intermediates
        // that are not there, which is only saved by them not being there.
        Assert.True(provisioner.TreeIsStaleFor(OptimumSource.Newest));
    }

    [Theory]
    [InlineData(true, "-Version", "-Refresh")]
    [InlineData(false, "--version", "--refresh")]
    public void Bootstrap_is_told_the_game_version_and_whether_to_start_over(
        bool windows, string versionFlag, string refreshFlag)
    {
        var source = OptimumSource.Newest;

        var (script, kept) = OptimumProvisioner.BootstrapFor(source, windows, refresh: false);

        Assert.Equal(windows ? "bootstrap.ps1" : "bootstrap.sh", script);
        Assert.Equal(source.GameVersion, kept[kept.IndexOf(versionFlag) + 1]);
        Assert.DoesNotContain(refreshFlag, kept);

        var (_, refreshed) = OptimumProvisioner.BootstrapFor(source, windows, refresh: true);

        Assert.Contains(refreshFlag, refreshed);
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

        var source = OptimumSource.Newest;

        Assert.False(provisioner.Plan(source).AlreadyBuilt);

        // Named by the store, not by hand: on macOS an install directory is a bundle, and a
        // path built here without the suffix is one nothing looks in.
        var install = Dir("games", GameStore.DirectoryNameFor(source.InstallName));
        Touch(install, "VintagestoryAPI.dll");
        Touch(install, OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum");
        OptimumProvisioner.WriteMarker(install, source);

        Assert.True(provisioner.Plan(source).AlreadyBuilt);

        // One build being installed says nothing about another. They are separate
        // directories precisely so a machine can hold both.
        var other = OptimumSource.Known.FirstOrDefault(s => s != source);
        if (other is not null) Assert.False(provisioner.Plan(other).AlreadyBuilt);
    }
}
