using Cairn.Core.Games;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The sign of life shown while a step that reports nothing is running. On Windows that
/// step is the game installer, which sat there for minutes looking hung.
/// </summary>
public class DirectoryGrowthTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cairn-growth-" + Guid.NewGuid().ToString("n")[..8]);

    public DirectoryGrowthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Collect : IProgress<InstallProgress>
    {
        public List<InstallProgress> Reports { get; } = [];
        public void Report(InstallProgress value)
        {
            lock (Reports) Reports.Add(value);
        }
    }

    private void Write(string name, int megabytes) =>
        File.WriteAllBytes(Path.Combine(_dir, name), new byte[megabytes * 1024 * 1024]);

    [Fact]
    public void Measure_counts_the_whole_tree_including_subdirectories()
    {
        Write("a.bin", 2);
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        File.WriteAllBytes(Path.Combine(_dir, "nested", "b.bin"), new byte[1024 * 1024]);

        Assert.Equal(3L * 1024 * 1024, DirectoryGrowth.Measure(_dir));
    }

    [Fact]
    public void Measure_of_a_directory_that_does_not_exist_is_zero_not_an_exception()
    {
        // The installer's target is created before it runs, but a progress hint must never
        // be what fails an install.
        Assert.Equal(0, DirectoryGrowth.Measure(Path.Combine(_dir, "absent")));
    }

    [Fact]
    public async Task It_reports_the_tree_growing_until_it_is_stopped()
    {
        var collected = new Collect();
        using var cts = new CancellationTokenSource();

        var reporting = DirectoryGrowth.ReportAsync(
            _dir, "installing Vintage Story 1.22.5", collected, cts.Token,
            interval: TimeSpan.FromMilliseconds(50));

        Write("first.bin", 1);
        await Task.Delay(200);
        Write("second.bin", 2);
        await Task.Delay(200);

        await cts.CancelAsync();
        await reporting;   // cancellation is swallowed, so this must not throw

        Assert.NotEmpty(collected.Reports);

        // Never a percentage: the expanded size is not known, so the bar must stay
        // indeterminate rather than sit at a made-up number.
        Assert.All(collected.Reports, r => Assert.Null(r.Fraction));

        // And it did observe the tree getting bigger.
        Assert.Equal(3L * 1024 * 1024, collected.Reports[^1].Done);
        Assert.Contains("3 MB written", collected.Reports[^1].Detail);
    }

    [Fact]
    public async Task An_empty_directory_reports_the_label_alone_rather_than_zero_MB()
    {
        var collected = new Collect();
        using var cts = new CancellationTokenSource();

        var reporting = DirectoryGrowth.ReportAsync(
            _dir, "running the installer", collected, cts.Token,
            interval: TimeSpan.FromMilliseconds(50));

        await Task.Delay(150);
        await cts.CancelAsync();
        await reporting;

        Assert.All(collected.Reports, r => Assert.Equal("running the installer", r.Detail));
    }

    [Fact]
    public async Task Without_a_progress_sink_it_does_no_work_at_all()
    {
        // Returns immediately rather than polling a directory nobody is watching.
        await DirectoryGrowth.ReportAsync(_dir, "x", progress: null, CancellationToken.None);
    }
}
