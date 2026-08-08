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

    /// <summary>
    /// Dedicated servers Cairn installed, one directory per version.
    ///
    /// Kept apart from <see cref="GamesRoot"/> rather than sharing it, though the store
    /// underneath is the same one. A server download and a client of the same version are
    /// different things wearing the same version number, and a machine can hold both: the
    /// one somebody plays on, and the one a world is running out of. Sharing a directory
    /// would mean updating the client you play moves the server a world is live on, and a
    /// version installed for one purpose being deleted for the other.
    /// </summary>
    public static string ServersRoot => Path.Combine(Root, "servers");

    /// <summary>
    /// The socket a running server listens on for console commands, one per pack.
    ///
    /// Under CAIRN_HOME rather than /run, so that the same path is reached whether the
    /// server runs from a system unit, a user unit or a terminal — three answers for where
    /// a socket lives is three ways for "send this command" to find nothing. Short on
    /// purpose: a Unix socket path is limited to about 100 characters, and a pack id is
    /// already the only variable part.
    /// </summary>
    public static string ConsoleSocket(string packId) =>
        Path.Combine(Root, "run", packId + ".sock");

    /// <summary>.NET runtimes Cairn downloaded, so the game needs no system-wide install.</summary>
    public static string RuntimesRoot => Path.Combine(Root, "runtimes");

    /// <summary>Shareable pack files written by export.</summary>
    public static string ExportsRoot => Path.Combine(Root, "exports");

    /// <summary>
    /// Working trees for clients Cairn builds from source, one directory per build.
    ///
    /// Deliberately not under <see cref="CacheRoot"/> despite being reproducible: a build
    /// tree is several gigabytes and takes twenty minutes to recreate, so "delete this to
    /// free space" and "delete this, it costs nothing" want to be different directories.
    /// </summary>
    public static string BuildsRoot => Path.Combine(Root, "builds");

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
    /// When Cairn last asked whether there was a newer version — one Unix timestamp, in
    /// seconds, and nothing else.
    ///
    /// Its own file rather than a corner of settings.json, because that one is written
    /// whole: UiScale.Save serialises every key it knows about and moves the result into
    /// place, so anything else living there would be dropped the next time somebody
    /// changed the interface size.
    /// </summary>
    public static string LastUpdateCheckPath => Path.Combine(Root, "last-update-check");

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
