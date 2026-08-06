using System.Runtime.InteropServices;

namespace Cairn.Core;

/// <summary>
/// Finds a command on PATH, the way a shell would.
///
/// Exists because "is git installed?" cannot be answered by running <c>git --version</c> and
/// catching: on Windows a missing executable and a broken one both surface as
/// Win32Exception, and starting a process to ask a question costs a visible console flash
/// per tool on a machine that is missing several. Reading PATH answers it without launching
/// anything.
/// </summary>
public static class ExecutableLookup
{
    /// <summary>The full path to <paramref name="name"/>, or null if PATH has no such command.</summary>
    /// <param name="name">A bare command name, e.g. "git".</param>
    /// <param name="searchPath">
    /// Overrides PATH. Passed by tests, which need to describe a machine they are not
    /// running on — and cannot do it by setting the environment variable, because these
    /// suites run in parallel and would be editing each other's process.
    /// </param>
    public static string? Find(string name, string? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // A name carrying a directory is a path, not a command, and PATH has no say in it.
        if (name != Path.GetFileName(name)) return File.Exists(name) ? name : null;

        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            foreach (var candidate in Candidates(name))
            {
                string full;
                try
                {
                    full = Path.Combine(dir.Trim('"'), candidate);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with invalid characters in it — real, and not worth
                    // failing a whole lookup over.
                    break;
                }

                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    public static bool Exists(string name, string? searchPath = null) =>
        Find(name, searchPath) is not null;

    /// <summary>
    /// The filenames to try for one command.
    ///
    /// Windows resolves <c>git</c> to <c>git.exe</c> via PATHEXT, and nothing on that
    /// platform is on PATH under its bare name — so checking only the bare name reports
    /// every tool missing on the one platform with the shortest prerequisite list.
    /// </summary>
    private static IEnumerable<string> Candidates(string name)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return name;
            yield break;
        }

        // An explicit extension is already a filename.
        if (Path.HasExtension(name)) { yield return name; yield break; }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT")
                      ?? ".COM;.EXE;.BAT;.CMD";

        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return name + ext.Trim();
    }
}
