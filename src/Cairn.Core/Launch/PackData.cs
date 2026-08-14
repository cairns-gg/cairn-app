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
/// So every pack gets its own data path, and the session is merged in at launch instead.
///
/// Unconditionally: sharing a data path was briefly offered as a per-pack choice, for packs
/// created before this existed. But it is not a preference — it is the failure mode this
/// class exists to prevent, and offering it made it look like a supported way to run.
/// Packs from before simply get a data path on their next launch. Worlds already in the
/// shared path stay there, still reachable by launching Vintage Story normally: they are
/// the player's ordinary saves, and Cairn cannot know which pack, if any, they belong to.
/// </summary>
public sealed class PackData(PackStore store, string? sessionPath = null, string? sharedDataPath = null)
{
    /// <summary>Cairn's record of the login, shared by every pack.</summary>
    public string SessionPath { get; } = sessionPath ?? CairnPaths.SessionPath;

    /// <summary>The data path packs used before they had their own, and the seed for new ones.</summary>
    public string SharedDataPath { get; } = sharedDataPath ?? GameInstall.DefaultDataPath;

    private static string SettingsIn(string dataPath) => Path.Combine(dataPath, "clientsettings.json");

    /// <summary>
    /// Where this pack launches. Always its own directory.
    ///
    /// A pure read: cairn-cli launch --dry-run prints this without starting anything, so
    /// resolving a path must not create one. EnsureDataPath does the creating, from the
    /// launch path only.
    /// </summary>
    public string DataPathFor(string id) => store.DataDir(id);

    /// <summary>Whether the directory is there yet, which is not the same as which path is used.</summary>
    private bool Exists(string id) => Directory.Exists(store.DataDir(id));

    /// <summary>
    /// Creates the pack's data path if it has none yet, seeding settings once.
    ///
    /// Worlds in the shared path are deliberately not moved: they are the player's ordinary
    /// saves, and Cairn cannot know which pack — if any — they belong to. Taking them would
    /// remove them from plain Vintage Story too.
    /// </summary>
    public void EnsureDataPath(string id)
    {
        var data = store.DataDir(id);
        var target = SettingsIn(data);

        Directory.CreateDirectory(data);

        // Seeded once from whatever the player already uses, so a new pack starts with
        // their keybinds and graphics settings rather than bare defaults.
        //
        // Keyed off the settings file, not the directory. PackStore.Create makes the
        // directory itself — that is how a pack records that it has its own data path — so
        // the older guard here saw it and returned, and a pack created through the launcher
        // was never seeded at all.
        if (File.Exists(target)) return;

        var seed = SettingsIn(SharedDataPath);

        try
        {
            if (File.Exists(seed)) File.Copy(seed, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Starting from defaults is a worse first launch, not a failed one.
        }

        // The seed is for keybinds and graphics, not for who is signed in. The login
        // arrives on the next line of BeforeLaunch, from the one record that knows which
        // session is current — see ClientSession.Forget.
        ClientSession.Forget(target);

        // The seed names the player's own Mods folder, in absolute form. Left as copied,
        // every pack would load it alongside its own — see ClientModPaths. Done here and
        // not reported: this pack has never been launched, so nothing has been loaded from
        // there to stop loading. Writes the setting even when there was no seed to copy,
        // which is what keeps the game from choosing the list itself.
        ClientModPaths.Confine(target, data);
    }

    /// <summary>
    /// Makes sure the pack has a data path and puts the current login into it, so a pack
    /// never asks you to sign in again.
    /// </summary>
    /// <returns>
    /// Mod directories dropped from the pack's settings because they belong to something
    /// else, so the caller can say a launch stopped loading them. Empty on every launch but
    /// the first after this became a rule, which is why it is a return value rather than a
    /// warning somebody has to dismiss.
    /// </returns>
    /// <param name="bound">
    /// Receives the hotkeys taken from the pack, which is how a launch says it changed the
    /// keyboard instead of doing it quietly. See <see cref="ClientHotkeys"/>.
    ///
    /// Purely for reporting, and optional for that reason alone — the bindings are applied
    /// either way. This was briefly a second overload with the hotkey work inside the
    /// <c>bound is not null</c> branch, which made whether a pack's keyboard arrived depend
    /// on whether the caller wanted to print about it. Two front ends passed one; a third
    /// calling the obvious overload would have launched without the pack's hotkeys and
    /// said nothing.
    /// </param>
    /// <param name="config">
    /// Receives what became of the mod config values the pack declares — both the ones
    /// written and the ones left alone because somebody had changed them since. Optional for
    /// the same reason and with the same caveat as <paramref name="bound"/>, and reporting
    /// matters more here: this writes into files belonging to other people's mods, and half
    /// of what it does is decline to.
    /// </param>
    public IReadOnlyList<string> BeforeLaunch(
        string id, ICollection<string>? bound = null, ICollection<ModConfigChange>? config = null)
    {
        EnsureDataPath(id);

        // Again here, not only on seeding: a pack made before this existed still carries
        // the player's own Mods folder in its settings, and the launch is the only thing
        // that reaches into it.
        var dropped = ClientModPaths.Confine(SettingsIn(store.DataDir(id)), store.DataDir(id));

        // Take the newest login on the machine first. The command line does not wait for
        // the game to exit, so signing in inside one pack would otherwise never reach the
        // others; this notices it on the next launch instead.
        CaptureLatest();

        ClientSession.Load(SessionPath).MergeInto(SettingsIn(store.DataDir(id)));

        // After the session merge rather than before, so both write into one settled file.
        // Read from disk rather than taken as an argument: this is the launch, and the
        // manifest on disk is what the pack currently declares.
        try
        {
            var manifest = store.Load(id);

            foreach (var code in ClientHotkeys.Apply(SettingsIn(store.DataDir(id)), manifest.Keybinds))
                bound?.Add(code);

            // Before the pack's own values go in, so what an author is later shown as their
            // own change is not something Cairn wrote a moment earlier.
            ModConfigFiles.Capture(store.DataDir(id));

            // Runs whether or not the manifest declares any, because the record of what the
            // pack last asked for has to be brought up to date when the answer is "nothing
            // any more" — that is what stops a removed value being reported forever.
            foreach (var change in ModConfigFiles.Apply(store.DataDir(id), manifest.ModConfig))
                config?.Add(change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException)
        {
            // A pack whose manifest will not load has bigger problems, and every one of
            // them is reported somewhere a launch already looks.
        }

        return dropped;
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
            if (Exists(id))
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
        if (!Exists(id)) return;

        var played = ClientSession.ReadFrom(SettingsIn(store.DataDir(id)));
        if (!played.IsEmpty) played.Save(SessionPath);

        // Here as well as on the way in, because the first launch of a pack is exactly the
        // one where the mods' config files do not exist yet when it starts. This is the
        // first moment they can be seen at all, and a baseline recorded now is what lets the
        // Mod config tab tell an author's edits from what the mod ships.
        ModConfigFiles.Capture(store.DataDir(id));
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
