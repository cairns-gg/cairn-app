using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Where the root comes from. Nothing exercised this before: both suites set CAIRN_HOME and
/// so only ever took the first branch, which left the fallback — the one every real user
/// takes — never run outside somebody's own machine.
///
/// Driven through the overload that takes what it reads, because the real one reads the
/// running user's profile. A test that had to create ~/.cairn to check the default would be
/// writing to the developer's actual home directory to prove a rule about paths.
/// </summary>
public class CairnHomeTests
{
    private const string Default = "/home/someone/.cairn";
    private static readonly string Pointer = Path.Combine(Default, CairnHome.PointerName);

    private static Func<string, string?> Pointing(string? contents) => _ => contents;
    private static readonly Func<string, string?> NoPointer = _ => null;

    [Fact]
    public void With_nothing_set_the_default_is_used()
    {
        var r = CairnHome.Resolve(null, Default, NoPointer);

        Assert.Equal(Default, r.Root);
        Assert.Equal(HomeSource.Default, r.Source);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void The_environment_wins()
    {
        // ServerUnit writes Environment=CAIRN_HOME= into systemd units. A pointer file that
        // outranked it would redirect a running server to somewhere else entirely.
        var r = CairnHome.Resolve("/var/lib/cairn", Default, Pointing("/mnt/big/cairn"));

        Assert.Equal("/var/lib/cairn", r.Root);
        Assert.Equal(HomeSource.Environment, r.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void A_blank_environment_variable_counts_as_unset(string blank)
    {
        // It used to be taken literally, which made Root the empty string and put every
        // path under it relative to the working directory.
        var r = CairnHome.Resolve(blank, Default, NoPointer);

        Assert.Equal(Default, r.Root);
        Assert.Equal(HomeSource.Default, r.Source);
    }

    [Fact]
    public void A_pointer_file_moves_the_root()
    {
        var r = CairnHome.Resolve(null, Default, Pointing("/mnt/big/cairn"));

        Assert.Equal("/mnt/big/cairn", r.Root);
        Assert.Equal(HomeSource.Pointer, r.Source);
        Assert.Null(r.Problem);
    }

    [Fact]
    public void The_pointer_is_read_from_the_default_root()
    {
        // Not from the root it names, which only somebody who already knew the answer could
        // look in.
        string? asked = null;
        CairnHome.Resolve(null, Default, p => { asked = p; return null; });

        Assert.Equal(Pointer, asked);
    }

    [Theory]
    [InlineData("/mnt/big/cairn\n")]
    [InlineData("/mnt/big/cairn\r\n")]
    [InlineData("  /mnt/big/cairn  ")]
    public void Whitespace_around_the_path_is_ignored(string written)
    {
        // Every editor adds a trailing newline, and SetPointer writes one deliberately.
        Assert.Equal("/mnt/big/cairn", CairnHome.Resolve(null, Default, Pointing(written)).Root);
    }

    [Fact]
    public void An_empty_pointer_falls_back_and_says_so()
    {
        // A half-finished edit, not an instruction to use the default.
        var r = CairnHome.Resolve(null, Default, Pointing("  \n"));

        Assert.Equal(Default, r.Root);
        Assert.Equal(HomeSource.Default, r.Source);
        Assert.NotNull(r.Problem);
        Assert.Contains(Pointer, r.Problem);
    }

    [Fact]
    public void A_relative_pointer_is_refused()
    {
        // It would resolve against the working directory, which for a launcher started from
        // a Dock tile or a protocol handler is not a place anybody chose.
        var r = CairnHome.Resolve(null, Default, Pointing("cairn-data"));

        Assert.Equal(Default, r.Root);
        Assert.NotNull(r.Problem);
        Assert.Contains("absolute", r.Problem);
    }

    [Fact]
    public void An_unreadable_pointer_is_reported_rather_than_thrown()
    {
        // Root is read from everywhere and cannot be a property that throws.
        var r = CairnHome.Resolve(null, Default, _ => throw new IOException("disk is asleep"));

        Assert.Equal(Default, r.Root);
        Assert.Equal(HomeSource.Default, r.Source);
        Assert.Contains("disk is asleep", r.Problem);
    }

    [Fact]
    public void The_default_root_can_be_moved_for_a_sandboxed_run()
    {
        // What dev.sh and the UI suite use. CAIRN_HOME would have done it too, and would
        // have made every sandboxed run take the one branch users do not — including
        // refusing the move, which is the thing a sandbox exists to try.
        var previous = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", "/tmp/sandbox/.cairn");

        try
        {
            Assert.Equal("/tmp/sandbox/.cairn", CairnHome.DefaultRoot);

            // And it is a default, not an override: the pointer still outranks it, which is
            // the whole point of sandboxing this way rather than with CAIRN_HOME.
            var r = CairnHome.Resolve(null, CairnHome.DefaultRoot, _ => "/mnt/big/cairn");

            Assert.Equal("/mnt/big/cairn", r.Root);
            Assert.Equal(HomeSource.Pointer, r.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
        }
    }

    [Fact]
    public void Preflight_passes_when_the_pointer_target_is_there()
    {
        var r = new HomeResolution("/mnt/big/cairn", HomeSource.Pointer, null);

        Assert.Null(CairnHome.Preflight(r, _ => true));
    }

    [Fact]
    public void Preflight_refuses_a_pointer_at_a_directory_that_is_not_there()
    {
        // The unplugged-disk case. Falling back would start Cairn with an empty root, which
        // reads as total loss and invites re-downloading the game beside data that is fine.
        var r = new HomeResolution("/Volumes/Gone/cairn", HomeSource.Pointer, null);

        var problem = CairnHome.Preflight(r, _ => false);

        Assert.NotNull(problem);
        Assert.Contains("/Volumes/Gone/cairn", problem);
    }

    [Fact]
    public void Preflight_does_not_mind_the_default_being_absent()
    {
        // That is an ordinary first run.
        Assert.Null(CairnHome.Preflight(
            new HomeResolution(Default, HomeSource.Default, null), _ => false));
    }

    [Fact]
    public void Preflight_does_not_second_guess_the_environment()
    {
        // Somebody who set CAIRN_HOME is looking at what they set it to; a server unit that
        // names a directory systemd will create is the ordinary case, not a fault.
        Assert.Null(CairnHome.Preflight(
            new HomeResolution("/var/lib/cairn", HomeSource.Environment, null), _ => false));
    }

    [Fact]
    public void Preflight_reports_a_resolution_problem_ahead_of_anything_else()
    {
        var r = new HomeResolution(Default, HomeSource.Default, "the pointer file is empty");

        Assert.Equal("the pointer file is empty", CairnHome.Preflight(r, _ => true));
    }
}
