using System.Runtime.InteropServices;

namespace Cairn.Core.Runtime;

/// <summary>A .NET installation root, i.e. a directory containing shared/Microsoft.NETCore.App.</summary>
public sealed record DotnetRuntime(string Root, ExecutableArch Arch, IReadOnlyList<Version> Frameworks)
{
    public bool Satisfies(Version required) =>
        Frameworks.Any(v => v.Major == required.Major && v >= required);

    public Version? Best(Version required) =>
        Frameworks.Where(v => v.Major == required.Major && v >= required).Max();
}

/// <summary>
/// Finds a .NET runtime of a specific architecture.
///
/// The game is a framework-dependent x64 apphost, so it needs an x64 Microsoft.NETCore.App.
/// A default .NET install on Apple Silicon is arm64, which cannot host it — hence matching
/// on architecture rather than just "is .NET present".
/// </summary>
public static class DotnetRuntimeLocator
{
    private static string HostFileName => Host.This.Exe("dotnet");

    /// <summary>Plausible install roots, most authoritative first.</summary>
    /// <param name="preferredRoot">A root to try ahead of everything, or null.</param>
    /// <param name="os">
    /// Taken rather than asked, so the Windows list is checkable from any machine. The two
    /// lists share nothing at all below the environment variables, so a mistake in either
    /// is invisible to a run on the other.
    /// </param>
    /// <param name="environment">
    /// How to read DOTNET_ROOT and its x64 sibling. Taken for the same reason as the
    /// platform, and it is not hypothetical: a CI runner has DOTNET_ROOT set to
    /// /usr/share/dotnet, so a test asking for the Windows list on Linux got a Unix path
    /// back in it and could not tell that from the branch being wrong. The same arrangement
    /// <see cref="GameInstall.CandidateDirectories(string?, string?)"/> already makes, and
    /// for the same reason.
    /// </param>
    public static IEnumerable<string> CandidateRoots(
        string? preferredRoot = null, HostOs? os = null, Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;

        if (!string.IsNullOrWhiteSpace(preferredRoot)) yield return preferredRoot;

        foreach (var name in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            var value = read(name);
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }

        if ((os ?? Host.This) == HostOs.Windows)
        {
            foreach (var var in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var pf = Environment.GetEnvironmentVariable(var);
                if (!string.IsNullOrWhiteSpace(pf)) yield return Path.Combine(pf, "dotnet");
            }

            yield break;
        }

        // Microsoft's .pkg records where it installed, per architecture. This is what the
        // game's apphost falls back on when no environment is set (a Finder or Steam launch).
        foreach (var marker in new[] { "/etc/dotnet/install_location_x64", "/etc/dotnet/install_location" })
        {
            var recorded = ReadFirstLine(marker);
            if (recorded is not null) yield return recorded;
        }

        yield return "/usr/local/share/dotnet/x64";
        yield return "/usr/local/share/dotnet";
        yield return "/usr/share/dotnet";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
    }

    /// <summary>Reads a root, or null when it is not a usable .NET installation.</summary>
    public static DotnetRuntime? Inspect(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        var sharedDir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(sharedDir)) return null;

        var frameworks = new List<Version>();
        foreach (var dir in Directory.EnumerateDirectories(sharedDir))
        {
            // Strip any pre-release suffix: "10.0.0-rc.1" -> "10.0.0".
            var name = Path.GetFileName(dir);
            var dash = name.IndexOf('-');
            if (dash > 0) name = name[..dash];

            if (Version.TryParse(name, out var v)) frameworks.Add(v);
        }

        if (frameworks.Count == 0) return null;

        var host = Path.Combine(root, HostFileName);
        var arch = File.Exists(host) ? ExecutableImage.ReadArchitecture(host) : ExecutableArch.Unknown;

        return new DotnetRuntime(root, arch, frameworks);
    }

    /// <summary>
    /// First candidate root that is the right architecture and offers the required
    /// framework. Among discovered candidates, one whose architecture cannot be
    /// determined is accepted only when nothing better exists, so a readable-but-unusual
    /// layout is not fatal.
    ///
    /// <paramref name="preferredRoots"/> are overrides rather than just first guesses: the
    /// first of them that can host the app wins outright, even over an
    /// architecture-confirmed system install. Otherwise a caller-managed private runtime
    /// could never take effect on a machine that already has .NET.
    ///
    /// Several of them, in order, because two callers now have an opinion: an install may
    /// bring its own runtime and Cairn may be managing one. Trying only the first would
    /// mean a bundled runtime that turned out unusable silently discarded the private one
    /// rather than falling back to it.
    /// </summary>
    public static DotnetRuntime? Find(
        ExecutableArch arch, Version required, params string?[] preferredRoots)
    {
        foreach (var preferredRoot in preferredRoots)
        {
            if (string.IsNullOrWhiteSpace(preferredRoot)) continue;

            var overridePath = SafeFullPath(preferredRoot);
            var preferred = overridePath is null ? null : Inspect(overridePath);

            var usable = preferred is not null
                         && preferred.Satisfies(required)
                         && (preferred.Arch == arch || preferred.Arch == ExecutableArch.Unknown);

            if (usable) return preferred;
        }

        DotnetRuntime? fallback = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidateRoots())
        {
            var full = SafeFullPath(candidate);
            if (full is null || !seen.Add(full)) continue;

            var runtime = Inspect(full);
            if (runtime is null || !runtime.Satisfies(required)) continue;

            if (runtime.Arch == arch) return runtime;
            if (runtime.Arch == ExecutableArch.Unknown) fallback ??= runtime;
        }

        return fallback;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
