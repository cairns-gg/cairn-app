using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Cairn's log on disk — what survives a restart when the Log tab does not
/// (cairns-gg/cairn-app#8).
/// </summary>
[Collection(HomeEnvironment.Collection)]
public class CairnLogTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("cairn-log-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public CairnLogTests() => Environment.SetEnvironmentVariable("CAIRN_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public void A_line_is_on_disk_under_the_root()
    {
        CairnLog.Write("checksum refused", "seraph");

        Assert.Equal(Path.Combine(_home, "logs", "cairn.log"), CairnPaths.LogPath);
        Assert.Contains("[seraph] checksum refused", File.ReadAllText(CairnPaths.LogPath));
    }

    [Fact]
    public void A_packs_tail_leaves_out_other_packs_but_keeps_what_belongs_to_none()
    {
        CairnLog.Write("started");
        CairnLog.Write("synced", "one");
        CairnLog.Write("refused a mod", "two");
        CairnLog.Write("installed game 1.22.7");

        var tail = CairnLog.Tail(10, "one");

        Assert.Equal(3, tail.Count);
        Assert.EndsWith("- started", tail[0]);
        Assert.EndsWith("[one] synced", tail[1]);
        Assert.EndsWith("- installed game 1.22.7", tail[2]);
    }

    [Fact]
    public void A_tail_is_the_newest_entries_oldest_first()
    {
        for (var i = 0; i < 10; i++) CairnLog.Write($"line {i}");

        var tail = CairnLog.Tail(3);

        Assert.Equal(["line 7", "line 8", "line 9"], tail.Select(l => l.Split(' ', 4)[3]));
    }

    [Fact]
    public void A_stack_trace_stays_with_the_entry_that_introduced_it()
    {
        Exception thrown;
        try { throw new IOException("disk said no"); }
        catch (IOException e) { thrown = e; }

        CairnLog.Error("sync", thrown, "one");
        CairnLog.Write("after", "one");

        var tail = CairnLog.Tail(10, "one");

        // Two entries, not one per line of the trace: a tail cut at an entry count would
        // otherwise drop the line saying what failed and keep the frames under it.
        Assert.Equal(2, tail.Count);
        Assert.Contains("sync: System.IO.IOException: disk said no", tail[0]);
        Assert.Contains("\n    ", tail[0]);
        Assert.Contains(nameof(A_stack_trace_stays_with_the_entry_that_introduced_it), tail[0]);
    }

    [Fact]
    public void A_full_file_is_set_aside_and_one_older_kept()
    {
        CairnLog.Write(new string('x', (int)CairnLog.MaxBytes));
        CairnLog.Write("after the rotation");

        var previous = CairnPaths.LogPath + ".1";

        Assert.True(File.Exists(previous));
        Assert.True(new FileInfo(CairnPaths.LogPath).Length < 1024);
        Assert.Contains("after the rotation", File.ReadAllText(CairnPaths.LogPath));
    }

    [Fact]
    public void Nothing_written_yet_is_an_empty_tail_not_a_failure() =>
        Assert.Empty(CairnLog.Tail(10, "one"));

    [Fact]
    public void A_pointer_to_a_missing_root_is_not_created_to_hold_a_log()
    {
        // An unplugged disk. Making the directory to write one line into would put a stray
        // tree at the mount point on the boot disk — what CairnHome's preflight refuses.
        Environment.SetEnvironmentVariable("CAIRN_HOME", null);

        var previousDefault = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        var missing = Path.Combine(_home, "unplugged", "cairn");

        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", _home);
        File.WriteAllText(Path.Combine(_home, CairnHome.PointerName), missing);

        try
        {
            Assert.Equal(HomeSource.Pointer, CairnHome.Resolve().Source);

            CairnLog.Write("into the void");

            Assert.False(Directory.Exists(Path.Combine(_home, "unplugged")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previousDefault);
        }
    }
}
