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
    /// The kinds of file Vintage Story loads from a mod path, and therefore the kinds Cairn
    /// is willing to install.
    ///
    /// ModDB accepts these three for a release — see docs/moddb-listing.md — so this is
    /// what its API can hand back, not a preference. A folder mod is a directory and is
    /// unaffected either way.
    ///
    /// This list used to be the sweep's set too, on the reasoning that anything Cairn can
    /// write must be something Cairn can later clear away. The reasoning was sound and the
    /// mechanism was not: keying removal on the extension meant Cairn deleted loose mods
    /// somebody had placed by hand, which it had never written. The sweep now works from
    /// the previous lock — Cairn's record of what it actually installed — so the two sets
    /// are deliberately no longer coupled, and widening this one no longer widens what gets
    /// deleted. See the sweep in <see cref="PackSyncer"/>.
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
        !IsBare(name) ? Lang.Get("modfile-not-plain")
        : !HasModExtension(name)
            ? Lang.Get("modfile-wrong-kind", string.Join(", ", Extensions))
            : null;

    /// <summary>
    /// Whether this is a filename and nothing else — no directory part, nothing rooted,
    /// and nothing that means somewhere other than where it reads.
    ///
    /// Delegated to <see cref="BareFileName"/> rather than kept here, because the game
    /// catalogue and the .NET runtime index build paths out of remote names too and could
    /// not sensibly reach for something called "ModFileName". What kind of file a pack may
    /// hold is this type's business; what counts as a filename at all is not specific to
    /// mods and is one rule for the whole tree.
    /// </summary>
    public static bool IsBare(string? name) => BareFileName.IsBare(name);

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
