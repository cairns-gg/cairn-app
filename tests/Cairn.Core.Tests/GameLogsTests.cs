using Cairn.Core.Launch;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Reading Vintage Story's own logs. Cairn's log says what Cairn did, which is no help
/// when the game closes on startup.
/// </summary>
public class GameLogsTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "cairn-gamelogs-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly GameLogs _logs;

    public GameLogsTests() => _logs = new GameLogs(_data);

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private void Write(string file, params string[] lines)
    {
        Directory.CreateDirectory(_logs.Directory);
        File.WriteAllLines(_logs.PathTo(file), lines);
    }

    /// <summary>The game's actual format: "29.7.2026 20:03:04 [Error] …".</summary>
    private static string Line(string level, string message, string at = "29.7.2026 20:03:04")
        => $"{at} [{level}] {message}";

    [Fact]
    public void A_pack_that_has_never_launched_has_no_logs()
    {
        Assert.False(_logs.Exists);
        Assert.Empty(_logs.Files());
        Assert.Empty(_logs.Tail());
        Assert.Empty(_logs.Problems());
    }

    [Fact]
    public void The_tail_is_the_end_of_the_file_not_the_start()
    {
        Write(GameLogs.ClientMain, [.. Enumerable.Range(1, 500).Select(i => $"line {i}")]);

        var tail = _logs.Tail(lines: 10);

        Assert.Equal(10, tail.Count);
        Assert.Equal("line 491", tail[0]);
        Assert.Equal("line 500", tail[^1]);
    }

    [Fact]
    public void A_log_shorter_than_the_tail_is_returned_whole()
    {
        Write(GameLogs.ClientMain, "one", "two");

        Assert.Equal(["one", "two"], _logs.Tail(lines: 200));
    }

    [Fact]
    public void A_log_the_game_still_holds_open_can_be_read()
    {
        // The interesting case is a game that is still up, or that has just fallen over
        // with the file still locked. A plain File.ReadAllLines throws here.
        Write(GameLogs.ClientMain, Line("Error", "something went wrong"));

        using var held = new FileStream(
            _logs.PathTo(GameLogs.ClientMain), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Single(_logs.Tail());
        Assert.Single(_logs.Problems());
    }

    [Fact]
    public void Only_the_lines_the_game_flagged_count_as_problems()
    {
        Write(GameLogs.ClientMain,
            Line("Notification", "Loading mods"),
            Line("Error", "Failed to load mod olla"),
            Line("Debug", "the word error appears in this message"),
            Line("Warning", "Mod glassview is for an older version"));

        var problems = _logs.Problems();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("Failed to load mod olla"));
        Assert.Contains(problems, p => p.Contains("older version"));

        // "error" inside a Debug message is not a problem: the level is matched with its
        // brackets, because mod names and messages contain the word too.
        Assert.DoesNotContain(problems, p => p.Contains("the word error appears"));
    }

    [Fact]
    public void A_line_repeated_hundreds_of_times_is_collapsed()
    {
        // A failing render call logs the same line dozens of times a second. Left alone it
        // pushes the actual cause out of view, which is the one thing being looked for.
        var spam = Enumerable.Range(1, 200)
            .Select(i => Line("Error", "OpenGL threw an error: InvalidOperation", $"29.7.2026 20:03:{i % 60:00}"));

        Write(GameLogs.ClientMain, [Line("Error", "Failed to load mod olla"), .. spam]);

        var problems = _logs.Problems();

        Assert.Contains(problems, p => p.Contains("Failed to load mod olla"));
        Assert.Contains(problems, p => p.Contains("199 more like the previous line"));
        Assert.True(problems.Count < 10, $"expected the spam collapsed, got {problems.Count} lines");
    }

    [Fact]
    public void Both_sides_of_a_singleplayer_failure_are_reported()
    {
        // Singleplayer runs an internal server, and its side of a failure lands elsewhere.
        Write(GameLogs.ClientMain, Line("Error", "client could not connect"));
        Write(GameLogs.ServerMain, Line("Fatal", "server failed to start"));

        var problems = _logs.Problems();

        Assert.Contains(problems, p => p.StartsWith("[client]") && p.Contains("could not connect"));
        Assert.Contains(problems, p => p.StartsWith("[server]") && p.Contains("failed to start"));
    }

    [Fact]
    public void Problems_are_capped_at_the_most_recent()
    {
        Write(GameLogs.ClientMain,
            [.. Enumerable.Range(1, 100).Select(i => Line("Error", $"distinct failure {i}"))]);

        var problems = _logs.Problems(max: 5);

        Assert.Equal(5, problems.Count);

        // The last failure before it stopped is the one that matters.
        Assert.Contains("distinct failure 100", problems[^1]);
    }

    [Fact]
    public void It_copes_with_a_real_Vintage_Story_log()
    {
        // Synthetic lines prove the parsing matches what this test wrote. A real log is
        // the only thing that proves it matches what the game writes.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vintagestorydev", "Logs", GameLogs.ClientMain);

        if (!File.Exists(real)) return;   // not this machine; the rest of the file still applies

        Directory.CreateDirectory(_logs.Directory);
        File.Copy(real, _logs.PathTo(GameLogs.ClientMain), overwrite: true);

        Assert.NotEmpty(_logs.Tail());

        // Every problem line keeps its level marker, and none is a bare timestamp.
        foreach (var line in _logs.Problems())
            Assert.True(
                line.Contains("[Error]") || line.Contains("[Fatal]") || line.Contains("[Warning]")
                || line.Contains("more like the previous line"),
                $"not a problem line: {line}");
    }

    [Fact]
    public void Log_files_are_listed_newest_first()
    {
        Write("old.log", "x");
        File.SetLastWriteTimeUtc(_logs.PathTo("old.log"), DateTime.UtcNow.AddHours(-2));
        Write(GameLogs.ClientMain, "y");

        Assert.Equal(GameLogs.ClientMain, _logs.Files()[0]);
    }
}
