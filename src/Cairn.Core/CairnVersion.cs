using System.Reflection;

namespace Cairn.Core;

/// <summary>
/// What version this is, for showing a person.
///
/// Read from the assembly rather than kept in a constant, so there is one place a version
/// is decided — the tag the release was cut from — instead of a tag and a source file that
/// can disagree. A build nobody stamped says "dev", which is true and more useful than a
/// number that was last correct some releases ago.
/// </summary>
public static class CairnVersion
{
    /// <summary>e.g. "0.1.3", or "dev" for a build made without a version.</summary>
    public static string Current { get; } = Read();

    /// <summary>
    /// The commit this was built from, or null for a build that was not stamped with one.
    ///
    /// Separate from <see cref="Current"/> because it answers a different question and has
    /// a different audience: a version identifies a release to a person, and this
    /// identifies the source to somebody checking that the binary they are running matches
    /// the repository they are reading. The release manifest and the build attestation
    /// name the same commit, so all three either agree or visibly do not.
    ///
    /// Null off CI, deliberately — see Directory.Build.props. A local build claiming a
    /// commit would be claiming its working tree was clean, which nothing here checked.
    /// </summary>
    public static string? Commit { get; } = ReadCommit();

    private static string Read()
    {
        // This assembly, deliberately, and not the entry one. The entry assembly is
        // whoever is hosting — under a test runner that is the runner, which cheerfully
        // reported its own 17.11.1 as Cairn's version. Core is built by the same publish
        // as the launcher and the CLI, so -p:Version reaches it too, and it is the one
        // assembly all three have in common.
        var informational = typeof(CairnVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "dev";

        // The SDK appends "+<commit sha>" when the repository is known. Useful in a log,
        // noise in a window title.
        var version = informational.Split('+')[0];

        // What an unstamped build reports. Saying "dev" is honest; saying 1.0.0 is a claim
        // that this is a release, and it will be believed.
        return version is "1.0.0" or "0.0.0" ? "dev" : version;
    }

    private static string? ReadCommit()
    {
        // Same assembly as Read(), for the same reason: under a test runner the entry
        // assembly is the runner, and its commit is not Cairn's.
        var informational = typeof(CairnVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Everything after the first '+' is build metadata, which is where the SDK puts
        // SourceRevisionId. No '+' means nothing stamped it, which is the ordinary case for
        // a build made outside CI.
        var plus = informational?.IndexOf('+') ?? -1;
        if (plus < 0) return null;

        var commit = informational![(plus + 1)..];
        return string.IsNullOrWhiteSpace(commit) ? null : commit;
    }
}
