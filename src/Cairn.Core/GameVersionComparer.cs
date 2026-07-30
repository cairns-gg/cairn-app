namespace Cairn.Core;

/// <summary>
/// Orders Vintage Story version strings the way the game does — numerically, and with
/// pre-releases below the stable release they lead to.
///
/// A lexical sort gets this wrong in both directions: "1.10.0" sorts before "1.9.14", and
/// "1.22.0-rc.1" sorts after "1.22.0".
/// </summary>
public sealed class GameVersionComparer : IComparer<string>
{
    public static readonly GameVersionComparer Ascending = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        if (x == y) return 0;

        int[] a, b;
        try
        {
            a = GameVersions.SplitVersionString(x);
            b = GameVersions.SplitVersionString(y);
        }
        catch (ArgumentNullException)
        {
            return string.CompareOrdinal(x, y);
        }

        // Element 3 carries the release type (dev < pre < rc < stable), so a plain
        // element-wise comparison already ranks pre-releases correctly.
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }

        // Equal numerically but textually different, e.g. "1.22" and "1.22.0". Fall back
        // to something stable so sorting stays a total order.
        return string.CompareOrdinal(x, y);
    }

    /// <summary>Newest first, which is how versions are presented everywhere in Cairn.</summary>
    public static IEnumerable<string> Descending(IEnumerable<string> versions) =>
        versions.OrderByDescending(v => v, Ascending);
}
