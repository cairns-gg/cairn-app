namespace Cairn.Core.Tests;

/// <summary>
/// Shared corpus of version strings. Deliberately includes malformed input, because the
/// game's parser is permissive in ways that matter (see GameVersions.IsPlausibleVersion)
/// and our port must reproduce that permissiveness exactly rather than tightening it.
/// </summary>
public static class VersionCorpus
{
    public static readonly string[] Versions =
    [
        // ordinary releases
        "1.22.5", "1.22.0", "1.21.5", "1.19.0", "1.0.0", "2.0.0", "1.10.0", "1.9.0",
        // two-segment, which the parser normalises by appending ".0"
        "1.22", "1.9", "0.1",
        // pre-release forms, which land a rank in element [3]
        "1.22.0-rc.1", "1.22.0-rc.10", "1.22.0-pre.1", "1.22.0-pre.5", "1.22.0-dev.1",
        "1.22-pre.1", "1.22-rc.2",
        // real ModDB tags observed in the wild
        "1.18.8", "1.22.4", "5.0.8",
        // malformed: the game reads these as major version 0 rather than failing
        ">=1.22.0", "^1.22", "~1.22.0", "1.22.x", "garbage", "v1.22.5", "1..2",
        // odd but parseable
        "-1.22.0", "1.22.0-", "1.22.0.4",
    ];

    /// <summary>Every ordered pair, for the comparison functions.</summary>
    public static IEnumerable<(string A, string B)> Pairs()
    {
        foreach (var a in Versions)
        foreach (var b in Versions)
            yield return (a, b);
    }
}
