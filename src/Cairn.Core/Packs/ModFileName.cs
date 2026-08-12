namespace Cairn.Core.Packs;

/// <summary>
/// The name a mod file is allowed to have inside a pack's Mods directory.
///
/// Two properties, and they only work together. A name has to be a bare filename, because
/// it is combined with a directory and "../../evil.zip" would write outside the pack. And
/// it has to be one of the kinds of file Cairn installs, because the sweep that clears out
/// what a pack no longer lists can only remove what it knows to look for — a name outside
/// that set is a file nothing would ever tidy away again, still sitting in the directory
/// handed to the game long after the mod was removed from the pack.
///
/// This lives here rather than inside <see cref="PackSyncer"/>, where it was written and
/// where it was correct. Three places write or read a lock's filename and only that one
/// had the guard: <see cref="InstallImport.BuildLock"/> recorded whatever ModDB's API
/// said, and <see cref="Diagnostics"/> combined it with a directory and reported on
/// whatever it found there — an existence-and-size oracle for any path, in text people are
/// asked to paste into a bug report. A rule kept private to one caller is a rule the other
/// callers do not follow.
/// </summary>
public static class ModFileName
{
    /// <summary>
    /// The kinds of file Vintage Story loads from a mod path, which is also exactly what
    /// the sweep in <see cref="PackSyncer"/> knows to remove. Kept deliberately in step:
    /// anything Cairn can write has to be something Cairn can later clear away.
    ///
    /// ModDB accepts these three for a release — see docs/moddb-listing.md — so this is
    /// what its API can hand back, not a preference. A folder mod is a directory and is
    /// unaffected either way.
    /// </summary>
    public static readonly string[] Extensions = [".zip", ".dll", ".cs"];

    /// <summary>
    /// The name, or null when it is not one a pack may hold. Rejects rather than
    /// sanitises, so a name that tries to escape is reported instead of quietly becoming
    /// something else.
    /// </summary>
    public static string? Safe(string? name) => Problem(name) is null ? name : null;

    /// <summary>
    /// Why this name cannot be used, phrased to finish "refusing a mod filename that …",
    /// or null when there is nothing wrong with it.
    ///
    /// The two reasons are kept apart because they mean different things to whoever reads
    /// the sync log: one is a name trying to write somewhere it should not, and the other
    /// is an ordinary name for a kind of file Cairn does not handle. Reporting both as one
    /// message would make a mod nobody can install look like an attack.
    /// </summary>
    public static string? Problem(string? name) =>
        !IsBare(name) ? "is not a plain file name"
        : !HasModExtension(name)
            ? $"is not a kind of mod file Cairn installs ({string.Join(", ", Extensions)})"
            : null;

    /// <summary>
    /// Whether this is a filename and nothing else — no directory part, nothing rooted,
    /// and nothing that means somewhere other than where it reads.
    /// </summary>
    public static bool IsBare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // GetFileName strips any directory part; if that changed the string, the original
        // was carrying one. Also catches "..", rooted paths and both separators.
        if (Path.GetFileName(name) != name || name is "." or "..") return false;
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0) return false;
        if (Path.IsPathRooted(name)) return false;

        // Windows reads "mod.zip:hidden" as an alternate data stream, which File.Create
        // will happily write and which neither the sweep nor a directory listing shows.
        // The colon survives GetFileName unchanged, so it has to be named on its own.
        return !name.Contains(':');
    }

    /// <summary>
    /// Whether this is a file Cairn installs, and therefore one it is entitled to remove.
    /// Length is checked as well as the suffix so a file called exactly ".zip" — which has
    /// no name at all — is not treated as a mod.
    /// </summary>
    public static bool HasModExtension(string? name) =>
        name is not null
        && Extensions.Any(e => name.Length > e.Length
                               && name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
