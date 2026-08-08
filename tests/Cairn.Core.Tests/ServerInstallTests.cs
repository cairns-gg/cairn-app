using System.Text.Json;
using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What ModDB's side field is allowed to decide, which is only what gets said.
/// </summary>
public class ModSidesTests
{
    [Theory]
    [InlineData("server", ModSide.Client)]
    [InlineData("client", ModSide.Server)]
    [InlineData("SERVER", ModSide.Client)]
    public void A_mod_for_the_other_side_is_worth_saying_so_about(string declared, ModSide installingFor)
        => Assert.True(ModSides.WrongSide(declared, installingFor));

    [Theory]
    [InlineData("client", ModSide.Client)]
    [InlineData("server", ModSide.Server)]
    [InlineData("both", ModSide.Server)]
    [InlineData("universal", ModSide.Client)]
    [InlineData("", ModSide.Server)]
    [InlineData(null, ModSide.Server)]
    public void Anything_else_says_nothing(string? declared, ModSide installingFor)
    {
        // Absent on plenty of mods and stale on others. A warning that fires on a field
        // nobody maintains teaches people to ignore warnings.
        Assert.False(ModSides.WrongSide(declared, installingFor));
    }
}

/// <summary>
/// Covers the half of an install Cairn could not previously see.
///
/// A dedicated server download is the client minus the client: same assets, same Lib, no
/// Vintagestory binary at all. Everything that decides whether something can start reads
/// the executable — its architecture, and the runtimeconfig beside it — so an install whose
/// only binary is the server has to be found by, and answered for, that binary.
/// </summary>
public class ServerInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-server-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string ClientName =>
        OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory";

    private static string ServerName =>
        OperatingSystem.IsWindows() ? "VintagestoryServer.exe" : "VintagestoryServer";

    /// <summary>An install with whichever binaries are named, each with its own config.</summary>
    private string Install(string name, (string Exe, string Framework)[] binaries)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        foreach (var (exe, framework) in binaries)
        {
            File.WriteAllBytes(Path.Combine(dir, exe), new byte[64]);
            File.WriteAllText(
                Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".runtimeconfig.json"),
                JsonSerializer.Serialize(new
                {
                    runtimeOptions = new { framework = new { name = "Microsoft.NETCore.App", version = framework } },
                }));
        }

        return dir;
    }

    [Fact]
    public void A_server_download_is_an_install()
    {
        var dir = Install("server", [(ServerName, "10.0.0")]);

        var install = GameInstall.TryAt(dir);

        Assert.NotNull(install);
        Assert.Equal(Path.Combine(dir, ServerName), install!.Executable);
        Assert.True(install.HasServer);
    }

    [Fact]
    public void A_server_is_asked_for_its_own_dotnet_rather_than_the_clients()
    {
        // The two are separate apphosts with separate configs. Reading the client's — or
        // falling back to a guess when it is not there — is not visibly wrong: it resolves,
        // downloads and installs a runtime, and only then fails to start on it. 1.21 wants
        // .NET 8 where 1.22 wants 10, so the guess is wrong within one supported release.
        var dir = Install("server", [(ServerName, "8.0.0")]);

        Assert.Equal(new Version(8, 0, 0), GameInstall.TryAt(dir)!.RequiredFramework);
    }

    [Fact]
    public void A_client_install_can_be_projected_onto_the_server_it_ships()
    {
        // Deliberately disagreeing versions: the point is which file was read, and equal
        // ones would pass whichever it was.
        var dir = Install("client", [(ClientName, "10.0.0"), (ServerName, "8.0.0")]);

        var client = GameInstall.TryAt(dir);
        Assert.Equal(Path.Combine(dir, ClientName), client!.Executable);
        Assert.Equal(new Version(10, 0, 0), client.RequiredFramework);
        Assert.True(client.HasServer);

        var server = client.AsServer();
        Assert.NotNull(server);
        Assert.Equal(Path.Combine(dir, ServerName), server!.Executable);
        Assert.Equal(new Version(8, 0, 0), server.RequiredFramework);

        // Same install underneath: it is one directory, and a pack's mods and data do not
        // change because the other binary is the one starting.
        Assert.Equal(client.Directory, server.Directory);
        Assert.Equal(client.Version, server.Version);
    }

    [Fact]
    public void An_install_with_no_server_says_so_rather_than_offering_one()
    {
        var dir = Install("client", [(ClientName, "10.0.0")]);

        Assert.False(GameInstall.TryAt(dir)!.HasServer);
        Assert.Null(GameInstall.TryAt(dir)!.AsServer());
    }

    [Fact]
    public void A_variant_naming_a_launcher_is_never_fallen_back_from()
    {
        // The marker exists so a modified client cannot be mistaken for the stock game. A
        // marker naming a launcher that is not there is refused — quietly running the
        // server binary instead would be the same class of substitution.
        var dir = Install("variant", [(ServerName, "10.0.0")]);
        File.WriteAllText(
            Path.Combine(dir, GameInstall.VariantMarker),
            """{"label":"Optimum","executable":"Optimum"}""");

        Assert.Null(GameInstall.TryAt(dir));
    }

    [Fact]
    public void Server_downloads_are_published_for_this_machine_only_where_they_exist()
    {
        var keys = GameCatalog.ServerPlatformKeys;

        // The generic key is what every version before 1.18.15 published instead of the
        // platform ones, so it is the fallback rather than an alternative.
        if (OperatingSystem.IsLinux()) Assert.Equal(["linuxserver", "server"], keys);
        if (OperatingSystem.IsWindows()) Assert.Equal(["windowsserver", "server"], keys);

        // There has never been a mac server download. A client install ships the server
        // binary, so the client artifact is the only way to have one there at all.
        if (OperatingSystem.IsMacOS()) Assert.Equal(GameCatalog.PlatformKeys, keys);
    }

    [Fact]
    public void A_windows_server_zip_is_something_Cairn_can_unpack()
    {
        // The Windows *client* is an installer exe and the Windows *server* is a zip, so
        // "not a tarball" and "not installable" are different questions — conflating them
        // is what left Windows unable to fetch the game at all once before.
        var manifest = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(
            """
            {
              "1.22.6": {
                "windowsserver": {
                  "filename": "vs_server_win-x64_1.22.6.zip",
                  "urls": { "cdn": "https://cdn.example/vs_server_win-x64_1.22.6.zip" }
                }
              },
              "1.17.12": {
                "server": {
                  "filename": "vs_server_1.17.12.tar.gz",
                  "urls": { "cdn": "https://cdn.example/vs_server_1.17.12.tar.gz" }
                }
              }
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var releases = GameCatalog.Parse(manifest, ["windowsserver", "server"]);

        Assert.Equal(2, releases.Count);
        Assert.All(releases, r =>
        {
            Assert.True(r.IsArchive);
            Assert.True(r.CanInstall);
            Assert.False(r.IsWindowsInstaller);
        });

        // Newest first, and the pre-split version reached through the generic key.
        Assert.Equal("windowsserver", releases[0].Platform);
        Assert.Equal("server", releases[1].Platform);
    }
}
