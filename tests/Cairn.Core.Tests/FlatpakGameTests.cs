using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The game installed as a Flatpak — an ordinary install in a place nothing looked, next to
/// a .NET that may be the only one on the machine.
///
/// The layout here is copied from a real Bazzite install rather than invented: the deploy
/// under <c>&lt;root&gt;/app/&lt;id&gt;/current/active</c>, the game at
/// <c>files/extra/vintagestory</c> because the Flatpak unpacks the shipped tarball as extra
/// data, and the runtime at <c>files/lib/dotnet</c> because <c>/app</c> in the sandbox is
/// <c>files</c> on the host.
/// </summary>
public class FlatpakGameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-flatpak-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previousUserDir =
        Environment.GetEnvironmentVariable("FLATPAK_USER_DIR");

    public FlatpakGameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FLATPAK_USER_DIR", _previousUserDir);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The deploy directory of a Flatpak install under <paramref name="installation"/>.</summary>
    private static string Deploy(string installation) => Path.Combine(
        installation, "app", FlatpakGame.AppId, "current", "active");

    /// <summary>
    /// Enough of a deploy for the real code to accept it: the game where the Flatpak puts
    /// it, and — unless asked otherwise — the bundled runtime beside it.
    /// </summary>
    private string Install(bool withRuntime = true, bool emptyRuntime = false)
    {
        var deploy = Deploy(_root);
        var game = Path.Combine(deploy, "files", "extra", "vintagestory");
        Directory.CreateDirectory(game);

        File.WriteAllText(Path.Combine(game, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(game, "VintagestoryAPI.dll"), "");

        var dotnet = Path.Combine(deploy, "files", "lib", "dotnet");

        if (emptyRuntime) Directory.CreateDirectory(dotnet);
        else if (withRuntime)
            Directory.CreateDirectory(Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", "10.0.8"));

        return game;
    }

    [Fact]
    public void An_install_in_a_deploy_carries_the_runtime_beside_it()
    {
        var game = Install();
        var install = GameInstall.TryAt(game);

        Assert.NotNull(install);
        Assert.NotNull(install.DotnetRoot);
        Assert.Equal(
            Path.Combine(Deploy(_root), "files", "lib", "dotnet"),
            Path.GetFullPath(install.DotnetRoot!));

        // The point of carrying it: on a host with no system .NET this is the only root
        // that can host the game, and it has to be the one resolved.
        var resolution = new GameLauncher(install).ResolveRuntime();
        Assert.True(resolution.Resolved);
        Assert.Equal(new Version(10, 0, 8), resolution.Runtime!.Best(new Version(10, 0, 0)));
    }

    [Fact]
    public void An_ordinary_install_carries_no_runtime()
    {
        var dir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        Assert.Null(GameInstall.TryAt(dir)?.DotnetRoot);
    }

    [Fact]
    public void A_sibling_directory_that_is_not_a_dotnet_root_is_not_offered()
    {
        // Offering it unchecked would be worse than offering nothing: a preferred root
        // wins outright, so an empty one would shadow the machine's real .NET and then
        // resolve to no framework at all.
        var install = GameInstall.TryAt(Install(emptyRuntime: true));

        Assert.NotNull(install);
        Assert.Null(install.DotnetRoot);
    }

    [Fact]
    public void Candidate_directories_follow_the_symlink_that_survives_an_update()
    {
        Environment.SetEnvironmentVariable("FLATPAK_USER_DIR", _root);

        var directories = FlatpakGame.GameDirectories().ToList();

        // Never the content-hashed deploy path that `flatpak info --show-location` reports:
        // that directory is renamed by every `flatpak update`.
        Assert.Equal(
            Path.Combine(Deploy(_root), "files", "extra", "vintagestory"),
            directories[0]);

        Assert.Contains(
            Path.Combine("/var/lib/flatpak", "app", FlatpakGame.AppId, "current", "active",
                "files", "extra", "vintagestory"),
            directories);
    }

    [Fact]
    public void A_flatpak_install_is_locatable_once_its_installation_is_known()
    {
        if (!OperatingSystem.IsLinux()) return;    // only Linux enumerates Flatpak roots

        var game = Install();
        Environment.SetEnvironmentVariable("FLATPAK_USER_DIR", _root);

        Assert.Contains(GameInstall.CandidateDirectories(), d => d == game);
    }

    [Fact]
    public void The_installs_own_runtime_is_preferred_over_a_managed_one()
    {
        var install = GameInstall.TryAt(Install());
        Assert.NotNull(install);

        var managed = Path.Combine(_root, "managed-dotnet");
        Directory.CreateDirectory(Path.Combine(managed, "shared", "Microsoft.NETCore.App", "10.0.20"));

        var resolution = new GameLauncher(install)
            .ResolveRuntime(new LaunchOptions { PreferredDotnetRoot = managed });

        // Higher version, and still not chosen: the bundled one is what the game runs on
        // when launched the ordinary way, so it is not something to improve on silently.
        Assert.Equal(install.DotnetRoot, resolution.Runtime!.Root);
    }

    [Fact]
    public void A_managed_runtime_still_applies_when_the_bundled_one_cannot_serve()
    {
        var managed = Path.Combine(_root, "managed-dotnet");
        Directory.CreateDirectory(Path.Combine(managed, "shared", "Microsoft.NETCore.App", "10.0.20"));

        // A bundled root offering only .NET 9 cannot host a net10.0 app. Trying just the
        // first preferred root would drop the managed one rather than fall back to it.
        var bundled = Path.Combine(_root, "bundled-dotnet");
        Directory.CreateDirectory(Path.Combine(bundled, "shared", "Microsoft.NETCore.App", "9.0.18"));

        var found = DotnetRuntimeLocator.Find(
            ExecutableArch.X64, new Version(10, 0, 0), bundled, managed);

        Assert.NotNull(found);
        Assert.Equal(managed, found.Root);
    }

    [Fact]
    public void A_configured_installation_is_a_root_like_any_other()
    {
        var conf = Path.Combine(_root, "installations.d");
        Directory.CreateDirectory(conf);

        File.WriteAllText(Path.Combine(conf, "sdcard.conf"), """
            [Installation "sdcard"]
            Path = /run/media/mmcblk0p1/flatpak
            DisplayName=SD Card
            """);

        // A file that parses as nothing must cost nothing, not throw on the way past.
        File.WriteAllText(Path.Combine(conf, "broken.conf"), "not an ini at all\nPath\n[x\n");

        Assert.Equal(
            ["/run/media/mmcblk0p1/flatpak"],
            FlatpakGame.ConfiguredInstallations(conf).ToList());
    }

    [Fact]
    public void A_key_that_merely_starts_with_Path_is_not_one()
    {
        var conf = Path.Combine(_root, "installations.d-2");
        Directory.CreateDirectory(conf);
        File.WriteAllText(Path.Combine(conf, "x.conf"), "PathPrefix=/wrong\nPath=/right\n");

        Assert.Equal(["/right"], FlatpakGame.ConfiguredInstallations(conf).ToList());
    }

    [Fact]
    public void Naming_an_install_does_not_drop_its_runtime()
    {
        // GameStore rebuilds an install to name it from its directory. Losing the bundled
        // root there leaves an install that launches on this machine when found by listing
        // and not when addressed by path.
        var game = Install();
        var store = new GameStore(Path.Combine(_root, "store"));

        Assert.Equal(GameInstall.TryAt(game)!.DotnetRoot, store.At(game)?.DotnetRoot);
    }
}
