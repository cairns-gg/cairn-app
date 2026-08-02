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
}
