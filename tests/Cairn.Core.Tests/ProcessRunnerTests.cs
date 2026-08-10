using Cairn.Core.Games.Optimum;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Running a build tool and watching it work.
///
/// These start real processes, because what is being tested is precisely the behaviour that
/// cannot be faked: that output arrives while the command is still running rather than at
/// the end, that a failure carries the reason with it, and that cancelling actually stops
/// something. A stubbed process would prove none of it.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly string Dir = Path.GetTempPath();

    /// <summary>The platform's way to run a one-line shell command.</summary>
    private static (string File, string[] Args) Shell(string command) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", command])
            : ("/bin/sh", ["-c", command]);

    [Fact]
    public async Task Output_is_reported_line_by_line()
    {
        var lines = new List<string>();
        var (file, args) = Shell("echo one && echo two");

        var result = await ProcessRunner.RunAsync(file, args, Dir,
            new Reports<string>(l => { lock (lines) lines.Add(l); }));

        Assert.True(result.Succeeded);

        // No sleep waiting for callbacks to land. Reports<T> runs where it is called, so by
        // the time the run has been awaited every line it produced has been added — a sleep
        // is the same race with a longer fuse, and it costs the suite a fifth of a second
        // for every test that copies it. The lock stays: the reader is a background thread.
        lock (lines)
        {
            Assert.Contains(lines, l => l.Contains("one"));
            Assert.Contains(lines, l => l.Contains("two"));
        }
    }

    [Fact]
    public async Task The_command_itself_is_logged_before_it_runs()
    {
        var lines = new List<string>();
        var (file, args) = Shell("echo hello");

        await ProcessRunner.RunAsync(file, args, Dir,
            new Reports<string>(l => { lock (lines) lines.Add(l); }));

        // A log that shows output but not what produced it is unreadable across the five
        // commands a build runs.
        lock (lines) Assert.Contains(lines, l => l.StartsWith("$ "));
    }

    [Fact]
    public async Task A_failure_carries_its_exit_code_and_the_end_of_its_output()
    {
        var (file, args) = Shell("echo something broke && exit 3");

        var result = await ProcessRunner.RunAsync(file, args, Dir);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);

        // The exit code alone is useless — these tools exit 1 for everything — so the
        // reason has to travel with it.
        Assert.Contains(result.Tail, l => l.Contains("something broke"));
        Assert.Contains("something broke", result.Describe());
    }

    [Fact]
    public async Task Standard_error_is_captured_too()
    {
        var (file, args) = Shell("echo to stderr 1>&2 && exit 1");

        var result = await ProcessRunner.RunAsync(file, args, Dir);

        // Every one of these tools reports its actual problem on stderr.
        Assert.Contains(result.Tail, l => l.Contains("to stderr"));
    }

    [Fact]
    public async Task Only_the_tail_of_a_long_run_is_kept()
    {
        // A decompile emits tens of thousands of lines; holding them all to describe a
        // failure would cost more memory than the failure is worth.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "for /l %i in (1,1,200) do @echo line %i" })
            : ("/bin/sh", ["-c", "i=1; while [ $i -le 200 ]; do echo line $i; i=$((i+1)); done"]);

        var result = await ProcessRunner.RunAsync(file, args, Dir);

        Assert.True(result.Succeeded);
        Assert.Equal(ProcessRunner.TailLines, result.Tail.Count);
        Assert.Contains(result.Tail, l => l.Contains("200"));
    }

    [Fact]
    public async Task RunOrThrow_throws_with_the_output_in_the_message()
    {
        var (file, args) = Shell("echo the actual reason && exit 1");

        var e = await Assert.ThrowsAsync<OptimumBuildException>(
            () => ProcessRunner.RunOrThrowAsync(file, args, Dir));

        Assert.Contains("the actual reason", e.Message);
    }

    [Fact]
    public async Task A_command_that_is_not_installed_says_so_rather_than_crashing()
    {
        // The prerequisite check should have caught this, but a tool can go missing between
        // the check and the build, and a raw Win32Exception is not a message.
        var e = await Assert.ThrowsAsync<OptimumBuildException>(
            () => ProcessRunner.RunAsync("cairn-no-such-tool-exists", [], Dir));

        Assert.Contains("cairn-no-such-tool-exists", e.Message);
    }

    [Fact]
    public async Task Cancelling_stops_the_command()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", ["-c", "sleep 30"]);

        using var cts = new CancellationTokenSource();
        var run = ProcessRunner.RunAsync(file, args, Dir, ct: cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Cancelling_returns_promptly_rather_than_waiting_out_the_command()
    {
        // The build is a twenty-minute job somebody must be able to abandon. A cancel that
        // waits for the current step is indistinguishable from one that did nothing.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 60 127.0.0.1 > nul" })
            : ("/bin/sh", ["-c", "sleep 60"]);

        using var cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var run = ProcessRunner.RunAsync(file, args, Dir, ct: cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15),
            "cancellation should not wait for the command to finish");
    }

    [Fact]
    public void The_script_host_bypasses_windows_execution_policy()
    {
        var (file, prefix) = ProcessRunner.ScriptHost("/tmp/script");

        if (OperatingSystem.IsWindows())
        {
            // A default Windows install refuses to run unsigned .ps1 files at all, so
            // without this the build fails instantly on a machine behaving normally.
            Assert.Contains("powershell", file, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Bypass", prefix);
            Assert.Contains("-NonInteractive", prefix);
        }
        else
        {
            Assert.Equal("/bin/bash", file);
            Assert.Contains("/tmp/script", prefix);
        }
    }
}
