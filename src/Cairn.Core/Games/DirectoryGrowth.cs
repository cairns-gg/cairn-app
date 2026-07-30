namespace Cairn.Core.Games;

/// <summary>
/// Reports how much has landed on disk while a step runs that cannot report anything for
/// itself — unpacking a tarball, and running the Windows installer, which takes minutes
/// under /VERYSILENT and says nothing at all.
///
/// Total is deliberately left at zero, so <see cref="InstallProgress.Fraction"/> stays
/// null and anything bound to it stays indeterminate. The expanded size is not known ahead
/// of time, and a number pretending to be a percentage is worse than an honest animation
/// beside an honest byte count.
/// </summary>
public static class DirectoryGrowth
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Polls until cancelled. Returns rather than throwing when cancelled, so callers can
    /// stop it in a finally block without guarding every one.
    /// </summary>
    public static async Task ReportAsync(
        string directory,
        string label,
        IProgress<InstallProgress>? progress,
        CancellationToken ct,
        TimeSpan? interval = null)
    {
        if (progress is null) return;

        try
        {
            while (true)
            {
                await Task.Delay(interval ?? DefaultInterval, ct).ConfigureAwait(false);

                var written = Measure(directory);
                progress.Report(new InstallProgress(
                    InstallPhase.Extracting, written, 0,
                    written > 0 ? $"{label} — {written / 1024 / 1024} MB written" : label));
            }
        }
        catch (OperationCanceledException)
        {
            // The step finished; nothing more to say.
        }
    }

    /// <summary>
    /// Bytes currently under <paramref name="directory"/>, or 0 if it cannot be read —
    /// this is a progress hint, so it must never be the thing that fails an install.
    /// </summary>
    public static long Measure(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                // A file being written can vanish or lock between enumerating and measuring.
                .Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
