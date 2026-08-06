using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Packs;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Working out which downloads nothing points at. Every version here is re-downloadable,
/// which is what makes sweeping them safe — but deleting the one a pack was about to
/// launch is still a bad afternoon, so the rules are worth pinning.
/// </summary>
public class GameCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-cleanup-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly GameStore _games;
    private readonly RuntimeStore _runtimes;
    private readonly PackStore _packs;

    public GameCleanupTests()
    {
        _games = new GameStore(Path.Combine(_root, "games"));
        _runtimes = new RuntimeStore(Path.Combine(_root, "runtimes"));
        _packs = new PackStore(Path.Combine(_root, "packs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>An install real enough for GameInstall.TryAt, named for its version.</summary>
    private string InstallGame(string version, int bytes = 4096)
    {
        var dir = _games.InstallDir(version);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory"), new byte[bytes]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        return dir;
    }

    private void MakePack(string id, string gameVersion) => _packs.Create(id, gameVersion);

    private CleanupPlan Plan() => GameCleanup.Plan(_games, _runtimes, _packs);

    [Fact]
    public void With_no_packs_every_installed_version_is_unused()
    {
        InstallGame("1.21.7");
        InstallGame("1.22.5");

        var plan = Plan();

        Assert.Equal(2, plan.Versions.Count);
        Assert.True(plan.AnythingToDo);
        Assert.True(plan.TotalBytes > 0);
    }

    [Fact]
    public void A_version_a_pack_targets_is_kept()
    {
        InstallGame("1.21.7");
        InstallGame("1.22.5");
        MakePack("anego", "1.22.5");

        var plan = Plan();

        Assert.Equal("1.21.7", Assert.Single(plan.Versions).Label);
        Assert.Contains("1.22.5", plan.Kept);
    }

    [Fact]
    public void Every_version_being_in_use_is_nothing_to_do_rather_than_an_error()
    {
        InstallGame("1.22.5");
        MakePack("anego", "1.22.5");

        var plan = Plan();

        Assert.False(plan.AnythingToDo);
        Assert.False(plan.IsBlocked);
        Assert.Empty(plan.Describe());
    }

    [Fact]
    public void A_pack_whose_manifest_cannot_be_read_blocks_the_whole_sweep()
    {
        // It might need any version at all. Treating it as needing nothing could delete
        // the one thing it was about to launch.
        InstallGame("1.21.7");
        MakePack("broken", "1.22.5");
        File.WriteAllText(_packs.ManifestPath("broken"), "{ not json");

        var plan = Plan();

        Assert.True(plan.IsBlocked);
        Assert.False(plan.AnythingToDo);
        Assert.Contains("broken", plan.Blocked);
    }

    [Fact]
    public void The_machines_own_install_is_never_swept()
    {
        // It lives outside the store, and GameStore.ListInstalled is all this consults.
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(outside, "VintagestoryAPI.dll"), "");

        var plan = Plan();

        Assert.Empty(plan.Versions);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void What_is_described_is_named_and_sized()
    {
        InstallGame("1.21.7", bytes: 3 * 1024 * 1024);

        var line = Assert.Single(Plan().Describe());

        Assert.Contains("Vintage Story 1.21.7", line);
        Assert.Contains("MB", line);
    }

    // ---- removal ----

    [Fact]
    public void Removing_a_planned_version_takes_its_directory()
    {
        var dir = InstallGame("1.21.7");
        InstallGame("1.22.5");
        MakePack("anego", "1.22.5");

        var target = Assert.Single(Plan().Versions);
        _games.Remove(GameInstall.TryAt(target.Directory)!);

        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(_games.InstallDir("1.22.5")));
    }

    [Fact]
    public void A_runtime_outside_the_store_is_refused()
    {
        // The same guard as GameStore: this deletes recursively.
        var outside = Path.Combine(_root, "not-ours");
        Directory.CreateDirectory(outside);

        Assert.Throws<InvalidOperationException>(() =>
            _runtimes.Remove(new DotnetRuntime(outside, ExecutableArch.X64, [new Version(10, 0, 0)])));

        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void The_store_root_itself_is_refused()
    {
        Directory.CreateDirectory(_runtimes.Root);

        Assert.Throws<InvalidOperationException>(() =>
            _runtimes.Remove(new DotnetRuntime(_runtimes.Root, ExecutableArch.X64, [new Version(10, 0, 0)])));

        Assert.True(Directory.Exists(_runtimes.Root));
    }

    // ---- a client built from source is not a download ----

    /// <summary>An install marked as a modified build, named the way Cairn names one.</summary>
    private string InstallVariant(string version, int bytes = 8192)
    {
        var dir = Path.Combine(_games.Root, version + "-optimum");
        Directory.CreateDirectory(dir);

        File.WriteAllBytes(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), new byte[bytes]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker),
            """{"label":"Optimum"}""");

        return dir;
    }

    [Fact]
    public void A_built_client_is_never_swept()
    {
        InstallGame("1.22.5");
        var variant = InstallVariant("1.22.5");

        // No pack targets it, which is exactly when the sweep would take it.
        var plan = Plan();

        // The sweep's licence is that nothing in it is irreplaceable — every version is a
        // re-download. A client built from source is twenty minutes of compiling, so on
        // the same rule it would vanish the moment the last pack using it was retargeted,
        // from a button offering to tidy up.
        Assert.DoesNotContain(plan.Versions, v => v.Directory == variant);
        Assert.Contains(plan.Versions, v => v.Label == "1.22.5");

        Assert.True(Directory.Exists(variant));
    }

    [Fact]
    public void A_runtime_the_built_client_needs_is_kept_with_it()
    {
        InstallVariant("1.22.5");

        // Nothing else survives the sweep, so a runtime is orphaned only if the variant
        // does not count — and it launches like anything else, so it does.
        var plan = Plan();

        Assert.Empty(plan.Versions);
        Assert.Contains(plan.Kept, k => k.Contains("Optimum"));
    }

    [Fact]
    public void The_kept_list_does_not_say_the_same_version_twice()
    {
        MakePack("anego", "1.22.5");
        InstallGame("1.22.5");
        InstallVariant("1.22.5");

        // A variant reports the version it was built from, so both installs answer to
        // "1.22.5" and a plain version list would show it twice.
        Assert.Equal(plan_kept().Distinct().Count(), plan_kept().Count);

        List<string> plan_kept() => [.. Plan().Kept];
    }

    // ---- build trees ----

    [Fact]
    public void A_build_tree_is_reported_but_never_swept()
    {
        var builds = Path.Combine(_root, "builds");
        var tree = Path.Combine(builds, "optimum");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "big.bin"), new byte[64 * 1024]);

        var trees = GameCleanup.BuildTreesUnder(builds);

        Assert.Single(trees);
        Assert.Equal("optimum", trees[0].Label);
        Assert.True(trees[0].Bytes >= 64 * 1024);

        // Reported so the disk it uses is visible; not swept, because it is the one thing
        // here that does not come back on its own.
        Assert.DoesNotContain(Plan().Versions, v => v.Directory == tree);
    }

    [Fact]
    public void No_builds_root_reports_nothing_rather_than_failing()
    {
        Assert.Empty(GameCleanup.BuildTreesUnder(Path.Combine(_root, "never-built")));
    }
}
