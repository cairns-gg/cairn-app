using Cairn.Core;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

public class GameLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "Cairn-launch-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _fakeRuntimeRoot;

    public GameLauncherTests()
    {
        Directory.CreateDirectory(_dir);

        _fakeRuntimeRoot = Path.Combine(_dir, "private-dotnet");
        Directory.CreateDirectory(Path.Combine(_fakeRuntimeRoot, "shared", "Microsoft.NETCore.App", "10.0.10"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private GameInstall Install(ExecutableArch arch = ExecutableArch.X64) => new()
    {
        Directory = _dir,
        Executable = Path.Combine(_dir, "Vintagestory"),
        Version = "1.22.5",
        Architecture = arch,
        RequiredFramework = new Version(10, 0, 0),
    };

    [Fact]
    public void Arguments_are_built_in_the_order_the_game_expects()
    {
        var args = new GameLauncher(Install()).BuildArguments(new LaunchOptions
        {
            DataPath = "/data",
            ModPaths = { "/packs/a/Mods", "/packs/b/Mods" },
            Connect = "host:42420",
        });

        Assert.Equal(
            ["--dataPath", "/data",
             "--addModPath", "/packs/a/Mods",
             "--addModPath", "/packs/b/Mods",
             "--connect", "host:42420"],
            args);
    }

    [Fact]
    public void Mod_paths_repeat_the_flag_rather_than_joining_them()
    {
        var args = new GameLauncher(Install())
            .BuildArguments(new LaunchOptions { ModPaths = { "/a", "/b", "/c" } });

        Assert.Equal(3, args.Count(a => a == "--addModPath"));
        Assert.DoesNotContain(args, a => a.Contains(':') || a.Contains(','));
    }

    [Fact]
    public void Empty_options_produce_no_arguments()
        => Assert.Empty(new GameLauncher(Install()).BuildArguments(new LaunchOptions()));

    [Fact]
    public void A_preferred_runtime_root_is_used_and_set_under_both_variable_names()
    {
        var psi = new GameLauncher(Install()).BuildStartInfo(new LaunchOptions
        {
            PreferredDotnetRoot = _fakeRuntimeRoot,
        });

        // Both, because the arch-specific variable takes precedence for an apphost and a
        // stale DOTNET_ROOT_X64 in the user's shell would otherwise win.
        Assert.Equal(_fakeRuntimeRoot, psi.Environment["DOTNET_ROOT"]);
        Assert.Equal(_fakeRuntimeRoot, psi.Environment["DOTNET_ROOT_X64"]);
    }

    [Fact]
    public void An_arm64_game_would_get_the_arm64_variable_not_the_x64_one()
    {
        var psi = new GameLauncher(Install(ExecutableArch.Arm64)).BuildStartInfo(new LaunchOptions
        {
            PreferredDotnetRoot = _fakeRuntimeRoot,
        });

        Assert.Equal(_fakeRuntimeRoot, psi.Environment["DOTNET_ROOT_ARM64"]);

        // Environment is seeded from the current process, so DOTNET_ROOT_X64 may already
        // exist. What matters is that we did not point it at the arm64 root.
        if (psi.Environment.TryGetValue("DOTNET_ROOT_X64", out var x64))
            Assert.NotEqual(_fakeRuntimeRoot, x64);
    }

    [Fact]
    public void An_unusable_preferred_root_falls_through_rather_than_being_written()
    {
        var noFrameworks = Path.Combine(_dir, "empty-root");
        Directory.CreateDirectory(noFrameworks);

        var psi = new GameLauncher(Install()).BuildStartInfo(new LaunchOptions
        {
            PreferredDotnetRoot = noFrameworks,
        });

        // Whatever gets written must never be the bogus root: hostfxr falls back to the
        // machine install when DOTNET_ROOT holds no framework, so writing junk cannot
        // help, and clobbering a working value could hurt.
        if (psi.Environment.TryGetValue("DOTNET_ROOT", out var written))
            Assert.NotEqual(noFrameworks, written);
    }

    [Fact]
    public void ResolveRuntime_reports_what_it_found()
    {
        var resolution = new GameLauncher(Install())
            .ResolveRuntime(new LaunchOptions { PreferredDotnetRoot = _fakeRuntimeRoot });

        Assert.True(resolution.Resolved);
        Assert.Equal(_fakeRuntimeRoot, resolution.Runtime!.Root);
        Assert.Contains("10.0.10", resolution.Describe());

        // The fixture has no dotnet host, so arch is Unknown, which must not be reported
        // as a mismatch.
        Assert.False(resolution.ArchMismatch);
    }

    [Fact]
    public void A_wrong_architecture_runtime_is_reported_as_a_mismatch()
    {
        var resolution = new RuntimeResolution(
            new DotnetRuntime("/x", ExecutableArch.Arm64, [new Version(10, 0, 10)]),
            GameArch: ExecutableArch.X64,
            Required: new Version(10, 0, 0));

        Assert.True(resolution.Resolved);
        Assert.True(resolution.ArchMismatch);
    }

    [Fact]
    public void An_unresolved_runtime_explains_that_the_game_bundles_none()
    {
        var resolution = new RuntimeResolution(null, ExecutableArch.X64, new Version(10, 0, 0));

        Assert.False(resolution.Resolved);
        Assert.False(resolution.ArchMismatch);
        Assert.Contains("bundles no", resolution.Describe());
    }
}
