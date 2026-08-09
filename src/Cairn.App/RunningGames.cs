using System.Diagnostics;
using Avalonia.Threading;
using Cairn.Core.Launch;
using Cairn.Core.Packs;

namespace Cairn.App;

/// <summary>
/// Which packs are mid-launch or have a game running, keyed by pack id.
///
/// Owned by MainViewModel for the same reason each pack's log is: PackDetailViewModel is
/// rebuilt every time the selection changes, so a launch tracked on it was forgotten the
/// moment another pack was clicked. The pane came back saying nothing was running, Play was
/// enabled again, and pressing it started a second copy of the game on the same pack — two
/// processes writing one save.
///
/// It also owns what happens when the game exits, rather than leaving that on the view
/// model that started it: a pack whose game closes while you are looking at another pack
/// still has to have its session written back and its crash reported.
/// </summary>
public sealed class RunningGames(PackStore store, Action<string, string> log)
{
    private sealed class Run
    {
        public Process? Process;
        public string Stage = "";

        /// <summary>Set when the exit was asked for, so it is not reported as a crash.</summary>
        public bool Killed;
    }

    private readonly PackData _data = new(store);

    private readonly Dictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A non-zero exit waiting to be shown, for a pack that had no pane at the time. Held
    /// rather than dropped: the game closing on startup is the thing most worth saying, and
    /// it is no less true for having happened while you were looking elsewhere.
    /// </summary>
    private readonly Dictionary<string, string> _exits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread with the pack whose launch state moved.</summary>
    public event Action<string>? Changed;

    /// <summary>Play has been pressed and the game has not exited yet.</summary>
    public bool IsLaunching(string packId) => _runs.ContainsKey(packId);

    /// <summary>The game itself is up — as opposed to still syncing towards it.</summary>
    public bool IsRunning(string packId) =>
        _runs.TryGetValue(packId, out var run) && run.Process is not null;

    public string StageFor(string packId) =>
        _runs.TryGetValue(packId, out var run) ? run.Stage : "";

    /// <summary>Play was pressed; from here until exit the pack counts as launching.</summary>
    public void Begin(string packId, string stage)
    {
        _runs[packId] = new Run { Stage = stage };
        Changed?.Invoke(packId);
    }

    /// <summary>What the launch is doing, for the pane's progress line.</summary>
    public void Report(string packId, string stage)
    {
        if (!_runs.TryGetValue(packId, out var run)) return;

        run.Stage = stage;
        Changed?.Invoke(packId);
    }

    /// <summary>The launch gave up before starting anything, so Play comes back.</summary>
    public void Abandon(string packId)
    {
        if (_runs.Remove(packId)) Changed?.Invoke(packId);
    }

    /// <summary>
    /// The game is up. Watching it is this object's job from here — the pane that pressed
    /// Play may well be gone before it exits.
    /// </summary>
    public void Track(string packId, Process process)
    {
        var run = _runs.TryGetValue(packId, out var existing) ? existing : _runs[packId] = new Run();
        run.Process = process;
        run.Stage = $"Vintage Story is running (pid {process.Id})";
        Changed?.Invoke(packId);

        _ = WatchAsync(packId, process);
    }

    /// <summary>
    /// Kills the game a pack has running, and whatever it started with it.
    ///
    /// The way out of a hang. A game that has stopped drawing is still a running process
    /// holding the pack's save open, and without this the only answer was Activity Monitor
    /// or Task Manager — with the pack still saying "playing now" afterwards, because
    /// nothing here would have noticed.
    ///
    /// The exit is recorded as asked-for, so what follows reads as a quit rather than as
    /// the crash report a non-zero exit code would otherwise raise. Returns false if there
    /// was nothing to kill.
    /// </summary>
    public bool ForceQuit(string packId)
    {
        if (!_runs.TryGetValue(packId, out var run) || run.Process is not { } proc) return false;

        run.Killed = true;
        run.Stage = "Forcing Vintage Story to quit…";
        Changed?.Invoke(packId);

        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or SystemException)
        {
            // Already gone, or the OS refused part of the tree. Either way the watch is
            // still on it and will clear the pack when the process itself ends; saying so
            // is all there is to do, and it beats a dialog about a thing that may well
            // have just worked.
            log(packId, $"could not force Vintage Story to quit: {e.Message}");
        }

        return true;
    }

    /// <summary>
    /// The banner a pane should raise when it appears, if the pack's last game exited badly.
    /// Taken rather than read: it is news once.
    /// </summary>
    public string? TakeExitNotice(string packId)
    {
        if (!_exits.Remove(packId, out var notice)) return null;
        return notice;
    }

    private async Task WatchAsync(string packId, Process proc)
    {
        try
        {
            await proc.WaitForExitAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or SystemException)
        {
            // Process already gone; fall through and re-enable.
        }

        var code = TryExitCode(proc);

        // A login made inside the pack, or a session the game rotated while playing,
        // becomes the one every other pack uses next.
        _data.AfterExit(packId);

        Dispatcher.UIThread.Post(() =>
        {
            var killed = _runs.TryGetValue(packId, out var run) && run.Killed;
            _runs.Remove(packId);

            // A kill produces a non-zero exit code by definition, and reporting that as a
            // crash — banner, log dump and all — would be Cairn telling you something went
            // wrong with the thing you just asked it to do.
            if (killed)
            {
                log(packId, "Vintage Story was forced to quit");
            }
            else if (code is { } c && c != 0)
            {
                _exits[packId] = $"Vintage Story exited with code {c}. See the Log tab.";
                log(packId, $"Vintage Story exited with code {c}");

                // The moment the game's log matters, so it is put in front of you rather
                // than left somewhere you would have to know to look.
                ShowGameProblems(packId, $"exit code {c}");
            }
            else
            {
                log(packId, "Vintage Story exited");
            }

            Changed?.Invoke(packId);
        });
    }

    /// <summary>The errors and warnings only, which is what a failed launch is asked about.</summary>
    private void ShowGameProblems(string packId, string why)
    {
        var problems = new GameLogs(_data.DataPathFor(packId)).Problems();
        if (problems.Count == 0) return;

        log(packId, $"── {why}: what the game logged ──");
        foreach (var line in problems) log(packId, line);
        log(packId, "── use Game log for the full file ──");
    }

    private static int? TryExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }
}
