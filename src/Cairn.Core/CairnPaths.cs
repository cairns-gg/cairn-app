namespace Cairn.Core;

/// <summary>
/// Where Cairn keeps its own state. Kept well away from the game's data path so a
/// pack directory can be deleted without touching saves or settings.
/// </summary>
public static class CairnPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("CAIRN_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cairn");

    public static string PacksRoot => Path.Combine(Root, "packs");

    /// <summary>Game versions Cairn installed itself, one directory per version.</summary>
    public static string GamesRoot => Path.Combine(Root, "games");

    /// <summary>.NET runtimes Cairn downloaded, so the game needs no system-wide install.</summary>
    public static string RuntimesRoot => Path.Combine(Root, "runtimes");

    /// <summary>Shareable pack files written by export.</summary>
    public static string ExportsRoot => Path.Combine(Root, "exports");

    /// <summary>
    /// Things Cairn can always fetch again. Kept apart from packs, games and runtimes so
    /// it can be deleted without losing anything that matters.
    /// </summary>
    public static string CacheRoot => Path.Combine(Root, "cache");

    /// <summary>Mod icons from ModDB, so browsing does not re-download the same images.</summary>
    public static string IconCacheRoot => Path.Combine(CacheRoot, "icons");

    public static string PackDir(string id) => Path.Combine(PacksRoot, id);

    public static string ManifestPath(string id) => Path.Combine(PackDir(id), "pack.json");

    public static string LockPath(string id) => Path.Combine(PackDir(id), "pack.lock.json");

    /// <summary>The directory handed to the game via --addModPath for this pack.</summary>
    public static string ModsDir(string id) => Path.Combine(PackDir(id), "Mods");

    public static IEnumerable<string> ListPackIds()
    {
        if (!Directory.Exists(PacksRoot)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(PacksRoot).OrderBy(d => d))
            if (File.Exists(Path.Combine(dir, "pack.json")))
                yield return Path.GetFileName(dir);
    }
}
