using Cairn.Core.Packs;

namespace Cairn.Core.Launch;

/// <summary>
/// Decides which Vintage Story data path a pack launches with, and carries the login
/// across packs that have their own.
///
/// Packs used to share one data path so there would be only one login. That also shared
/// <c>Saves/</c>, so a world was reachable from every pack whatever its mods — and opening
/// a save against a different mod set is a leading way to ruin it. <c>ModConfig/</c>,
/// <c>Playerdata/</c> and <c>ModsByServer/</c> were shared for the same reason; all of them
/// are hardcoded under the data path by the game, with only <c>Logs</c> overridable.
///
/// So a pack gets its own data path, and the session is merged in at launch instead.
/// </summary>
public sealed class PackData(PackStore store, string? sessionPath = null, string? sharedDataPath = null)
{
    /// <summary>Cairn's record of the login, shared by every pack.</summary>
    public string SessionPath { get; } = sessionPath ?? CairnPaths.SessionPath;

    /// <summary>The data path packs used before they had their own, and the seed for new ones.</summary>
    public string SharedDataPath { get; } = sharedDataPath ?? GameInstall.DefaultDataPath;

    private static string SettingsIn(string dataPath) => Path.Combine(dataPath, "clientsettings.json");

    /// <summary>
    /// Whether this pack has its own data. The directory is the flag — no extra state
    /// file, and nothing machine-local smuggled into the manifest, which travels.
    /// </summary>
    public bool HasOwnData(string id) => Directory.Exists(store.DataDir(id));

    /// <summary>Where this pack launches: its own data if it has any, else the shared path.</summary>
    public string DataPathFor(string id) => HasOwnData(id) ? store.DataDir(id) : SharedDataPath;

    /// <summary>
    /// Gives a pack its own data path.
    ///
    /// Existing worlds are deliberately left where they are rather than moved: they belong
    /// to whoever made them, under a mod set Cairn cannot vouch for. Callers should say so
    /// — the worlds stay reachable by launching the game normally.
    /// </summary>
    public void EnableOwnData(string id)
    {
        var data = store.DataDir(id);
        if (Directory.Exists(data)) return;

        Directory.CreateDirectory(data);

        // Seeded once from whatever the player already uses, so a new pack starts with
        // their keybinds and graphics settings rather than bare defaults.
        var seed = SettingsIn(SharedDataPath);
        var target = SettingsIn(data);

        try
        {
            if (File.Exists(seed) && !File.Exists(target)) File.Copy(seed, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Starting from defaults is a worse first launch, not a failed one.
        }
    }

    /// <summary>
    /// Puts the current login into this pack's settings, so a pack with its own data path
    /// does not ask you to sign in again. Does nothing for a pack on the shared path,
    /// which already has the real settings file.
    /// </summary>
    public void BeforeLaunch(string id)
    {
        if (!HasOwnData(id)) return;

        // Take the newest login on the machine first. The command line does not wait for
        // the game to exit, so signing in inside one pack would otherwise never reach the
        // others; this notices it on the next launch instead.
        CaptureLatest();

        ClientSession.Load(SessionPath).MergeInto(SettingsIn(store.DataDir(id)));
    }

    /// <summary>
    /// Records the most recently written login found anywhere Cairn knows about — its own
    /// record, the shared data path, or any pack's.
    ///
    /// Newest-wins by file timestamp rather than first-found, because a session rotates
    /// while playing and an older copy would sign you out.
    /// </summary>
    public void CaptureLatest()
    {
        var best = ClientSession.Load(SessionPath);
        var bestAt = LastWritten(SessionPath);

        foreach (var candidate in CandidateSettings())
        {
            var at = LastWritten(candidate);
            if (at <= bestAt) continue;

            var session = ClientSession.ReadFrom(candidate);
            if (session.IsEmpty) continue;

            best = session;
            bestAt = at;
        }

        if (!best.IsEmpty) best.Save(SessionPath);
    }

    private IEnumerable<string> CandidateSettings()
    {
        // Read only. Cairn never writes to the player's own data path.
        yield return SettingsIn(SharedDataPath);

        foreach (var id in store.ListIds())
            if (HasOwnData(id))
                yield return SettingsIn(store.DataDir(id));
    }

    private static DateTime LastWritten(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Picks up a login made inside this pack, so signing in once works everywhere. The
    /// session can also rotate while playing, which is why this runs after every exit
    /// rather than only when the record is empty.
    /// </summary>
    public void AfterExit(string id)
    {
        if (!HasOwnData(id)) return;

        var played = ClientSession.ReadFrom(SettingsIn(store.DataDir(id)));
        if (!played.IsEmpty) played.Save(SessionPath);
    }

    /// <summary>
    /// Worlds under this pack's data path, for telling someone what deleting it costs.
    /// </summary>
    public IReadOnlyList<string> Worlds(string id)
    {
        try
        {
            var saves = Path.Combine(store.DataDir(id), "Saves");
            if (!Directory.Exists(saves)) return [];

            return Directory.GetFiles(saves, "*.vcdbs").Select(Path.GetFileNameWithoutExtension).ToList()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Bytes under this pack's data path, or 0 if it has none.</summary>
    public long DataSize(string id)
    {
        try
        {
            var data = store.DataDir(id);
            if (!Directory.Exists(data)) return 0;

            return new DirectoryInfo(data).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
