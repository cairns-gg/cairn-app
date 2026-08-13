using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.Runtime;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What a pack may put in its connect field, which is handed to the game as the value of
/// --connect.
///
/// It arrives from somebody else's pack. There is no shell — every argument goes through
/// ArgumentList as its own argv entry — so this is not command injection. What it is
/// instead is an argument the game's own parser reads, and CommandLineParser treats a token
/// beginning with "--" as an option name rather than a value. Whether the game acts on a
/// partially parsed argv was never established; refusing to produce one makes it moot.
/// </summary>
public class ServerAddressTests
{
    [Theory]
    [InlineData("play.example.com")]
    [InlineData("play.example.com:42420")]
    [InlineData("192.168.1.5:42420")]
    [InlineData("10.0.0.1")]
    [InlineData("vs")]                       // a LAN name somebody made up
    [InlineData("my-server.home:42420")]
    [InlineData("[::1]:42420")]
    [InlineData("[2001:db8::1]")]
    [InlineData("::1")]                      // a bare IPv6 literal, no port
    [InlineData(null)]                       // absent, which is most packs
    [InlineData("")]
    public void An_address_a_pack_might_really_have_is_allowed(string? address) =>
        Assert.Null(ServerAddress.Problem(address));

    /// <summary>
    /// The one that matters. Anything the game would read as another option rather than as
    /// this option's value.
    /// </summary>
    [Theory]
    [InlineData("--logPath=C:\\Users\\me\\evil")]
    [InlineData("--traceLog")]
    [InlineData("-c")]
    [InlineData("--addModPath")]
    public void A_value_that_is_really_another_option_is_refused(string address)
    {
        var problem = ServerAddress.Problem(address);

        Assert.NotNull(problem);
        Assert.Contains("option", problem);
    }

    [Theory]
    [InlineData("host with spaces:1234")]
    [InlineData("host\tname")]
    [InlineData(" play.example.com")]
    [InlineData("play.example.com ")]
    [InlineData("play.example.com:notaport")]
    [InlineData("play.example.com:0")]
    [InlineData("play.example.com:99999")]
    [InlineData(":42420")]
    [InlineData("under_score/slash")]
    public void Anything_that_is_not_an_address_is_refused(string address) =>
        Assert.NotNull(ServerAddress.Problem(address));

    [Fact]
    public void An_absurdly_long_value_is_refused() =>
        Assert.NotNull(ServerAddress.Problem(new string('a', 500) + ".example.com"));

    // ---- and the two places it is enforced ----

    [Fact]
    public void A_manifest_carrying_one_says_so()
    {
        var manifest = new PackManifest
        {
            Id = "anego",
            GameVersion = "1.22.5",
            Connect = "--logPath=/tmp/evil",
        };

        Assert.Contains(manifest.ValidatePack(), p => p.Contains("connect"));
    }

    /// <summary>
    /// The boundary rather than the form. A manifest is checked when it is synced; a
    /// pack.json edited by hand afterwards reaches argv through the launcher and nothing
    /// else, so the launcher checks too — and drops it rather than refusing to start,
    /// because the main menu is where an unusable address would have led anyway.
    /// </summary>
    [Fact]
    public void The_launcher_will_not_pass_one_on_even_if_it_reaches_it()
    {
        var launcher = new GameLauncher(new GameInstall
        {
            Directory = "/games/1.22.5",
            Executable = "/games/1.22.5/Vintagestory",
            Version = "1.22.5",
            Architecture = ExecutableArch.X64,
            RequiredFramework = new Version(10, 0, 0),
        });

        var hostile = launcher.BuildArguments(new LaunchOptions
        {
            DataPath = "/tmp/data",
            Connect = "--logPath=/tmp/evil",
        });

        Assert.DoesNotContain("--connect", hostile);
        Assert.DoesNotContain("--logPath=/tmp/evil", hostile);

        var ordinary = launcher.BuildArguments(new LaunchOptions
        {
            DataPath = "/tmp/data",
            Connect = "play.example.com:42420",
        });

        Assert.Contains("--connect", ordinary);
        Assert.Contains("play.example.com:42420", ordinary);
    }
}
