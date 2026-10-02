using Cairn.Core.Packs;

namespace Cairn.Core.Launch;

/// <summary>What the game will make of one entry in the local mods folder.</summary>
public enum LocalModOutcome
{
    /// <summary>The pack has no mod by this id, so it simply loads alongside the pack's.</summary>
    Adds,

    /// <summary>The pack has this mod at a lower version, so the game loads this one instead.</summary>
    Overrides,

    /// <summary>The pack has this mod at a higher version, so the game loads the pack's.</summary>
    Loses,

    /// <summary>
    /// The pack has this mod and nobody can say which copy the game will pick: the same
    /// version, or one of the two has none.
    /// </summary>
    Undecided,

    /// <summary>Loaded, but what it is could not be read — a code mod, or no usable modinfo.json.</summary>
    Unread,
}

/// <param name="Entry">The file or folder name, as it sits in the local mods folder.</param>
/// <param name="PackVersion">The version of the pack's own copy, when the pack has one.</param>
public sealed record LocalMod(
    string Entry, string? ModId, string? Version, LocalModOutcome Outcome, string? PackVersion, Message Line);

/// <param name="ModPath">
/// The folder to hand the game alongside the pack's own, or null when there is none to
/// hand it — not set, or not there.
/// </param>
/// <param name="Lines">What to say in the launch log, in order: the folder, then each mod.</param>
public sealed record LocalModsPlan(string? ModPath, IReadOnlyList<LocalMod> Mods, IReadOnlyList<Message> Lines)
{
    public static readonly LocalModsPlan None = new(null, [], []);
}

/// <summary>
/// A folder of mods somebody is working on, loaded alongside every pack they play.
///
/// This is how a mod author tests a mod against a pack without it being on ModDB, and
/// deliberately the bluntest form of that: one folder, set in Preferences, handed to the game
/// as one more <c>--addModPath</c>. Nothing is copied into the pack, nothing reaches the
/// manifest or the lock, and so nothing is ever published or shared — a path on the author's
/// disk means nothing on anybody else's.
///
/// What it does not do is decide which copy loads when the folder and the pack hold the same
/// mod. The game does that: <c>ModLoader.CheckDuplicateModIDMods</c> keeps the highest version
/// by <c>GameVersion.IsNewerVersionThan</c> and disables the rest, regardless of which
/// directory each came from. Making the local copy win outright would mean keeping the pack's
/// zip away from the game — a staging directory of links rebuilt on every launch — and .NET
/// has no portable hard link to build one with. So the game's rule stands, and this says in
/// advance what it is going to decide, because the failure it otherwise produces is silent:
/// a rebuilt mod that is not newer than the pack's loses, and its author spends an evening
/// testing the old code.
///
/// The comparison is <see cref="GameVersions"/>, which is the game's own comparator ported
/// and held to it by the conformance suite. The trap worth naming is that it ranks
/// <c>1.2.0-dev</c> below <c>1.2.0</c>, which is exactly the version an author reaches for.
/// </summary>
public static class LocalMods
{
    /// <param name="folder">The folder from Preferences, or null when none is set.</param>
    /// <param name="packModsDir">The pack's own Mods directory, to compare against.</param>
    public static LocalModsPlan Plan(string? folder, string packModsDir)
    {
        if (string.IsNullOrWhiteSpace(folder)) return LocalModsPlan.None;

        // Left out rather than handed over, so the game's log does not say "Not found?" on
        // a launch that otherwise worked — but said, because a folder that has gone away
        // (an unplugged disk, a renamed project) is otherwise a mod that silently stopped
        // loading.
        if (!Directory.Exists(folder))
            return new LocalModsPlan(null, [], [new Message("launch-local-missing", folder)]);

        var pack = PackVersions(packModsDir);
        var mods = Entries(folder).Select(e => Judge(e, pack)).OfType<LocalMod>().ToList();

        return new LocalModsPlan(
            folder, mods, [new Message("launch-local-from", folder), .. mods.Select(m => m.Line)]);
    }

    private static LocalMod? Judge(string path, IReadOnlyDictionary<string, string?> pack)
    {
        var entry = Path.GetFileName(path);
        var isFolder = Directory.Exists(path);
        var extension = Path.GetExtension(entry);

        // The game's own test for what counts — every folder, and files by extension — so
        // nothing is reported here that the game will not try to load, and nothing it will
        // load goes unmentioned.
        if (!isFolder && !IsModExtension(extension)) return null;

        // A .cs or .dll declares its id in an attribute the game reads by compiling or
        // loading it. Not something to do from a launcher, so these are passed through and
        // the game's rule applies unpredicted.
        if (!isFolder && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return new LocalMod(entry, null, null, LocalModOutcome.Unread, null,
                new Message("launch-local-code", entry));

        var info = ModDependencies.Describe(path);
        var id = GameModId(info.ModId, info.Name);
        var version = Trimmed(info.Version);

        if (id is null)
            return new LocalMod(entry, null, version, LocalModOutcome.Unread, null,
                new Message("launch-local-unread", entry, info.Problem ?? Lang.Get("deps-no-modinfo")));

        // The game deserialises modinfo.json with `name` marked required and refuses the mod
        // without it ("Required property 'Name' not found in JSON"). A hand-written
        // modinfo.json in a project folder is exactly where that gets left out, and saying
        // "loading" about a mod the game is about to reject is the one wrong answer here.
        if (string.IsNullOrWhiteSpace(info.Name))
            return new LocalMod(entry, id, version, LocalModOutcome.Unread, null,
                new Message("launch-local-refused", entry, Lang.Get("deps-modinfo-no-name")));

        var label = version is null ? id : $"{id} {version}";

        if (!pack.TryGetValue(id, out var theirs))
            return new LocalMod(entry, id, version, LocalModOutcome.Adds, null,
                new Message("launch-local-adds", label));

        if (version is null || theirs is null)
            return new LocalMod(entry, id, version, LocalModOutcome.Undecided, theirs,
                new Message("launch-local-unversioned", label));

        if (GameVersions.IsNewerVersionThan(version, theirs))
            return new LocalMod(entry, id, version, LocalModOutcome.Overrides, theirs,
                new Message("launch-local-overrides", label, theirs));

        if (GameVersions.IsNewerVersionThan(theirs, version))
            return new LocalMod(entry, id, version, LocalModOutcome.Loses, theirs,
                IsPreReleaseOf(version, theirs)
                    ? new Message("launch-local-loses-prerelease", label, theirs)
                    : new Message("launch-local-loses", label, theirs));

        // Neither is newer. The game's comparator then either calls them equal and keeps
        // whichever its sort met first, which comes down to filenames, or — for "1.2" beside
        // "1.2.0", which it calls lower in both directions — is not a consistent order at
        // all. Either way nobody can say in advance which loads.
        return new LocalMod(entry, id, version, LocalModOutcome.Undecided, theirs,
            new Message("launch-local-tie", label));
    }

    /// <summary>
    /// The pack's mods by id, at the highest version of each the game would see. Read from
    /// the zips rather than the lock: it is what is in the directory that the game compares,
    /// including anything placed there by hand, and its version as the zip spells it rather
    /// than as ModDB does.
    /// </summary>
    private static Dictionary<string, string?> PackVersions(string packModsDir)
    {
        var versions = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var mod in InstalledMods.Scan(packModsDir).Mods)
        {
            if (GameModId(mod.ModId, mod.Name) is not { } id) continue;

            if (!versions.TryGetValue(id, out var seen)
                || seen is null
                || (mod.Version is { } v && GameVersions.IsNewerVersionThan(v, seen)))
                versions[id] = mod.Version;
        }

        return versions;
    }

    /// <summary>
    /// The id the game will know a mod by. A modinfo.json may leave <c>modid</c> out, and the
    /// game then derives one from <c>name</c> (<c>ModInfo.ToModID</c>): letters and digits
    /// only, lowercased. Compared ordinally afterwards, as the game groups them.
    /// </summary>
    internal static string? GameModId(string? modId, string? name)
    {
        if (Trimmed(modId) is { } id) return id;
        if (string.IsNullOrWhiteSpace(name)) return null;

        // The game throws for a name that starts with a digit, and the mod then fails to
        // load at all — so there is no id to compare.
        if (char.IsAsciiDigit(name[0])) return null;

        var derived = new string(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return derived.Length == 0 ? null : derived;
    }

    /// <summary>
    /// "1.2.0-dev" against "1.2.0": the same release, ranked below it because of the suffix.
    /// Worth its own sentence, because a version that reads as newer to everybody but the
    /// game is the one most likely to be left there.
    /// </summary>
    private static bool IsPreReleaseOf(string version, string reference)
    {
        if (!version.Contains('-')) return false;

        var mine = GameVersions.SplitVersionString(version);
        var theirs = GameVersions.SplitVersionString(reference);

        return mine.Length >= 3 && theirs.Length >= 3 && mine.Take(3).SequenceEqual(theirs.Take(3));
    }

    private static bool IsModExtension(string extension) =>
        extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Entries(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
