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
