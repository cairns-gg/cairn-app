using System.Runtime.InteropServices;

namespace Cairn.Core.Games.Optimum;

/// <summary>A command the build shells out to, and what to tell somebody who lacks it.</summary>
/// <param name="Name">The command as it appears on PATH.</param>
/// <param name="UsedFor">Why the build needs it, in words a player can act on.</param>
/// <param name="Hint">How to get it on this platform.</param>
public sealed record BuildTool(string Name, string UsedFor, string Hint);

/// <summary>What the machine is missing before Optimum can be built on it.</summary>
public sealed record PrereqReport(IReadOnlyList<BuildTool> Missing, string? Unsupported = null)
{
    public bool Satisfied => Unsupported is null && Missing.Count == 0;

    /// <summary>
    /// The whole problem in one message, listing every missing tool at once.
    ///
    /// All of them, not the first: a machine with none of these would otherwise send
    /// somebody round the loop of install-one-thing-and-retry five times, and the build
    /// takes long enough that each retry is a real cost.
    /// </summary>
    public string Describe()
    {
        if (Unsupported is not null) return Unsupported;
        if (Missing.Count == 0) return Lang.Get("optimum-prereqs-ok");

        var lines = Missing.Select(t => $"  {t.Name} — {t.UsedFor}\n      {t.Hint}");

        return Lang.Plural("optimum-prereqs-missing", Missing.Count, Missing.Count)
               + "\n" + string.Join("\n", lines);
    }
}

/// <summary>
/// The tools Optimum's own build scripts require, per platform.
///
/// Only what Cairn <em>cannot</em> supply. The .NET SDK is deliberately absent from this
/// list even though the build needs one, because Cairn can fetch a private SDK the same way
/// it already fetches a private runtime — so an SDK is a step in the build, not a
/// prerequisite to report. Everything here is something a person has to go and install, and
/// the point of the list is to say so once rather than fail five times.
///
/// The split by platform is not cosmetic. Optimum ships two bootstraps, and
/// <c>bootstrap.ps1</c> implements every fixup natively in PowerShell — so Windows needs
/// neither perl nor python3, which the bash path uses throughout for text rewriting and for
/// two dedicated fixup scripts. Assuming the bash path's list everywhere would tell a
/// Windows user to go and install two things that build would never touch.
///
/// pwsh is on neither list. Optimum's own prerequisite check calls it required, but that
/// check covers packaging for <em>every</em> platform from one machine; the only thing that
/// needs pwsh is building the Windows package from a non-Windows host, and Cairn always
/// builds for the machine it is running on.
/// </summary>
public static class OptimumPrereqs
{
    /// <summary>
    /// Whether this platform has a build path at all.
    ///
    /// Optimum's scripts cover win-x64, linux-x64 and osx. There is no arm64 Windows path
    /// and no 32-bit anything, and a build that runs for twenty minutes before discovering
    /// that is worse than a sentence up front.
    /// </summary>
    public static string? UnsupportedReason()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (arch is not (Architecture.X64 or Architecture.Arm64))
            return Lang.Get("optimum-no-build-for", arch);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && arch is Architecture.Arm64)
            return Lang.Get("optimum-no-windows-arm64");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Lang.Get("optimum-platforms");

        return null;
    }

    /// <summary>The commands this platform's build path shells out to.</summary>
    public static IReadOnlyList<BuildTool> Required()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return
            [
                new BuildTool("git", Lang.Get("tool-git-for"),
                        Lang.Get("tool-git-windows-hint")),
            ];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return
            [
                // Both arrive together, which is why the hint is one command rather than two.
                new BuildTool("git", Lang.Get("tool-git-for"), Lang.Get("tool-xcode-hint")),
                    new BuildTool("python3", Lang.Get("tool-python-for"), Lang.Get("tool-xcode-hint")),
                    new BuildTool("perl", Lang.Get("tool-perl-for"), Lang.Get("tool-ships-with-macos", "perl")),
                    new BuildTool("curl", Lang.Get("tool-curl-for"), Lang.Get("tool-ships-with-macos", "curl")),
                    new BuildTool("tar", Lang.Get("tool-tar-for"), Lang.Get("tool-ships-with-macos", "tar")),
            ];

        return
        [
            new BuildTool("git", Lang.Get("tool-git-for"), Lang.Get("tool-package-manager", "git")),
                new BuildTool("python3", Lang.Get("tool-python-for"), Lang.Get("tool-package-manager", "python3")),
                new BuildTool("perl", Lang.Get("tool-perl-for"), Lang.Get("tool-package-manager", "perl")),
                new BuildTool("curl", Lang.Get("tool-curl-for"), Lang.Get("tool-package-manager", "curl")),
                new BuildTool("tar", Lang.Get("tool-tar-for"),
                "Install it with your package manager, e.g. apt install tar"),
        ];
    }

    /// <summary>
    /// What is missing, cheapest check there is — no process is started.
    /// </summary>
    /// <param name="has">
    /// Overrides the PATH lookup. Tests need to describe a machine they are not running on;
    /// a real caller passes nothing.
    /// </param>
    public static PrereqReport Check(Func<string, bool>? has = null)
    {
        if (UnsupportedReason() is { } why) return new PrereqReport([], why);

        has ??= name => ExecutableLookup.Exists(name);

        return new PrereqReport([.. Required().Where(t => !has(t.Name))]);
    }
}
