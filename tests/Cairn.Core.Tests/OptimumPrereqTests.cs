using Cairn.Core;
using Cairn.Core.Games.Optimum;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What Cairn tells somebody before it spends twenty minutes building a client for them.
///
/// The build shells out to tools Cairn cannot install, and it is long enough that finding
/// out about them one at a time is a real cost. These hold the two things that make the
/// difference between a useful message and a useless one: that the whole list arrives at
/// once, and that the list is the one this platform actually uses.
/// </summary>
public class OptimumPrereqTests
{
    /// <summary>A machine with exactly these commands on it.</summary>
    private static Func<string, bool> Machine(params string[] present) =>
        name => present.Contains(name, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_machine_with_everything_is_satisfied()
    {
        var report = OptimumPrereqs.Check(Machine([.. OptimumPrereqs.Required().Select(t => t.Name)]));

        Assert.True(report.Satisfied);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public void Every_missing_tool_is_named_at_once()
    {
        // A bare machine, which is the case that matters: reporting only the first would
        // send somebody round install-and-retry once per tool, against a build that takes
        // long enough for each retry to hurt.
        var report = OptimumPrereqs.Check(Machine());

        Assert.False(report.Satisfied);
        Assert.Equal(OptimumPrereqs.Required().Count, report.Missing.Count);

        foreach (var tool in OptimumPrereqs.Required())
            Assert.Contains(tool.Name, report.Describe());
    }

    [Fact]
    public void Every_tool_says_what_it_is_for_and_how_to_get_it()
    {
        // A tool name on its own is a puzzle, not a message. "python3" tells a player
        // nothing; the reason and the command are the whole value of the list.
        foreach (var tool in OptimumPrereqs.Required())
        {
            Assert.NotEmpty(tool.UsedFor);
            Assert.NotEmpty(tool.Hint);
        }
    }

    [Fact]
    public void Git_is_needed_on_every_platform()
    {
        // The one tool with no substitute: the build applies its patches with git apply,
        // and libgit2 — so any C# git binding — cannot apply a patch at all.
        Assert.Contains(OptimumPrereqs.Required(), t => t.Name == "git");
    }

    [Fact]
    public void The_dotnet_sdk_is_not_a_prerequisite()
    {
        // The build needs one, but Cairn fetches a private SDK the same way it already
        // fetches a private runtime. Listing it would tell somebody to go and install
        // something Cairn was about to install for them.
        Assert.DoesNotContain(OptimumPrereqs.Required(), t => t.Name.Contains("dotnet"));
    }

    [Fact]
    public void Pwsh_is_never_required()
    {
        // Optimum's own prerequisite check calls pwsh required, and it is wrong for this
        // caller: the only thing needing it is building the *Windows* package from a
        // non-Windows host. Cairn always builds for the machine it is on, and telling a
        // Linux user to install PowerShell for a Linux build would be a fiction.
        Assert.DoesNotContain(OptimumPrereqs.Required(), t => t.Name is "pwsh" or "powershell");
    }

    [Fact]
    public void Windows_is_not_told_to_install_the_bash_path_s_tools()
    {
        // bootstrap.ps1 implements every fixup natively, so perl and python3 are the bash
        // path's business alone. This is the assertion that stops the shorter list being
        // "simplified" back into one shared list later.
        if (!OperatingSystem.IsWindows()) return;

        Assert.DoesNotContain(OptimumPrereqs.Required(), t => t.Name is "perl" or "python3");
    }

    [Fact]
    public void The_bash_platforms_are_told_about_perl_and_python()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Contains(OptimumPrereqs.Required(), t => t.Name == "perl");
        Assert.Contains(OptimumPrereqs.Required(), t => t.Name == "python3");
    }

    [Fact]
    public void A_supported_platform_reports_no_reason_against_it()
    {
        // The suite runs on the three platforms Optimum builds on, so this is really
        // asserting that ordinary machines are not turned away.
        Assert.Null(OptimumPrereqs.UnsupportedReason());
    }
}

/// <summary>
/// Finding a command without starting one.
///
/// PATH is read rather than probed by running each tool, because on Windows a missing
/// executable and a broken one raise the same exception, and probing flashes a console
/// window per tool on exactly the machine that is missing several.
/// </summary>
public class ExecutableLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-path-" + Guid.NewGuid().ToString("n")[..8]);

    public ExecutableLookupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Plant(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>
    /// Same file, allowing for the case of the extension on Windows.
    ///
    /// PATHEXT is upper case by convention — ".COM;.EXE;.BAT;.CMD" — so a bare "faketool"
    /// is found as "faketool.EXE" while the file planted is "faketool.exe". They name the
    /// same file on a case-insensitive filesystem, and it is the file that is being
    /// asserted about. Exact on every other platform, where case means something.
    /// </summary>
    private static void SameFile(string expected, string? actual) =>
        Assert.Equal(expected, actual, ignoreCase: OperatingSystem.IsWindows());

    [Fact]
    public void A_command_on_the_search_path_is_found()
    {
        var planted = Plant(OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");

        SameFile(planted, ExecutableLookup.Find("faketool", _dir));
        Assert.True(ExecutableLookup.Exists("faketool", _dir));
    }

    [Fact]
    public void A_command_that_is_not_there_is_null_rather_than_a_throw()
    {
        Assert.Null(ExecutableLookup.Find("definitely-not-installed", _dir));
        Assert.False(ExecutableLookup.Exists("definitely-not-installed", _dir));
    }

    [Fact]
    public void Later_entries_are_searched_when_the_first_does_not_have_it()
    {
        var planted = Plant(OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");
        var search = string.Join(Path.PathSeparator, Path.Combine(_dir, "nope"), _dir);

        SameFile(planted, ExecutableLookup.Find("faketool", search));
    }

    [Fact]
    public void An_empty_or_missing_search_path_finds_nothing()
    {
        Assert.Null(ExecutableLookup.Find("faketool", ""));
        Assert.Null(ExecutableLookup.Find("", _dir));
    }

    [Fact]
    public void Windows_resolves_a_bare_name_through_pathext()
    {
        // git is on PATH as git.exe and never as "git". Checking the bare name alone would
        // report every tool missing on the one platform with the shortest list — so this is
        // the assertion standing between a Windows user and an impossible message.
        if (!OperatingSystem.IsWindows()) return;

        var planted = Plant("faketool.cmd");

        SameFile(planted, ExecutableLookup.Find("faketool", _dir));
    }

    [Fact]
    public void A_name_carrying_a_directory_is_treated_as_a_path()
    {
        var planted = Plant("faketool");

        // Not a command lookup at all: PATH has no say in a name that already points
        // somewhere, and searching for one would find an unrelated tool of the same name.
        Assert.Equal(planted, ExecutableLookup.Find(planted, _dir));
        Assert.Null(ExecutableLookup.Find(Path.Combine(_dir, "absent"), _dir));
    }
}
