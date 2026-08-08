namespace Cairn.Core.Packs;

/// <summary>Which end of the game a copy of a pack is being installed for.</summary>
public enum ModSide
{
    Client,
    Server,
}

/// <summary>
/// What ModDB's <c>side</c> field means for an install, and what it does not.
///
/// It is metadata about a mod, not about a file: the authority is the <c>side</c> in the
/// <c>modinfo.json</c> inside each zip, which the game's own mod loader reads and obeys —
/// a mod for the other side is not loaded, rather than loaded and broken. So a wrong-side
/// mod costs a download and some disk, and nothing else.
///
/// That is the whole reason nothing is skipped on the strength of this. ModDB's field is
/// loose — absent on plenty of mods, and stale on others, which is what the moddb-audit
/// tool exists to keep measuring — and it is known only *before* the download, while the
/// trustworthy answer arrives inside the file we would have been trying not to fetch.
/// Skipping on a guess would also make a pack's lock describe something the machine does
/// not have, on the copy most likely to be compared against an author's.
/// </summary>
public static class ModSides
{
    /// <summary>The other side's name, or null when the field says nothing useful.</summary>
    private static string? Named(string? side)
    {
        if (string.IsNullOrWhiteSpace(side)) return null;

        var trimmed = side.Trim();

        // "both" and "universal" are the common ways of saying "either", and anything
        // unrecognised is treated the same way: silence is better than a wrong warning.
        return trimmed.Equals("client", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("server", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToLowerInvariant()
            : null;
    }

    /// <summary>Whether ModDB marks this mod for the side we are not installing for.</summary>
    public static bool WrongSide(string? declared, ModSide installingFor) =>
        Named(declared) is { } named && named != Describe(installingFor);

    public static string Describe(ModSide side) => side == ModSide.Server ? "server" : "client";
}
