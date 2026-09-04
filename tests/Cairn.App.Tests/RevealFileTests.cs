using Cairn.Core;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Asking a file manager to open a folder with one file picked out in it.
///
/// A platform fork, so it takes the platform as a parameter and all three answers are
/// checked from whichever machine is running — see <see cref="HostOs"/>. This one earns it:
/// the arguments are unlike each other, only one of the three can be exercised here, and
/// Explorer's are unlike anything else in the world. Nothing calls <see cref="Files.Reveal"/>
/// itself in a test, because a passing run would leave a file manager window open on the
/// machine that ran it.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class RevealFileTests
{
    /// <summary>
    /// The switch is <c>/select,</c> — a comma, no space — and the path belongs to the same
    /// argument, quoted inside it.
    /// </summary>
    [Fact]
    public void Windows_asks_explorer_to_select_the_file()
    {
        var plan = Files.RevealCommand(
            @"C:\Users\dizzyd\.cairn\packs\anego\data\ModConfig\bedspawn.json", HostOs.Windows);

        Assert.NotNull(plan);
        Assert.Equal("explorer.exe", plan!.Exe);
        Assert.Null(plan.Args);
        Assert.Equal(
            @"/select,""C:\Users\dizzyd\.cairn\packs\anego\data\ModConfig\bedspawn.json""",
            plan.CommandLine);
    }

    /// <summary>
    /// The reason it is a command line rather than argv, and the whole of what was wrong
    /// with it before.
    ///
    /// ProcessStartInfo.ArgumentList quotes any argument containing a space, wrapping the
    /// whole of it — so this path arrived at Explorer as "/select,C:\Users\Dave Smith\...",
    /// with the quote ahead of the switch, and Explorer answered by dropping the selection
    /// and opening Documents. Measured on Windows: a path with no space is quoted by nobody
    /// and works either way, which is what kept it hidden.
    ///
    /// The quotes must therefore sit round the path and inside the argument, which is what
    /// this asserts.
    /// </summary>
    [Fact]
    public void A_path_with_a_space_keeps_the_quotes_inside_the_switch()
    {
        var plan = Files.RevealCommand(
            @"C:\Users\Dave Smith\.cairn\packs\anego\data\ModConfig\bedspawn.json", HostOs.Windows);

        Assert.NotNull(plan);
        Assert.StartsWith("/select,\"", plan!.CommandLine);
        Assert.EndsWith("bedspawn.json\"", plan.CommandLine);

        // The failure this replaced: the whole argument wrapped, switch and all.
        Assert.DoesNotContain("\"/select,", plan.CommandLine);
    }

    /// <summary>
    /// argv here, and deliberately: a Unix path may hold a space, a quote or a backslash,
    /// and argv is the one shape with no escaping rules to get wrong.
    /// </summary>
    [Fact]
    public void MacOs_asks_open_to_reveal_it()
    {
        var plan = Files.RevealCommand("/Users/dave/.cairn/packs/anego/data/ModConfig/bedspawn.json",
                                       HostOs.MacOs);

        Assert.NotNull(plan);
        Assert.Equal("open", plan!.Exe);
        Assert.Null(plan.CommandLine);
        Assert.Equal(["-R", "/Users/dave/.cairn/packs/anego/data/ModConfig/bedspawn.json"], plan.Args);
    }

    [Fact]
    public void A_mac_path_with_a_quote_in_it_needs_no_escaping()
    {
        var plan = Files.RevealCommand("/Users/dave/od\"d/ModConfig/bedspawn.json", HostOs.MacOs);

        Assert.NotNull(plan);
        Assert.Equal(["-R", "/Users/dave/od\"d/ModConfig/bedspawn.json"], plan!.Args);
    }

    /// <summary>
    /// Nothing to ask for: freedesktop has no "select this file", and the file managers that
    /// do have one disagree on the flag. Null rather than a guess, so the caller falls back
    /// to opening the folder — which is where the file is, and is the whole point of the
    /// button anyway.
    /// </summary>
    [Fact]
    public void Linux_has_no_way_to_ask_so_the_folder_is_the_answer()
    {
        Assert.Null(Files.RevealCommand("/home/dave/.cairn/packs/anego/data/ModConfig/bedspawn.json",
                                        HostOs.Linux));
    }

    /// <summary>
    /// The same guard OpenFolder has, for the same reason: this hands a path to the shell,
    /// so it only ever hands it one that is what it says it is.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_revealed_for_nothing(string? path) => Assert.False(Files.Reveal(path));

    [Fact]
    public void A_file_that_is_not_there_is_not_revealed()
    {
        var missing = Path.Combine(Path.GetTempPath(),
                                   "cairn-reveal-" + Guid.NewGuid().ToString("n")[..8] + ".json");

        Assert.False(Files.Reveal(missing));
    }

    /// <summary>A directory is not a file; OpenFolder is the call for one of those.</summary>
    [Fact]
    public void A_directory_is_not_revealed_as_a_file()
    {
        Assert.False(Files.Reveal(Path.GetTempPath()));
    }
}
