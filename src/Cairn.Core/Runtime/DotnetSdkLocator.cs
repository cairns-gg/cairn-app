namespace Cairn.Core.Runtime;

/// <summary>A .NET installation root that can compile, i.e. one with an sdk/ directory.</summary>
public sealed record DotnetSdk(string Root, IReadOnlyList<Version> Versions)
{
    public string Executable =>
        Path.Combine(Root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    /// <summary>
    /// Whether this install satisfies a global.json pin with <c>rollForward: latestFeature</c>.
    ///
    /// Same major <em>and</em> minor, then at least the pinned feature band. Not simply
    /// ">= required": latestFeature rolls forward within one major.minor only, so a .NET 11
    /// SDK does not satisfy a 10.0.100 pin however much newer it is, and accepting one would
    /// hand the build an SDK its own global.json rejects — twenty minutes in.
    /// </summary>
    public bool Satisfies(Version required) => Versions.Any(v =>
        v.Major == required.Major && v.Minor == required.Minor && v >= required);

    public Version? Best(Version required) => Versions
        .Where(v => v.Major == required.Major && v.Minor == required.Minor && v >= required)
        .Max();
}

/// <summary>
/// Finds a .NET SDK, as opposed to a runtime.
///
/// Separate from <see cref="DotnetRuntimeLocator"/> because the two answer different
/// questions about the same directories. A runtime is what the game is <em>hosted</em> on,
/// so it must match the game's architecture; an SDK is what Optimum is <em>compiled</em>
/// with, where architecture does not matter at all — an arm64 SDK on Apple Silicon builds
/// the same assemblies as an x64 one. Matching an SDK on architecture would reject a
/// perfectly good toolchain for a reason that only applies to hosting.
/// </summary>
public static class DotnetSdkLocator
{
    /// <summary>
    /// What Optimum's global.json pins: 10.0.100 with rollForward latestFeature.
    ///
    /// Held here rather than read from the checkout because it gates whether Cairn needs to
    /// download an SDK at all, and that decision is made before anything is cloned.
    /// </summary>
    public static readonly Version RequiredForOptimum = new(10, 0, 100);

    /// <summary>Reads a root, or null when it holds no SDK.</summary>
    public static DotnetSdk? Inspect(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var sdkDir = Path.Combine(root, "sdk");
        if (!Directory.Exists(sdkDir)) return null;

        var versions = new List<Version>();

        foreach (var dir in Directory.EnumerateDirectories(sdkDir))
        {
            // Strip any pre-release suffix, as the runtime locator does: "10.0.100-rc.1".
            var name = Path.GetFileName(dir);
            var dash = name.IndexOf('-');
            if (dash > 0) name = name[..dash];

            if (Version.TryParse(name, out var v)) versions.Add(v);
        }

        return versions.Count == 0 ? null : new DotnetSdk(root, versions);
    }

    /// <summary>
    /// Roots that might hold an SDK, best first.
    ///
    /// The runtime locator's candidates plus whatever <c>dotnet</c> resolves to on PATH.
    /// That last one matters: a developer SDK installed by Homebrew, a distribution package
    /// or the dotnet-install script often lives somewhere the fixed list does not name, and
    /// downloading a second 200 MB SDK next to a working one is a poor first impression.
    /// </summary>
    public static IEnumerable<string> CandidateRoots(string? preferredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredRoot)) yield return preferredRoot;

        if (ExecutableLookup.Find("dotnet") is { } onPath)
        {
            var dir = Path.GetDirectoryName(onPath);
            if (!string.IsNullOrWhiteSpace(dir)) yield return dir;
        }

        foreach (var root in DotnetRuntimeLocator.CandidateRoots()) yield return root;
    }

    /// <summary>First candidate offering an SDK good enough for the pin, or null.</summary>
    public static DotnetSdk? Find(Version required, string? preferredRoot = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidateRoots(preferredRoot))
        {
            string full;
            try { full = Path.GetFullPath(candidate.Trim()); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException
                                          or PathTooLongException)
            { continue; }

            if (!seen.Add(full)) continue;

            var sdk = Inspect(full);
            if (sdk is not null && sdk.Satisfies(required)) return sdk;
        }

        return null;
    }
}
