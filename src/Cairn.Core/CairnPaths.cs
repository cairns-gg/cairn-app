namespace Cairn.Core;

/// <summary>
/// Where Cairn keeps its own state.
///
/// A pack's game data — its worlds, its mod configs, its settings — lives inside that
/// pack's directory, because the pack is the instance. Deleting a pack therefore deletes
/// its worlds, which is what people expect: a world made under a pack's mod set usually
/// cannot be opened without it, so keeping one behind would strand data nothing can read.
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

    /// <summary>
    /// Cairn's record of the Vintage Story login, shared by every pack so that having
    /// separate data paths does not mean signing in separately.
    /// </summary>
    public static string SessionPath => Path.Combine(Root, "session.json");

    /// <summary>Cairn's own preferences, as opposed to any pack's.</summary>
    public static string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>
    /// When Cairn last asked whether there was a newer version, and which one it has
    /// already mentioned.
    ///
    /// Its own file rather than a corner of settings.json, because that one is written
    /// whole: UiScale.Save serialises every key it knows about and moves the result into
    /// place, so anything else living there would be dropped the next time somebody
    /// changed the interface size. This is bookkeeping rather than a preference anyway.
    /// </summary>
    public static string UpdateStatePath => Path.Combine(Root, "updates.json");

    /// <summary>
    /// The token this machine holds for cairns.gg. Kept apart from settings because it is
    /// a credential rather than a preference, and written owner-only for the same reason.
    /// </summary>
    public static string AuthPath => Path.Combine(Root, "auth.json");

    /// <summary>This pack's game data path — worlds, mod configs and settings.</summary>
    public static string DataDir(string id) => Path.Combine(PackDir(id), "data");

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
