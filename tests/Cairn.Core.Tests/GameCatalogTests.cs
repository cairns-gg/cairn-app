using System.Text.Json;
using Cairn.Core.Games;
using Cairn.Core.Runtime;
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
        "mac-arm64": {
          "filename": "vs_client_osx-arm64_1.22.5.tar.gz", "filesize": "607.8 MB",
          "md5": "e7e4dd2b38f500000000000000000000",
          "urls": { "cdn": "https://cdn.example/vs_client_osx-arm64_1.22.5.tar.gz" },
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
        },
        "mac-arm64": {
          "filename": "vs_client_osx-arm64_1.10.0.tar.gz", "filesize": "300 MB",
          "md5": "dddd", "urls": {}
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

    private static List<GameRelease> ParseMacPreferringNative()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(
            Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return GameCatalog.Parse(raw, ["mac-arm64", "mac-x64"]);
    }

    [Fact]
    public void The_native_mac_client_wins_where_one_is_published()
    {
        var release = ParseMacPreferringNative().Single(r => r.Version == "1.22.5");

        // An x64 client on Apple Silicon runs under Rosetta and has to be hosted by an x64
        // .NET, which is a second runtime to find on a machine whose own is arm64.
        Assert.Equal("mac-arm64", release.Platform);
        Assert.Contains("osx-arm64", release.Artifact.FileName);
    }

    [Fact]
    public void A_version_published_before_the_native_client_existed_is_still_offered()
    {
        // 1.22 is where mac-arm64 appears; nothing older publishes one. Preferring the
        // native key without falling back would not merely install the wrong client, it
        // would drop every older version out of the list of versions installable at all.
        var release = ParseMacPreferringNative().Single(r => r.Version == "1.21.5");

        Assert.Equal("mac-x64", release.Platform);
    }

    [Fact]
    public void A_malformed_native_entry_falls_through_rather_than_losing_the_version()
    {
        // 1.10.0 publishes a mac-arm64 entry with no usable url. Preferring a key must mean
        // preferring an artifact it actually yields.
        var release = ParseMacPreferringNative().Single(r => r.Version == "1.10.0");

        Assert.Equal("mac-x64", release.Platform);
    }

    [Fact]
    public void Platform_keys_prefer_the_client_this_machine_runs_natively()
    {
        var keys = GameCatalog.PlatformKeys;

        if (OperatingSystem.IsLinux()) Assert.Equal(["linux"], keys);
        if (OperatingSystem.IsWindows()) Assert.Equal(["windows"], keys);

        if (OperatingSystem.IsMacOS())
        {
            // The x64 client is the fallback on every Mac, never absent: it is the only
            // artifact a pre-1.22 version has.
            Assert.Equal("mac-x64", keys[^1]);

            Assert.Equal(
                ExecutableImage.NativeArchitecture == ExecutableArch.Arm64
                    ? ["mac-arm64", "mac-x64"]
                    : ["mac-x64"],
                keys);
        }
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

    [Fact]
    public void An_install_is_removed_by_the_directory_it_was_found_in()
    {
        // Find already tolerates a directory name that differs from the version the
        // assembly reports. Deriving the path back from that version deletes nothing and
        // reports success, leaving a version that looks removed and goes on working.
        var dir = Path.Combine(_root, "vintagestory-1.22.5");
        Directory.CreateDirectory(dir);

        _store.Remove(Fake("1.22.5", dir));

        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData("/somewhere/else")]
    [InlineData("")]          // the store root itself
    public void Only_installs_inside_the_store_can_be_removed(string relative)
    {
        var dir = relative.Length == 0 ? _root : relative;
        Directory.CreateDirectory(_root);

        Assert.Throws<InvalidOperationException>(() => _store.Remove(Fake("1.22.5", dir)));
        Assert.True(Directory.Exists(_root));
    }

    internal static GameInstall Fake(string version, string dir) => new()
    {
        Directory = dir,
        Executable = Path.Combine(dir, "Vintagestory"),
        Version = version,
        Architecture = Cairn.Core.Runtime.ExecutableArch.X64,
        RequiredFramework = new Version(10, 0, 0),
    };

    /// <summary>An install real enough for GameInstall.TryAt, in a directory of its own.</summary>
    private static string Materialise(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(
            Path.Combine(dir, OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory"),
            new byte[64]);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        return dir;
    }

    [Fact]
    public void An_install_directory_is_a_bundle_only_where_that_means_something()
    {
        var name = Path.GetFileName(_store.InstallDir("1.22.5"));

        // The game's Info.plist opts out of Retina, and the window server reads that only
        // from a bundle — so on macOS the directory has to be one, or the game renders into
        // a quarter of its own window. Nowhere else has the notion.
        Assert.Equal(OperatingSystem.IsMacOS() ? "1.22.5.app" : "1.22.5", name);
    }

    [Fact]
    public void A_bundled_install_is_still_found_by_its_version()
    {
        Materialise(_store.InstallDir("1.22.5"));

        Assert.True(_store.IsInstalled("1.22.5"));
        Assert.NotNull(_store.Find("1.22.5"));

        // Its metadata is unreadable, so what ListInstalled reports is the directory-name
        // fallback answering — which has to see through the suffix to do it. This is the
        // name the version picker and the installed list show.
        Assert.Equal("1.22.5", _store.ListInstalled().Single().Version);
    }

    [Fact]
    public void A_variant_behind_two_suffixes_still_reports_the_version_it_is_of()
    {
        var dir = Materialise(Path.Combine(_root, GameStore.DirectoryNameFor("1.22.5-optimum")));
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), "Optimum");

        var install = _store.At(dir);

        Assert.NotNull(install);
        Assert.Equal("1.22.5", install!.Version);
        Assert.True(install.IsVariant);
    }

    [Fact]
    public void An_install_made_before_installs_were_bundles_is_migrated()
    {
        Materialise(Path.Combine(_root, "1.22.5"));

        var moved = _store.MigrateToBundles();

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Empty(moved);
            return;
        }

        Assert.Single(moved);
        Assert.True(Directory.Exists(Path.Combine(_root, "1.22.5.app")));
        Assert.False(Directory.Exists(Path.Combine(_root, "1.22.5")));
        Assert.NotNull(_store.Find("1.22.5"));
    }

    [Fact]
    public void A_pack_that_recorded_the_old_path_follows_it_to_the_bundle()
    {
        var before = Path.Combine(_root, "1.22.5-optimum");
        Materialise(before);
        _store.MigrateToBundles();

        // What a pack's local state holds is the path as it was when the choice was made.
        // Losing it here means silently falling back to the stock game.
        Assert.NotNull(_store.At(before));
    }

    [Fact]
    public void Migration_leaves_alone_what_is_not_an_install()
    {
        // An interrupted download's staging directory. Renamed into place it would be a
        // bundle with half a game in it.
        Directory.CreateDirectory(Path.Combine(_root, "1.22.5.staging"));

        Assert.Empty(_store.MigrateToBundles());
        Assert.True(Directory.Exists(Path.Combine(_root, "1.22.5.staging")));
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
