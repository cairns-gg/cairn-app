using System.Text.Json;
using Cairn.Core.Games;
using Xunit;

namespace Cairn.Core.Tests;

public class GameCatalogTests
{
    /// <summary>Shaped exactly like the live manifest at api.vintagestory.at/stable.json.</summary>
    private const string Manifest = """
    {
      "1.22.5": {
        "windows": {
          "filename": "vs_install_win-x64_1.22.5.exe", "filesize": "570.2 MB",
          "md5": "8b28f69adff116e83a1c39dd613c6d65",
          "urls": { "cdn": "https://cdn.example/vs_install_win-x64_1.22.5.exe",
                    "local": "https://account.example/vs_install_win-x64_1.22.5.exe" },
          "latest": 1
        },
        "mac-x64": {
          "filename": "vs_client_osx-x64_1.22.5.tar.gz", "filesize": "613.5 MB",
          "md5": "6131fa037b8300000000000000000000",
          "urls": { "cdn": "https://cdn.example/vs_client_osx-x64_1.22.5.tar.gz" },
          "latest": 1
        },
        "linux": {
          "filename": "vs_client_linux-x64_1.22.5.tar.gz", "filesize": "590.2 MB",
          "md5": "ffeb9b11b78400000000000000000000",
          "urls": { "cdn": "https://cdn.example/vs_client_linux-x64_1.22.5.tar.gz" }
        }
      },
      "1.21.5": {
        "mac-x64": {
          "filename": "vs_client_osx-x64_1.21.5.tar.gz", "filesize": "563.6 MB",
          "md5": "8b8838c3937100000000000000000000",
          "urls": { "cdn": "https://cdn.example/vs_client_osx-x64_1.21.5.tar.gz" }
        }
      },
      "1.10.0": {
        "mac-x64": {
          "filename": "vs_client_osx-x64_1.10.0.tar.gz", "filesize": "300 MB",
          "md5": "aaaa", "urls": { "cdn": "https://cdn.example/old.tar.gz" }
        }
      },
      "1.9.14": {
        "mac-x64": {
          "filename": "vs_client_osx-x64_1.9.14.tar.gz", "filesize": "290 MB",
          "md5": "bbbb", "urls": { "cdn": "https://cdn.example/older.tar.gz" }
        }
      },
      "1.22.6": {
        "mac-x64": { "filename": "broken.tar.gz", "filesize": "1 MB", "md5": "cccc", "urls": {} }
      }
    }
    """;

    private static List<GameRelease> Parse(string platform)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(
            Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return GameCatalog.Parse(raw, platform);
    }

    [Fact]
    public void Only_releases_for_the_requested_platform_are_returned()
    {
        var mac = Parse("mac-x64");
        Assert.Contains(mac, r => r.Version == "1.22.5");
        Assert.Contains(mac, r => r.Version == "1.21.5");

        var windows = Parse("windows");
        Assert.Single(windows);
        Assert.Equal("1.22.5", windows[0].Version);
    }

    [Fact]
    public void Versions_are_ordered_newest_first_numerically()
    {
        var versions = Parse("mac-x64").Select(r => r.Version).ToList();

        // 1.10.0 must outrank 1.9.14 — a lexical sort would get this backwards.
        Assert.Equal(["1.22.5", "1.21.5", "1.10.0", "1.9.14"], versions);
    }

    [Fact]
    public void An_entry_with_no_usable_url_is_skipped_rather_than_failing_the_catalog()
    {
        var mac = Parse("mac-x64");
        Assert.DoesNotContain(mac, r => r.Version == "1.22.6");
        Assert.NotEmpty(mac);
    }

    [Fact]
    public void The_cdn_url_is_preferred_over_the_account_url()
    {
        var windows = Parse("windows").Single();
        Assert.StartsWith("https://cdn.example/", windows.Artifact.DownloadUrl);
    }

    [Fact]
    public void Windows_ships_an_installer_rather_than_an_archive()
    {
        Assert.False(Parse("windows").Single().IsArchive);
        Assert.True(Parse("mac-x64").First().IsArchive);
        Assert.True(Parse("linux").Single().IsArchive);
    }

    [Fact]
    public void The_windows_installer_is_still_something_Cairn_can_install()
    {
        var windows = Parse("windows").Single();

        // It is an Inno Setup installer, which takes a target directory and runs headless.
        // Not being a tarball is not the same as not being installable, and treating the
        // two as one thing is what left Windows unable to fetch the game at all.
        Assert.True(windows.IsWindowsInstaller);
        Assert.True(windows.CanInstall);

        Assert.All(Parse("mac-x64").Concat(Parse("linux")), r =>
        {
            Assert.False(r.IsWindowsInstaller);
            Assert.True(r.CanInstall);
        });
    }

    [Fact]
    public void Parsing_a_null_manifest_yields_nothing_rather_than_throwing()
        => Assert.Empty(GameCatalog.Parse(null, "mac-x64"));

    [Fact]
    public void Platform_key_is_the_x64_mac_build()
    {
        // The published clients are x64 on every platform.
        if (OperatingSystem.IsMacOS()) Assert.Equal("mac-x64", GameCatalog.PlatformKey);
        if (OperatingSystem.IsLinux()) Assert.Equal("linux", GameCatalog.PlatformKey);
        if (OperatingSystem.IsWindows()) Assert.Equal("windows", GameCatalog.PlatformKey);
    }
}

public class GameStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-gamestore-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly GameStore _store;

    public GameStoreTests() => _store = new GameStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("1.22.5")]
    [InlineData("1.22.0-rc.1")]
    [InlineData("1.9.14")]
    public void Version_directory_names_are_accepted(string version)
        => Assert.True(GameStore.IsValidVersion(version));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(null)]
    public void Version_names_that_could_escape_the_store_are_refused(string? version)
    {
        Assert.False(GameStore.IsValidVersion(version));
        Assert.Throws<ArgumentException>(() => _store.InstallDir(version!));
    }

    [Fact]
    public void An_empty_store_reports_nothing_installed()
    {
        Assert.Empty(_store.ListInstalled());
        Assert.False(_store.IsInstalled("1.22.5"));
        Assert.Null(_store.Find("1.22.5"));
    }

    [Fact]
    public void A_directory_without_a_game_in_it_is_not_an_install()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1.22.5"));
        Assert.Empty(_store.ListInstalled());
        Assert.False(_store.IsInstalled("1.22.5"));
    }
}

public class GameLibraryTests
{
    private static GameInstall Fake(string version, string dir) => new()
    {
        Directory = dir,
        Executable = Path.Combine(dir, "Vintagestory"),
        Version = version,
        Architecture = Cairn.Core.Runtime.ExecutableArch.X64,
        RequiredFramework = new Version(10, 0, 0),
    };

    [Fact]
    public void A_pack_resolves_to_the_system_install_when_the_version_matches()
    {
        var library = new GameLibrary(
            new GameStore(Path.Combine(Path.GetTempPath(), "Cairn-empty-" + Guid.NewGuid().ToString("n")[..6])),
            Fake("1.22.5", "/games/system"));

        Assert.NotNull(library.ForVersion("1.22.5"));
        Assert.Null(library.ForVersion("1.21.5"));
    }

    [Fact]
    public void Fallback_is_offered_when_the_exact_version_is_absent()
    {
        var system = Fake("1.22.5", "/games/system");
        var library = new GameLibrary(
            new GameStore(Path.Combine(Path.GetTempPath(), "Cairn-empty-" + Guid.NewGuid().ToString("n")[..6])),
            system);

        Assert.Null(library.ForVersion("1.21.5"));
        Assert.Equal(system, library.Fallback);
    }

    [Fact]
    public void With_no_installs_at_all_nothing_resolves()
    {
        var library = new GameLibrary(
            new GameStore(Path.Combine(Path.GetTempPath(), "Cairn-empty-" + Guid.NewGuid().ToString("n")[..6])),
            null);

        Assert.Null(library.ForVersion("1.22.5"));
        Assert.Null(library.Fallback);
    }
}
