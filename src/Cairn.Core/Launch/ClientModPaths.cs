using System.Text.Json.Nodes;

namespace Cairn.Core.Launch;

/// <summary>
/// Holds the mod directories named inside a pack's <c>clientsettings.json</c> to that pack.
///
/// A new pack's settings are seeded from the player's own, so their keybinds and graphics
/// carry over instead of starting from defaults. That file records where the game looks for
/// mods as a list of <em>absolute</em> strings, captured the first time plain Vintage Story
/// ran:
///
/// <code>
/// "stringListSettings": {
///   "modPaths": ["Mods", "/Users/you/Library/Application Support/VintagestoryData/Mods"]
/// }
/// </code>
///
/// <c>--addModPath</c> is additive to that list, not a replacement for it, so the seeded copy
/// quietly gave every pack the player's personal Mods folder as well as its own. A mod
/// installed both ways — already in plain Vintage Story, then added to a pack — loaded
/// twice, which is precisely what a pack exists to prevent.
///
/// The per-pack <c>--dataPath</c> does not help here. It moves <c>Saves</c>, <c>ModConfig</c>
/// and the rest, but the leaked entry is a literal path written into the settings rather
/// than one derived from the data path at startup, so it survives being pointed elsewhere.
///
/// Rewritten on seeding <em>and</em> again on every launch: every pack made before this
/// existed still carries the copied value, and there is nothing to reach into them but the
/// next launch. It is also the launch that has to be right — a pack whose settings were
/// correct when created and edited since is the same bug.
/// </summary>
public static class ClientModPaths
{
    private const string Bucket = "stringListSettings";
    private const string Key = "modPaths";

    /// <summary>
    /// The game's own relative entry, resolved against the binaries directory rather than
    /// the data path. It holds VSSurvivalMod, VSEssentials and VSCreativeMod — the game
    /// itself — so it stays in every list. It is not a place mods are added by hand either:
    /// the game ships a <c>do_not_add_mods_here.txt</c> in it saying exactly that.
    /// </summary>
    private const string Binaries = "Mods";

    private static string OwnModsIn(string dataPath) => Path.Combine(dataPath, "Mods");

    /// <summary>
    /// Rewrites the list to name only the game's own Mods directory and this pack's,
    /// dropping anything else. Returns what was dropped, for a caller that wants to say so.
    ///
    /// Written even when the file or the key is missing, rather than left to the game's own
    /// default. The default is not the pack's: a launch with <c>--dataPath</c> into a pack
    /// that had never been played logged
    ///
    /// <code>
    /// Will search the following paths for mods:
    ///     ~/.cairn/games/1.22.6.app/Mods
    ///     ~/Library/Application Support/VintagestoryData/Mods
    ///     ~/.cairn/packs/anego/Mods
    /// </code>
    ///
    /// — the player's own folder in the middle, and the pack's data-path Mods absent
    /// entirely. Saying what the list is costs one key in a file Cairn already writes;
    /// assuming it costs the thing this class exists to prevent.
    /// </summary>
    public static IReadOnlyList<string> Confine(string clientSettingsPath, string dataPath)
    {
        var root = ClientSettingsFile.TryLoad(clientSettingsPath) ?? new JsonObject();

        if (root[Bucket] is not JsonObject lists)
        {
            lists = new JsonObject();
            root[Bucket] = lists;
        }

        var paths = lists[Key] as JsonArray ?? [];
        var own = OwnModsIn(dataPath);

        var keep = new List<string> { Binaries, own };
        var dropped = new List<string>();

        foreach (var entry in paths)
        {
            // A hand-edited file can hold anything; a non-string is not a path we can judge,
            // and dropping it is the same as dropping a foreign one.
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path))
            {
                dropped.Add(entry?.ToJsonString() ?? "null");
                continue;
            }

            if (Same(path, Binaries) || Same(path, own)) continue;

            // A second directory inside the pack's own data path is still the pack's, and
            // somebody put it there on purpose. Only paths that reach outside are the bug.
            if (Inside(path, dataPath))
            {
                if (!keep.Any(k => Same(k, path))) keep.Add(path);
                continue;
            }

            dropped.Add(path);
        }

        if (dropped.Count == 0 && Matches(paths, keep)) return [];

        lists[Key] = new JsonArray([.. keep.Select(p => (JsonNode)JsonValue.Create(p))]);
        ClientSettingsFile.Write(clientSettingsPath, root);

        return dropped;
    }

    /// <summary>
    /// Whether the list is already exactly what would be written, so an unchanged pack is
    /// not rewritten on every launch — the game reads this file at startup and writes it at
    /// exit, and a launcher touching it in between should be able to change nothing.
    /// </summary>
    private static bool Matches(JsonArray paths, List<string> keep)
    {
        if (paths.Count != keep.Count) return false;

        for (var i = 0; i < keep.Count; i++)
            if (paths[i] is not JsonValue v || !v.TryGetValue<string>(out var p) || !Same(p, keep[i]))
                return false;

        return true;
    }

    /// <summary>
    /// Path comparison as the filesystem underneath would do it: Windows and macOS do not
    /// distinguish case here, and treating "Mods" and "mods" as two directories would add a
    /// duplicate of the entry it was meant to keep.
    /// </summary>
    private static bool Same(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), PathComparison);

    private static bool Inside(string path, string dataPath)
    {
        // Relative entries other than the game's own are resolved by the game against the
        // binaries directory, not the data path, so they are never inside it.
        if (!Path.IsPathRooted(path)) return false;

        var root = Normalise(dataPath);
        var candidate = Normalise(path);

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Normalise(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.IsPathRooted(path) ? Path.GetFullPath(path) : path);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Not a path this machine can express. Compared as written, which will not match
            // anything we keep — so it is dropped, which is the right answer for it anyway.
            return path;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
