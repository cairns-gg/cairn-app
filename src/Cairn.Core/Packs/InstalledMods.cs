using System.Text.Json.Nodes;
using Cairn.Core.Launch;

namespace Cairn.Core.Packs;

/// <summary>One mod zip found sitting in a Vintage Story install.</summary>
/// <param name="Problem">
/// Why this file could not be read, or null. A zip whose <c>modinfo.json</c> will not parse
/// is not the same as one that declares no dependencies, and the difference has to survive
/// as far as the person deciding what to import.
/// </param>
public sealed record InstalledMod(
    string Path, string FileName, string? ModId, string? Name, string? Version, string? Problem)
{
    /// <summary>"Olla 1.2.0", or the filename when the zip would not say.</summary>
    public string Describe =>
        Name is { Length: > 0 } name
            ? Version is { Length: > 0 } version ? $"{name} {version}" : name
            : FileName;
}

/// <param name="Ignored">
/// Everything in the folder that is not a mod zip, by name. Vintage Story also loads
/// unpacked folder mods and loose <c>.cs</c> files, and those cannot be imported — a pack
/// installs releases ModDB serves. Listed rather than passed over, because "it found 11 of
/// my 14 mods" needs to say which three and why.
/// </param>
public sealed record InstalledModScan(IReadOnlyList<InstalledMod> Mods, IReadOnlyList<string> Ignored);

/// <summary>
/// Reads the mods somebody already has, out of a plain Vintage Story install.
///
/// This is the other half of the mod-path leak that <see cref="ClientModPaths"/> closes.
/// Once a pack stops loading the player's own Mods folder, the mods in it are no longer
/// reachable from any pack — so there had better be a way to bring them into one. The two
/// changes only make sense together.
///
/// Read-only, like everything else Cairn does to the player's own data path: importing
/// copies nothing and moves nothing, and their plain Vintage Story goes on working exactly
/// as it did.
/// </summary>
public static class InstalledMods
{
    /// <summary>Where the game itself puts mods installed from ModDB or dropped in by hand.</summary>
    public static string DefaultModsDir => System.IO.Path.Combine(GameInstall.DefaultDataPath, "Mods");

    /// <summary>Where to read them from, allowing for a data path somebody has corrected.</summary>
    public static string ChosenModsDir => System.IO.Path.Combine(GameInstall.ChosenDataPath, "Mods");

    /// <summary>
    /// A folder somebody picked, and the data path it implies.
    /// </summary>
    /// <param name="ModsDir">The folder to read mod zips from.</param>
    /// <param name="DataPath">
    /// The folder holding it, which is where the worlds are. Derived rather than asked for
    /// separately: <c>Saves</c> sits beside <c>Mods</c>, and a person who fixed the mods
    /// folder and then found the worlds list still reading somewhere else would have been
    /// given half a repair.
    /// </param>
    public sealed record ModsFolder(string ModsDir, string DataPath);

    /// <summary>
    /// What a chosen folder means, allowing for either end of the same answer.
    ///
    /// "Mods" is the folder people can name — it is the one the game's own instructions send
    /// them to, and the one their zips are sitting in. The data path is the concept Cairn
    /// actually needs, since the worlds hang off it too, and it is jargon: nobody calls it
    /// that unless they have set <c>--dataPath</c>. So this asks for the folder they know and
    /// works out the one it needs.
    ///
    /// Both directions are accepted, because at the moment of picking, either is a reasonable
    /// thing to have clicked: a folder containing <c>Mods</c> is a data path, and anything
    /// else is taken as the mods folder itself with its parent as the data path.
    ///
    /// Never refuses a folder for being empty. A Mods folder with nothing in it is a real
    /// state — somebody who has just moved their data path has one — and the dialog already
    /// says when a scan found no zips, which is a better answer than a picker that rejects
    /// the correct folder.
    /// </summary>
    /// <returns>Null only when the folder is not there at all.</returns>
    public static ModsFolder? ChooseModsFolder(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked)) return null;

        var full = System.IO.Path.GetFullPath(picked.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        var inside = System.IO.Path.Combine(full, "Mods");
        if (Directory.Exists(inside)) return new ModsFolder(inside, full);

        // The parent, when there is one. A folder at a volume root has none, and its own
        // path is then the best answer available for where its worlds would be.
        return new ModsFolder(full, System.IO.Path.GetDirectoryName(full) ?? full);
    }

    /// <summary>
    /// Every mod zip in a folder, with what its own <c>modinfo.json</c> says about it.
    ///
    /// Never throws for one bad file: a folder of forty mods with one truncated download in
    /// it should import thirty-nine and say what happened to the fortieth.
    /// </summary>
    public static InstalledModScan Scan(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return new InstalledModScan([], []);

        var mods = new List<InstalledMod>();
        var ignored = new List<string>();

        foreach (var entry in Entries(modsDir))
        {
            var name = System.IO.Path.GetFileName(entry);

            if (!entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // The game's own note to the player is not a mod, and reporting it as one
                // that could not be read would be noise in every single import.
                if (!string.Equals(name, "Creating_Mods.txt", StringComparison.OrdinalIgnoreCase))
                    ignored.Add(name);

                continue;
            }

            var info = ModDependencies.Describe(entry);

            mods.Add(new InstalledMod(
                entry, name, Trimmed(info.ModId), Trimmed(info.Name), Trimmed(info.Version),
                info.Problem ?? (info.ModId is null ? Lang.Get("mods-declares-no-id") : null)));
        }

        return new InstalledModScan(mods, ignored);
    }

    private static IEnumerable<string> Entries(string modsDir)
    {
        try
        {
            return Directory
                .EnumerateFileSystemEntries(modsDir)
                .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The mods switched off in the game's own settings, by whatever they are listed as —
    /// ModDB's mod id for some, a filename for others, so both are matched later.
    ///
    /// A disabled mod is one the player decided not to run. Importing it would quietly turn
    /// it back on, in a pack whose whole claim is that it holds what you were playing.
    /// </summary>
    public static IReadOnlySet<string> DisabledIn(string dataPath)
    {
        var settings = System.IO.Path.Combine(dataPath, "clientsettings.json");
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ClientSettingsFile.TryLoad(settings)?["stringListSettings"]?["disabledMods"]
            is not JsonArray entries)
            return disabled;

        foreach (var entry in entries)
            if (entry is JsonValue value && value.TryGetValue<string>(out var name)
                && !string.IsNullOrWhiteSpace(name))
                disabled.Add(name.Trim());

        return disabled;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
