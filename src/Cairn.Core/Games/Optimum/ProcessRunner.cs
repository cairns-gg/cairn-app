using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cairn.Core.Games.Optimum;

/// <summary>A command that was run, and how it went.</summary>
public sealed record ProcessResult(string Command, int ExitCode, IReadOnlyList<string> Tail)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// A failure message with the end of the output in it.
    ///
    /// The exit code alone is useless here — every one of these tools exits 1 for
    /// everything — and the reason is nearly always in the last few lines.
    /// </summary>
    public string Describe() => Succeeded
        ? $"{Command} succeeded."
        : $"{Command} failed (exit {ExitCode})."
          + (Tail.Count == 0 ? "" : "\n" + string.Join("\n", Tail));
}

/// <summary>
/// Runs a build tool, streaming its output a line at a time.
///
/// Streaming rather than collecting is the whole point: these commands run for minutes
/// with long silences, and a caller that waits for completion to show anything is
/// indistinguishable from one that has hung. The same lines go to a log file, so a build
/// that failed after the window was closed is still diagnosable.
///
/// Cancellation kills the whole process tree. A bootstrap spawns git, ilspycmd and dotnet,
/// and killing only the shell leaves a decompiler running for another ten minutes against
/// a directory Cairn is about to delete.
/// </summary>
public static class ProcessRunner
{
    /// <summary>How many trailing lines a failure carries for its message.</summary>
    public const int TailLines = 15;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IProgress<string>? log = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        var display = Describe(fileName, arguments);
        log?.Report($"$ {display}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Bounded: a decompile emits tens of thousands of lines and the only ones anybody
        // needs on failure are the last few.
        var tail = new Queue<string>(TailLines);
        var done = new TaskCompletionSource();
        var streamsClosed = 0;

        void OnLine(string? line)
        {
            if (line is null)
            {
                // Both streams signal end-of-stream with null; the process is only really
                // finished reporting once each has.
                if (Interlocked.Increment(ref streamsClosed) == 2) done.TrySetResult();
                return;
            }

            lock (tail)
            {
                if (tail.Count == TailLines) tail.Dequeue();
                tail.Enqueue(line);
            }

            log?.Report(line);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new OptimumBuildException($"Could not run {fileName}: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // WaitForExitAsync returns once the process is gone, which can be before the
            // last buffered lines have been handed over. Without this the tail of a failure
            // — the part that says why — is routinely missing.
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Output drained slowly or not at all; the exit code still stands.
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ProcessResult(display, process.ExitCode, [.. tail]);
    }

    /// <summary>
    /// Runs a command and throws unless it succeeded.
    /// </summary>
    public static async Task<ProcessResult> RunOrThrowAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IProgress<string>? log = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(fileName, arguments, workingDirectory, log, environment, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded) throw new OptimumBuildException(result.Describe());

        return result;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill. Either way there is nothing to do and
            // failing here would replace the real error with a worse one.
        }
    }

    /// <summary>The command as somebody would type it, for the log.</summary>
    private static string Describe(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { Path.GetFileName(fileName) }
            .Concat(arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

    /// <summary>
    /// How to invoke a script on this platform.
    ///
    /// Windows runs Optimum's PowerShell bootstrap through the Windows-shipped
    /// <c>powershell.exe</c>, with execution policy bypassed for this one invocation. That
    /// is not a shortcut: a default Windows install refuses to run unsigned .ps1 files at
    /// all, so without it the build fails immediately on a machine that is behaving
    /// normally. It is scoped to the process rather than changed on the machine.
    /// </summary>
    public static (string FileName, List<string> Prefix) ScriptHost(string scriptPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);

        return ("/bin/bash", [scriptPath]);
    }
}
