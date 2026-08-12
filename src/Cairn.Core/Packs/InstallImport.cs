using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

/// <summary>What importing one installed mod would do, worst news first when sorted.</summary>
public enum ImportVerdict
{
    /// <summary>ModDB serves the exact release sitting in the folder. Nothing changes.</summary>
    Ready,

    /// <summary>
    /// The version installed is not one ModDB will serve for this game version, but a newer
    /// one is. The pack takes the newer one — which is a different mod set than the one
    /// being imported, so it is its own verdict rather than a footnote on <see cref="Ready"/>.
    /// </summary>
    Newest,

    /// <summary>
    /// The release is marked for no version like the pack's, and is being imported on the
    /// strength of the player already running it. Recorded as an acceptance in the manifest,
    /// which is where that testimony belongs.
    /// </summary>
    Accepted,

    /// <summary>The zip would not say what mod it is, so nothing can be looked up.</summary>
    Unreadable,

    /// <summary>A second zip claiming a mod id another one already claimed.</summary>
    Duplicate,

    /// <summary>Switched off in Vintage Story, so it is not part of what is being played.</summary>
    Disabled,

    /// <summary>ModDB has no mod under this id — a private build, or one since taken down.</summary>
    Unknown,

    /// <summary>On ModDB, with nothing published for the game version the pack targets.</summary>
    Incompatible,

    /// <summary>ModDB could not be reached about this one. Says nothing about the mod.</summary>
    Unreachable,
}

/// <summary>One installed mod and what the import made of it.</summary>
/// <param name="Release">
/// What would be locked, for the verdicts that install something. Null otherwise, and also
/// null for <see cref="ImportVerdict.Newest"/> — there is nothing to reproduce there, so the
/// pack names the mod and lets the next sync resolve it.
/// </param>
public sealed record ImportCandidate(
    InstalledMod Mod, ImportVerdict Verdict, ResolvedRelease? Release, string Note)
{
    /// <summary>The mod id this would be added under, which is what the zip called itself.</summary>
    public string ModId => Mod.ModId ?? "";

    public bool Included => Verdict is ImportVerdict.Ready or ImportVerdict.Newest
        or ImportVerdict.Accepted;
}

/// <summary>
/// Turns a folder of installed mods into a pack.
///
/// The rule the whole thing hangs on: a pack records what you are *running*, so an import
/// takes the versions in the folder — but it does not pin them. A pin means "stay here", and
/// nobody choosing Import from install has said that; they have said "start me where I am".
/// So the manifest names the mods and nothing more, and the exact releases go into the
/// lockfile, which is what sync installs from. Updating stays opt-in and one button away,
/// exactly as it is for every other pack.
///
/// Mods ModDB cannot serve are left out and reported one by one. Copying the zip in would be
/// the other answer, and a worse one: a pack whose mods come from a folder on one machine
/// cannot be shared, published, or reproduced by anyone, which is most of what a pack is for.
/// </summary>
public sealed class InstallImport(ModDbClient moddb)
{
    /// <summary>
    /// Works out what each installed mod would become, without writing anything.
    ///
    /// One ModDB request per mod in the ordinary case — the mod's own page carries every
    /// release, so finding the installed version among them costs nothing extra. A mod whose
    /// installed version has no place in this game version costs a second, to ask whether
    /// somebody has simply been running it anyway.
    /// </summary>
    /// <param name="disabled">
    /// Mod ids and filenames switched off in the game's settings; see
    /// <see cref="InstalledMods.DisabledIn"/>. Null imports everything found.
    /// </param>
    /// <param name="playedOn">
    /// The game version this folder was actually being played on, which is not always the
    /// one the pack targets — importing an install while moving to a newer game is an
    /// ordinary thing to do.
    ///
    /// It decides whether an unmarked release may be imported as accepted. "You are running
    /// it" is testimony about a game version: someone running a 1.21.4-only mod on 1.21.4
    /// has said nothing whatever about 1.22.6, and inheriting the acceptance would put an
    /// untested mod in a pack over a sentence nobody said. Null grants none.
    /// </param>
    /// <param name="decided">
    /// Each mod as it is settled, so a caller can show the folder immediately and fill the
    /// verdicts in behind it. Reading the zips is instant; only the lookups take time, and
    /// holding a list of somebody's own mods back for them made it look as though finding
    /// them were the slow part.
    /// </param>
    public async Task<IReadOnlyList<ImportCandidate>> PlanAsync(
        InstalledModScan scan,
        string gameVersion,
        IReadOnlySet<string>? disabled = null,
        string? playedOn = null,
        IProgress<ImportCandidate>? decided = null,
        CancellationToken ct = default)
    {
        var candidates = new List<ImportCandidate>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in scan.Mods)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = await JudgeAsync(mod, gameVersion, disabled, playedOn, claimed, ct)
                .ConfigureAwait(false);

            candidates.Add(candidate);
            decided?.Report(candidate);
        }

        return candidates;
    }

    private async Task<ImportCandidate> JudgeAsync(
        InstalledMod mod, string gameVersion, IReadOnlySet<string>? disabled, string? playedOn,
        HashSet<string> claimed, CancellationToken ct)
    {
        if (mod.Problem is not null || string.IsNullOrWhiteSpace(mod.ModId))
            return new ImportCandidate(mod, ImportVerdict.Unreadable, null,
                mod.Problem ?? "it declares no mod id");

        if (disabled is not null && (disabled.Contains(mod.ModId) || disabled.Contains(mod.FileName)))
            return new ImportCandidate(mod, ImportVerdict.Disabled, null,
                "switched off in Vintage Story");

        // Two zips of the same mod — an old copy left behind beside the one being used. The
        // first wins, because that is the order the game itself resolves them in.
        if (!claimed.Add(mod.ModId))
            return new ImportCandidate(mod, ImportVerdict.Duplicate, null,
                $"another zip in the folder is already '{mod.ModId}'");

        List<ResolvedRelease> compatible;
        try
        {
            compatible = await moddb.ListCompatibleReleasesAsync(mod.ModId, gameVersion, ct)
                .ConfigureAwait(false);
        }
        catch (ModDbException e)
        {
            // ModDB answering "no such mod" and ModDB being unreadable arrive the same way.
            // Both leave the mod out; only one of them is worth retrying, so they are told
            // apart for the person reading the list rather than merged into "failed".
            return new ImportCandidate(mod, ImportVerdict.Unknown, null, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ImportCandidate(mod, ImportVerdict.Unreachable, null,
                "could not reach ModDB about this one");
        }

        var mine = mod.Version is null
            ? null
            : compatible.FirstOrDefault(r =>
                string.Equals(r.ModVersion, mod.Version, StringComparison.OrdinalIgnoreCase));

        if (mine is not null)
            return new ImportCandidate(mod, ImportVerdict.Ready, mine, mine.ModVersion);

        // Not published for this game version, or not published at all any more. Before
        // moving them to a different version, ask whether the release they have is simply
        // unmarked — they are running it, which is the same testimony an acceptance records.
        // Only when they were running it on a game like this pack's: see playedOn.
        if (mod.Version is not null && Testifies(playedOn, gameVersion)
            && await AcceptedAsync(mod, gameVersion, ct).ConfigureAwait(false) is { } unmarked)
            return new ImportCandidate(mod, ImportVerdict.Accepted, unmarked,
                $"{unmarked.ModVersion} is not marked for game {gameVersion}, and is being "
                + "imported because you are running it");

        var newest = compatible.FirstOrDefault();

        if (newest is null)
            return new ImportCandidate(mod, ImportVerdict.Incompatible, null,
                $"nothing published for game {gameVersion}");

        return new ImportCandidate(mod, ImportVerdict.Newest, null,
            mod.Version is null
                ? $"will install {newest.ModVersion}"
                : $"{mod.Version} is not available for game {gameVersion} — "
                  + $"will install {newest.ModVersion}");
    }

    /// <summary>
    /// Whether playing on one version says anything about running mods on another. The same
    /// rule <see cref="PackMod.AcceptsUnmarkedFor"/> applies to an acceptance already in a
    /// manifest — a patch bump is interchangeable for mods, a minor bump is not — because
    /// this is the same claim, made a moment earlier.
    /// </summary>
    private static bool Testifies(string? playedOn, string gameVersion) =>
        new PackMod { ModId = "", AcceptedFor = playedOn }.AcceptsUnmarkedFor(gameVersion);

    private async Task<ResolvedRelease?> AcceptedAsync(
        InstalledMod mod, string gameVersion, CancellationToken ct)
    {
        try
        {
            var release = await moddb
                .ResolveAsync(mod.ModId!, gameVersion, mod.Version, ct, acceptUnmarked: true)
                .ConfigureAwait(false);

            // Only when it really is the unmarked case. Anything else came back from the
            // compatible list already and was handled there.
            return release?.Quality == MatchQuality.Unmarked ? release : null;
        }
        catch (Exception e) when (e is ModDbException or HttpRequestException or TaskCanceledException)
        {
            // The version is not on ModDB at all, which the caller handles by moving to the
            // newest one. Nothing here is worth failing the import over.
            return null;
        }
    }

    /// <summary>
    /// Creates the pack the plan describes: a manifest naming every included mod, and a
    /// lockfile holding the exact releases found installed.
    ///
    /// The lock entries carry no checksum, because nothing has been downloaded yet. That is
    /// a state the syncer already knows — it verifies against a locked hash when there is
    /// one and records the hash it computed when there is not — so the first sync fetches
    /// precisely these releases and fills the rest in.
    /// </summary>
    public static PackManifest CreatePack(
        PackStore store, string id, string gameVersion, string? name,
        IEnumerable<ImportCandidate> plan)
    {
        var chosen = plan.Where(c => c.Included).ToList();

        var manifest = store.Create(id, gameVersion, name);

        manifest.Mods = [.. chosen.Select(c => new PackMod
        {
            ModId = c.ModId,

            // Unpinned, deliberately: the version lives in the lock, where sync reads it
            // from and the update button can still move it.
            Version = null,

            AcceptedFor = c.Verdict == ImportVerdict.Accepted ? gameVersion : null,
        })];

        store.Save(manifest);
        BuildLock(gameVersion, chosen).Save(store.LockPath(id));

        return manifest;
    }

    /// <summary>
    /// The lockfile a plan produces. Only the mods whose installed release ModDB will serve
    /// appear in it: a mod being moved to a newer version has nothing to reproduce, and
    /// locking the version it is moving away from would send the first sync to fetch it.
    /// </summary>
    public static PackLock BuildLock(string gameVersion, IEnumerable<ImportCandidate> plan) =>
        new()
        {
            GameVersion = gameVersion,
            Mods = [.. plan
                .Where(c => c.Included && c.Release is not null)
                .Select(c => new LockedMod
                {
                    ModId = c.ModId,
                    Version = c.Release!.ModVersion,
                    // Guarded here as well as in PackSyncer: this is a lock written
                    // straight from a remote API's idea of a filename, and it sits on disk
                    // being read by the diagnostics report long before any sync re-derives
                    // it. A name Cairn would refuse to install is recorded as no name at
                    // all rather than as something a reader might combine with a path.
                    FileName = ModFileName.Safe(c.Release.FileName) ?? "",
                    Url = c.Release.Url,
                    ReleaseId = c.Release.ReleaseId,
                    FileId = c.Release.FileId,
                    Side = c.Release.Side,

                    // Left empty on purpose: Cairn computes it on first download, and a hash
                    // taken from the player's own copy would describe bytes ModDB may not
                    // serve — which is exactly the mismatch the field exists to catch.
                    Sha256 = "",

                    MarkedFor = c.Verdict == ImportVerdict.Accepted
                        ? c.Release.GameVersions?.ToList()
                        : null,
                })],
        };
}
