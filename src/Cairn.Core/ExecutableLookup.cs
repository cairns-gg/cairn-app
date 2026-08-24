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
    /// The full path to a Windows system tool, for launching one without a search.
    ///
    /// <c>CreateProcess</c> resolves a bare program name by searching, and the second place
    /// it looks is the current directory of the *calling* process — ahead of the system
    /// directory. Cairn does not choose its own working directory: for a <c>cairn://</c>
    /// launch it inherits whatever the shell handed it, which can be a directory somebody
    /// else can write to. A file called <c>reg.exe</c> sitting there would be found before
    /// the real one, and Cairn runs <c>reg</c> while registering that very protocol handler
    /// and again on each Windows game install.
    ///
    /// Setting <see cref="ProcessStartInfo.WorkingDirectory"/> does not help, which is worth
    /// saying because it looks like it should: that sets the working directory of the child,
    /// while the search uses the parent's.
    ///
    /// PATH is not the answer either — <see cref="Find"/> would consult it, and PATH can
    /// itself carry a directory somebody can write to. A system tool has a known home, so
    /// naming it outright removes the search rather than reordering it.
    /// </summary>
    /// <param name="name">A filename with its extension, e.g. "reg.exe".</param>
    public static string SystemTool(string name)
    {
        if (!OperatingSystem.IsWindows()) return name;

        // Empty on a platform without one, and nothing is gained by building a path from
        // that — the bare name is no worse than what this replaces.
        var system = Environment.SystemDirectory;

        return string.IsNullOrWhiteSpace(system) ? name : Path.Combine(system, name);
    }

    /// <summary>
    /// The filenames to try for one command.
    ///
    /// Windows resolves <c>git</c> to <c>git.exe</c> via PATHEXT, and nothing on that
    /// platform is on PATH under its bare name — so checking only the bare name reports
    /// every tool missing on the one platform with the shortest prerequisite list.
    /// </summary>
    private static IEnumerable<string> Candidates(string name) =>
        Candidates(name, Host.This, Environment.GetEnvironmentVariable("PATHEXT"));

    /// <param name="os">Taken rather than asked, so the Windows list is checkable anywhere.</param>
    /// <param name="pathext">
    /// PATHEXT's value, or null for the default. Passed in for the same reason as the
    /// platform: a machine that is not Windows has none, and the default is the part most
    /// worth pinning — it decides whether "git" is ever looked for as "git.exe", and
    /// getting it wrong reports every prerequisite missing on the one platform whose
    /// prerequisite list is shortest.
    /// </param>
    public static IEnumerable<string> Candidates(string name, HostOs os, string? pathext)
    {
        if (os != HostOs.Windows)
        {
            yield return name;
            yield break;
        }

        // An explicit extension is already a filename.
        if (Path.HasExtension(name)) { yield return name; yield break; }

        foreach (var ext in (pathext ?? ".COM;.EXE;.BAT;.CMD")
                 .Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return name + ext.Trim();
    }
}
