namespace Cairn.Core;

/// <summary>
/// Vintage Story version-string semantics.
///
/// This is a deliberate, faithful port of <c>Vintagestory.API.Config.GameVersion</c>
/// (VintagestoryApi/Config/GameVersion.cs). We do not reference the game assembly,
/// so Cairn builds in a clean checkout with no game install present. The port is
/// held to the original by GameVersionConformanceTests, which runs the real
/// implementation side by side when VINTAGE_STORY points at an install.
///
/// Quirks reproduced on purpose — do not "fix" these without changing the game:
///   * Unparseable numeric parts become 0 (int.TryParse), so ">=1.22.0" yields
///     [0, 22, 0, 3] rather than throwing. See <see cref="IsPlausibleVersion"/>.
///   * The hyphen check is "index &lt; 1", so a leading '-' counts as no hyphen.
///   * IsLowerVersionThan compares strings for equality before comparing numbers.
/// </summary>
public static class GameVersions
{
    private static readonly string[] Separators = [".", "-"];

    /// <summary>Release-type ranking stored in element [3]: dev &lt; pre &lt; rc &lt; stable.</summary>
    public const int ReleaseDevelopment = 0;
    public const int ReleasePreview = 1;
    public const int ReleaseCandidate = 2;
    public const int ReleaseStable = 3;

    /// <exception cref="ArgumentNullException">version is null or empty.</exception>
    public static int[] SplitVersionString(string version)
    {
        if (string.IsNullOrEmpty(version)) throw new ArgumentNullException(nameof(version));

        // "1.17-pre.1" must normalise to "1.17.0-pre.1" so that the release-type
        // marker lands in parts[3] as the code below assumes.
        var hyphenIndex = version.IndexOf('-');
        var majorMinor = hyphenIndex < 1 ? version : version[..hyphenIndex];
        if (majorMinor.Count(c => c == '.') == 1)
        {
            majorMinor += ".0";
            version = hyphenIndex < 1 ? majorMinor : majorMinor + version[hyphenIndex..];
        }

        var parts = version.Split(Separators, StringSplitOptions.None);
        if (parts.Length <= 3)
        {
            parts = [.. parts, ReleaseStable.ToString()];
        }
        else
        {
            parts[3] = parts[3] switch
            {
                "rc" => ReleaseCandidate.ToString(),
                "pre" => ReleasePreview.ToString(),
                _ => ReleaseDevelopment.ToString(),
            };
        }

        var versions = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            int.TryParse(parts[i], out var v);
            versions[i] = v;
        }

        return versions;
    }

    /// <summary>True when <paramref name="version"/> is at or above <paramref name="reference"/>.</summary>
    public static bool IsAtLeastVersion(string version, string reference)
    {
        var min = SplitVersionString(reference);
        var cur = SplitVersionString(version);

        for (var i = 0; i < min.Length; i++)
        {
            if (i >= cur.Length) return false;
            if (min[i] > cur[i]) return false;
            if (min[i] < cur[i]) return true;
        }

        return true;
    }

    public static bool IsNewerVersionThan(string version, string reference)
    {
        var min = SplitVersionString(reference);
        var cur = SplitVersionString(version);

        for (var i = 0; i < min.Length; i++)
        {
            if (i >= cur.Length) return false;
            if (min[i] > cur[i]) return false;
            if (min[i] < cur[i]) return true;
        }

        return false;
    }

    public static bool IsLowerVersionThan(string version, string reference)
        => version != reference && !IsNewerVersionThan(version, reference);

    /// <summary>True when both versions share major and minor, ignoring revision.</summary>
    public static bool IsSameMajorMinor(string a, string b)
    {
        var pa = SplitVersionString(a);
        var pb = SplitVersionString(b);
        return pa.Length >= 2 && pb.Length >= 2 && pa[0] == pb[0] && pa[1] == pb[1];
    }

    /// <summary>
    /// Guards against the silent-zero trap. The game accepts ">=1.22.0", "^1.22" and
    /// "garbage" and quietly reads them as major version 0, which makes a constraint
    /// satisfied by every real game version. Cairn refuses such strings instead of
    /// passing them through.
    /// </summary>
    public static bool IsPlausibleVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        var hyphenIndex = version.IndexOf('-');
        var numeric = hyphenIndex < 1 ? version : version[..hyphenIndex];

        var segments = numeric.Split('.');
        if (segments.Length is < 2 or > 3) return false;

        foreach (var s in segments)
        {
            if (s.Length == 0) return false;
            if (!s.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(s, out _)) return false;
        }

        if (hyphenIndex >= 1)
        {
            var suffix = version[(hyphenIndex + 1)..];
            var kind = suffix.Split('.')[0];
            if (kind is not ("rc" or "pre" or "dev")) return false;
        }

        return true;
    }
}
