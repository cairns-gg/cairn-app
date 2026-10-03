namespace Cairn.Core.Packs;

/// <summary>
/// The name a mod file is allowed to have inside a pack's Mods directory.
///
/// Two properties. A name has to be a bare filename, because it is combined with a
/// directory and "../../evil.zip" would write outside the pack. And it has to be one of the
/// kinds of file the game loads from a mod path, because anything else is not a mod: a
/// release named otherwise is refused rather than installed as something the game will
/// ignore.
///
/// Neither says what Cairn may remove. That is decided by the lock alone — the sweep in
/// <see cref="PackSyncer"/> removes what the previous lock named and nothing else, so a mod
/// somebody placed by hand is never Cairn's to delete whatever it is called.
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
    /// Not what may be deleted. This list was once the sweep's set as well, and keying
    /// removal on the extension deleted loose mods people had placed by hand; the sweep
    /// works from the previous lock now, so widening this widens nothing that is removed.
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
    /// Whether this is a kind of file the game loads as a mod — see <see cref="Extensions"/>,
    /// and not a statement about who may remove it. Length is checked as well as the suffix
    /// so a file called exactly ".zip" — which has no name at all — is not treated as a mod.
    /// </summary>
    public static bool HasModExtension(string? name) =>
        name is not null
        && Extensions.Any(e => name.Length > e.Length
                               && name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
