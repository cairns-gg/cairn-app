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
[Collection(HomeEnvironment.Collection)]
public class CairnHomeTests
{
    /// <summary>
    /// A fully-qualified path for the platform this is running on.
    ///
    /// Not decoration. <see cref="CairnHome.ResolvePointer"/> refuses a pointer that is not
    /// fully qualified, and Elsewhere is not one on Windows — it is rooted but has no
    /// drive, so Path.IsPathFullyQualified says false and the pointer is correctly ignored.
    /// Written the Unix way only, these tests asserted the rule on two platforms and
    /// asserted its refusal branch on the third while claiming to test the rule.
    /// </summary>
    private static string Abs(params string[] parts) =>
        (OperatingSystem.IsWindows() ? "C:" : "")
        + Path.DirectorySeparatorChar
        + string.Join(Path.DirectorySeparatorChar, parts);

    private static readonly string Default = Abs("home", "someone", ".cairn");
    private static readonly string Elsewhere = Abs("mnt", "big", "cairn");
    private static readonly string Managed = Abs("var", "lib", "cairn");
    private static readonly string Sandbox = Abs("tmp", "sandbox", ".cairn");
    private static readonly string Missing = Abs("Volumes", "Gone", "cairn");

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
        var r = CairnHome.Resolve(Managed, Default, Pointing(Elsewhere));

        Assert.Equal(Managed, r.Root);
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
        var r = CairnHome.Resolve(null, Default, Pointing(Elsewhere));

        Assert.Equal(Elsewhere, r.Root);
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

    [Fact]
    public void Whitespace_around_the_path_is_ignored()
    {
        // Every editor adds a trailing newline, and SetPointer writes one deliberately.
        //
        // A Fact over a loop rather than a Theory, because the path has to be built for the
        // platform and InlineData takes compile-time constants only.
        foreach (var written in new[] { Elsewhere + "\n", Elsewhere + "\r\n", "  " + Elsewhere + "  " })
            Assert.Equal(Elsewhere, CairnHome.Resolve(null, Default, Pointing(written)).Root);
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
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", Sandbox);

        try
        {
            Assert.Equal(Sandbox, CairnHome.DefaultRoot);

            // And it is a default, not an override: the pointer still outranks it, which is
            // the whole point of sandboxing this way rather than with CAIRN_HOME.
            var r = CairnHome.Resolve(null, CairnHome.DefaultRoot, _ => Elsewhere);

            Assert.Equal(Elsewhere, r.Root);
            Assert.Equal(HomeSource.Pointer, r.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
        }
    }

    [Fact]
    public void Pointing_at_the_default_removes_the_pointer_rather_than_writing_one()
    {
        // Sandboxed, because SetPointer writes to the running user's own default root.
        var previous = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        var sandbox = Directory.CreateTempSubdirectory("cairn-pointer-").FullName;
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", sandbox);

        try
        {
            CairnHome.SetPointer(Elsewhere);

            Assert.True(File.Exists(CairnHome.PointerPath));
            Assert.Equal(HomeSource.Pointer, CairnHome.Resolve().Source);

            // Naming the default is the same as naming nothing, and saying it the long way
            // would leave the directory holding a note about itself — so somebody who moved
            // home again reads "the default" rather than "the pointer file".
            CairnHome.SetPointer(sandbox);

            Assert.False(File.Exists(CairnHome.PointerPath));
            Assert.Equal(HomeSource.Default, CairnHome.Resolve().Source);
            Assert.Equal(sandbox, CairnHome.Resolve().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void Preflight_passes_when_the_pointer_target_is_there()
    {
        var r = new HomeResolution(Elsewhere, HomeSource.Pointer, null);

        Assert.Null(CairnHome.Preflight(r, _ => true));
    }

    [Fact]
    public void Preflight_refuses_a_pointer_at_a_directory_that_is_not_there()
    {
        // The unplugged-disk case. Falling back would start Cairn with an empty root, which
        // reads as total loss and invites re-downloading the game beside data that is fine.
        var r = new HomeResolution(Missing, HomeSource.Pointer, null);

        var problem = CairnHome.Preflight(r, _ => false);

        Assert.NotNull(problem);
        Assert.Contains(Missing, problem);
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
            new HomeResolution(Managed, HomeSource.Environment, null), _ => false));
    }

    [Fact]
    public void Preflight_reports_a_resolution_problem_ahead_of_anything_else()
    {
        var r = new HomeResolution(Default, HomeSource.Default, "the pointer file is empty");

        Assert.Equal("the pointer file is empty", CairnHome.Preflight(r, _ => true));
    }
}
