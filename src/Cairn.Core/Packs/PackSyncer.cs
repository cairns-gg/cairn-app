using System.Security.Cryptography;
using System.Text.Json;
using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

public enum SyncAction { Unchanged, Downloaded, Updated, Removed, Failed, Warned }

public sealed record SyncStep(SyncAction Action, string ModId, string Detail);

/// <summary>A mod that has moved on since the pack last installed it.</summary>
public sealed record ModUpdate(string ModId, string From, string To)
{
    public string Describe() => $"{ModId} {From} -> {To}";
}

public sealed record SyncReport(List<SyncStep> Steps, PackLock Lock)
{
    public bool Failed => Steps.Any(s => s.Action == SyncAction.Failed);
    public IEnumerable<SyncStep> Warnings => Steps.Where(s => s.Action == SyncAction.Warned);
}

/// <summary>
/// Brings a directory of mod zips in line with a pack manifest.
///
/// Mods are left as .zip — Vintage Story loads zipped mods directly (ModLoader.CollectMods
/// takes a FileSystemInfo), so there is never a reason to unpack them.
/// </summary>
public sealed class PackSyncer(ModDbClient moddb, HttpClient http)
{
    /// <param name="modsDir">Directory handed to the game via --addModPath.</param>
    /// <param name="allowUpdates">
    /// Mod ids permitted to move to a newer release. Empty by default, which is the whole
    /// point: syncing installs what the lockfile already says, so launching cannot change
    /// the mods underneath a save. Updating is something you ask for.
    /// </param>
    /// <param name="side">
    /// Which side this copy is being installed for. Changes what gets warned about and
    /// nothing else: every mod the lock names is installed either way, because the lock is
    /// the promise that a pack reproduces exactly, and a copy that quietly held fewer mods
    /// than it says would make that promise false on the machine most likely to be
    /// compared against.
    /// </param>
    public async Task<SyncReport> SyncAsync(
        PackManifest manifest,
        string modsDir,
        string lockPath,
        IProgress<SyncStep>? progress = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowUpdates = null,
        ModSide side = ModSide.Client)
    {
        // Only the pack-level problems stop everything. A missing id or an unusable game
        // version means nothing can be installed at all; one bad mod entry means one mod
        // cannot be, and throwing for that took a whole pack down over a single row —
        // recoverable only by hand-editing pack.json, which is not a thing to ask of
        // somebody who clicked Add on a search result.
        var problems = manifest.ValidatePack().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException("Pack manifest is invalid:\n  " + string.Join("\n  ", problems));

        Directory.CreateDirectory(modsDir);

        var steps = new List<SyncStep>();
        var previous = PackLock.Load(lockPath);
        var newLock = new PackLock { GameVersion = manifest.GameVersion };

        void Record(SyncStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        // Reported and skipped rather than installed or thrown over.
        var unusable = manifest.ModProblems().ToList();
        var skip = unusable.Select(p => p.Mod).ToHashSet();

        var usable = manifest.Mods.Where(m => !skip.Contains(m)).ToList();

        // Dependencies live inside the zip, so the full set is not known until things have
        // been downloaded. The queue is seeded from the manifest and grows as each mod's
        // modinfo.json is read; `seen` both dedupes and terminates it, including for two
        // libraries that declare each other.
        var queue = new Queue<PendingMod>(
            usable.Select(m => new PendingMod(m.ModId, m.Version, AcceptedFor: m.AcceptedFor)));
        var seen = new HashSet<string>(
            usable.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);

        // Accumulated rather than written straight onto the lock entry, because a library
        // can be pulled in by several mods and is only discovered as each is processed.
        var requiredBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Which mods may move. Seeded with what the caller asked to update, then inherited
        // by dependencies: updating carryon is meaningless if carryonlib stays behind, and
        // the newer mod is usually the reason the newer library is needed at all. Read at
        // dequeue rather than at enqueue, so a second requirer that turns up while the
        // library is still queued can still free it to move.
        var movable = new HashSet<string>(
            allowUpdates ?? (IEnumerable<string>)[], StringComparer.OrdinalIgnoreCase);

        foreach (var (mod, problem) in unusable)
            Record(new SyncStep(SyncAction.Failed,
                string.IsNullOrWhiteSpace(mod.ModId) ? "(no modid)" : mod.ModId, problem));

        while (queue.Count > 0)
        {
            var pending = queue.Dequeue();
            var installed = await InstallAsync(pending, movable.Contains(pending.ModId))
                .ConfigureAwait(false);
            if (installed is null) continue;

            var carriesUpdates = movable.Contains(pending.ModId);

            var declared = ModDependencies.Read(installed);

            // Warned, not Failed: the mod itself installed and the pack is usable. What is
            // not usable is the silence — without this, a dependency Cairn could not see is
            // indistinguishable from a mod that has none, right up until the game disables
            // it on startup for something the user was never told about.
            if (declared.Problem is not null)
                Record(new SyncStep(SyncAction.Warned, pending.ModId, declared.Problem));

            foreach (var dep in declared.Dependencies)
            {
                if (!requiredBy.TryGetValue(dep, out var wanters))
                    requiredBy[dep] = wanters = [];

                if (!wanters.Contains(pending.ModId, StringComparer.OrdinalIgnoreCase))
                    wanters.Add(pending.ModId);

                if (carriesUpdates) movable.Add(dep);
                if (seen.Add(dep)) queue.Enqueue(new PendingMod(dep, null, pending.ModId));
            }
        }

        // A mod the manifest names is not "required by" anything, however many other mods
        // also happen to want it — it is there because the pack asked for it.
        var direct = usable.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var locked in newLock.Mods)
        {
            if (direct.Contains(locked.ModId)) continue;
            if (requiredBy.TryGetValue(locked.ModId, out var wanters) && wanters.Count > 0)
                locked.RequiredBy = [.. wanters.Order(StringComparer.OrdinalIgnoreCase)];
        }

        // Anything in the directory we did not just account for is no longer part of the
        // pack. Every kind of file Cairn installs, not only .zip: the set here and the set
        // ModFileName will let through are the same set on purpose, because a file Cairn
        // could write and could not sweep would sit in the mod path for ever, invisible to
        // the lock and to everything that reads it.
        //
        // Still files only, so a hand-dropped folder mod is left alone, and still only the
        // kinds Cairn installs, so a readme or a config somebody parked here is not ours
        // to delete.
        var keep = newLock.Mods.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stray in Directory.EnumerateFiles(modsDir))
        {
            var name = Path.GetFileName(stray);

            if (keep.Contains(name) || !ModFileName.HasModExtension(name)) continue;

            File.Delete(stray);
            Record(new SyncStep(SyncAction.Removed, Path.GetFileNameWithoutExtension(stray), "no longer in pack"));
        }

        newLock.Save(lockPath);
        return new SyncReport(steps, newLock);

        // Installs one mod and returns where it landed, or null if it did not. Returning
        // the path is what lets the caller read its dependencies — which has to happen for
        // an already-installed mod too, or removing a mod would never reveal that the
        // library it pulled in has become an orphan.
        async Task<string?> InstallAsync(PendingMod want, bool mayUpdate)
        {
            ct.ThrowIfCancellationRequested();

            var prior = previous?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, want.ModId, StringComparison.OrdinalIgnoreCase));

            // The lock decides, unless it cannot: a mod never installed, a pin that has
            // moved, a pack retargeted at another game version, or an explicit update.
            var lockApplies = prior is not null
                              && !mayUpdate
                              && string.Equals(previous!.GameVersion, manifest.GameVersion,
                                  StringComparison.OrdinalIgnoreCase)
                              && (want.Version is null || want.Version == prior.Version);

            // A lock may say WHAT to install, but only ModDB says WHERE it comes from.
            // Somebody else's lock now arrives with those fields already cleared — see
            // PackLock.ClearResolvedLocations, which is where that rule lives, because a
            // host allowlist cannot tell a URL ModDB gave us from one an attacker uploaded
            // to the same host. Anyone may upload a mod, so anyone may put a file there.
            //
            // What is left here is the second question rather than the first: a URL that
            // survives to this point was written by a previous sync on this machine, and
            // the check is that ModDB has not since moved its downloads somewhere else. An
            // empty URL — the shape an imported lock has — fails it, which is precisely
            // what sends the entry to a fresh resolve pinned to the author's version.
            var lockUrlUsable = prior is not null && ModDbUrls.IsKnownDownloadHost(prior.Url);

            ResolvedRelease? release;

            if (lockApplies && lockUrlUsable)
            {
                release = FromLock(prior!);
            }
            else
            {
                // Pinned to what the lock already chose when the lock still applies, so
                // an untrusted URL costs a lookup rather than a different mod version.
                var wanted = lockApplies ? prior!.Version : want.Version;

                // Only for a mod whose manifest entry carries an acceptance covering this
                // pack's version. Everything else resolves as it always has, so a mod that
                // has simply not caught up still fails loudly rather than being installed
                // on a guess.
                var accepted = want.AcceptsUnmarkedFor(manifest.GameVersion);

                try
                {
                    release = await moddb.ResolveAsync(
                            want.ModId, manifest.GameVersion, wanted, ct, acceptUnmarked: accepted)
                        .ConfigureAwait(false);
                }
                // JsonException as well as the two the client raises deliberately: a mod
                // whose ModDB entry cannot be read must fail like any other unresolvable
                // mod. Escaping here aborts the run before the lock is written, leaving
                // downloaded zips that nothing accounts for and a pack that re-downloads
                // them and dies in the same place on every retry.
                catch (Exception e) when (e is ModDbException or HttpRequestException or JsonException)
                {
                    Record(new SyncStep(SyncAction.Failed, want.ModId, Explain(want, e.Message)));
                    return null;
                }

                if (release is null)
                {
                    // Named separately when an acceptance exists but is for another minor:
                    // "no release marked for 1.23.0" is true and says nothing about the
                    // note sitting in the manifest that used to make this work.
                    var stale = !accepted && !string.IsNullOrWhiteSpace(want.AcceptedFor)
                        ? $"; it was accepted for game {want.AcceptedFor}, and this pack has "
                          + "moved to a different release series since"
                        : "";

                    Record(new SyncStep(SyncAction.Failed, want.ModId, Explain(want,
                        $"no release marked for game {manifest.GameVersion}{stale}")));
                    return null;
                }
            }

            // ModDB answers to more than one id per mod, so a pack naming an alias and a
            // dependency naming the declared id are the same mod. Registering both stops
            // the second one being installed again over the top of the first, under a
            // different name, in the same file.
            if (!string.IsNullOrWhiteSpace(release.DeclaredModId))
                seen.Add(release.DeclaredModId);

            if (!lockApplies && release.Quality == MatchQuality.SameMinor)
                Record(new SyncStep(SyncAction.Warned, want.ModId,
                    $"{release.ModVersion} is not marked for {manifest.GameVersion} exactly, "
                    + "only for another release in that minor series"));

            // Every sync, and regardless of whether the lock applied — unlike the
            // same-minor note above, which is about a choice being made now. This one is
            // about what the pack is running, and a pack leaning on an untested
            // combination should say so every time somebody looks, not once when it was
            // added and never again.
            if (release.Quality == MatchQuality.Unmarked)
                Record(new SyncStep(SyncAction.Warned, want.ModId,
                    $"{release.ModVersion} is marked for "
                    + $"{DescribeVersions(release.GameVersions)}, not {manifest.GameVersion} — "
                    + "installed because the pack accepts it, and it may misbehave"));

            if (ModSides.WrongSide(release.Side, side))
                Record(new SyncStep(SyncAction.Warned, want.ModId,
                    $"ModDB marks this as {release.Side}-side; installing it "
                    + $"{ModSides.Describe(side)} may do nothing"));

            // Reduced to a name a pack may hold before it touches the filesystem: this
            // arrives from a remote API, and Path.Combine with "../../evil.zip" would
            // happily escape the pack. PackStore.PackDir guards ids for the same reason.
            if (ModFileName.Problem(release.FileName) is { } badName)
            {
                Record(new SyncStep(SyncAction.Failed, release.ModId,
                    $"refusing a mod filename that {badName}: '{release.FileName}'"));
                return null;
            }

            var safeName = release.FileName;

            var target = Path.Combine(modsDir, safeName);

            var locked = new LockedMod
            {
                ModId = release.ModId,
                Version = release.ModVersion,
                FileName = safeName,
                Url = release.Url,
                ReleaseId = release.ReleaseId,
                FileId = release.FileId,
                Side = release.Side,

                // Only when it does not match, so the field is absent for every ordinary
                // mod and means something wherever it appears.
                MarkedFor = release.Quality == MatchQuality.Unmarked
                    ? release.GameVersions?.ToList()
                    : null,
            };

            var upToDate = File.Exists(target)
                           && prior is not null
                           && prior.Version == release.ModVersion
                           && prior.Sha256.Length > 0
                           && await Sha256Async(target, ct).ConfigureAwait(false) == prior.Sha256;

            if (upToDate)
            {
                locked.Sha256 = prior!.Sha256;
                newLock.Mods.Add(locked);
                Record(new SyncStep(SyncAction.Unchanged, release.ModId, release.ModVersion));
                return target;
            }

            try
            {
                await DownloadAsync(release.Url, target, ct).ConfigureAwait(false);
                locked.Sha256 = await Sha256Async(target, ct).ConfigureAwait(false);

                // When a lock already pins this exact version — most importantly one that
                // arrived with a shared pack — the bytes must match. Otherwise the pack is
                // not reproducing what its author had.
                if (prior is not null
                    && prior.Version == release.ModVersion
                    && prior.Sha256.Length > 0
                    && !string.Equals(prior.Sha256, locked.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    Record(new SyncStep(SyncAction.Failed, release.ModId,
                        $"{release.ModVersion} does not match the locked checksum — refusing it"));
                    return null;
                }

                newLock.Mods.Add(locked);

                var action = prior is null ? SyncAction.Downloaded : SyncAction.Updated;
                var detail = prior is null || prior.Version == release.ModVersion
                    ? release.ModVersion
                    : $"{prior.Version} -> {release.ModVersion}";
                Record(new SyncStep(action, release.ModId, detail));
                return target;
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                Record(new SyncStep(SyncAction.Failed, release.ModId, e.Message));
                return null;
            }
        }

        // A mod nobody asked for, failing by its id alone, is a puzzle: the user has never
        // heard of it. Naming who wanted it turns the message into something actionable.
        string Explain(PendingMod want, string message) =>
            want.RequiredBy is null ? message : $"{message} (required by {want.RequiredBy})";
    }

    /// <summary>
    /// A mod waiting to be installed: from the manifest, or discovered inside another mod's
    /// <c>modinfo.json</c>. A dependency carries no version — the declared one is a minimum
    /// rather than a pin, so it is not something to resolve against.
    /// </summary>
    /// <param name="AcceptedFor">
    /// The game version this mod's manifest entry was accepted for, when it carries one.
    /// Never set for a dependency: an acceptance is somebody saying they ran a particular
    /// mod, and nobody said that about something a zip asked for on its way in.
    /// </param>
    private sealed record PendingMod(
        string ModId, string? Version, string? RequiredBy = null, string? AcceptedFor = null)
    {
        public bool AcceptsUnmarkedFor(string gameVersion) =>
            new PackMod { ModId = ModId, AcceptedFor = AcceptedFor }.AcceptsUnmarkedFor(gameVersion);
    }

    /// <summary>
    /// The game versions a release claims, for a warning somebody has to act on.
    ///
    /// Named rather than counted: "marked for 1.21.4" says how far behind the mod is and
    /// lets you judge whether you believe it, where "not marked for your version" only
    /// repeats what the warning already said. Truncated because ModDB entries routinely
    /// list a dozen.
    /// </summary>
    private static string DescribeVersions(IReadOnlyList<string>? versions)
    {
        if (versions is null || versions.Count == 0) return "no game version";

        var shown = versions.Take(3).ToList();
        var rest = versions.Count - shown.Count;

        return string.Join(", ", shown) + (rest > 0 ? $" and {rest} more" : "");
    }

    /// <summary>
    /// Treats a lock entry as a resolved release. Everything needed to install it is
    /// already recorded, so a fully-synced pack launches without touching ModDB at all.
    /// </summary>
    private static ResolvedRelease FromLock(LockedMod locked) =>
        new(locked.ModId, locked.Version, locked.FileName, locked.Url,
            locked.ReleaseId, locked.FileId,
            // Exact unless the lock says otherwise. A release recorded as marked for
            // something else was installed on somebody's say-so, and installing it again
            // from the lock is the same act — so it arrives here as what it is, rather
            // than laundered into a match by having been written down once.
            locked.MarkedFor is null ? MatchQuality.Exact : MatchQuality.Unmarked,
            locked.Side, null, locked.MarkedFor);

    /// <summary>
    /// What each following mod would move to if updated. Mods pinned to an exact version
    /// are skipped — a pin is an instruction to stay put, not a thing to nag about.
    /// </summary>
    /// <param name="cache">
    /// Remembers the answer for a few minutes. Passed in rather than created here so the
    /// caller decides where it lives, and so a caller that wants a live answer can pass
    /// nothing — but both front-ends pass one, because the alternative is thirty ModDB
    /// requests to be told the same thing twice. See <see cref="ModUpdateCache"/>.
    /// </param>
    /// <param name="force">Ignores a remembered answer, and replaces it with a fresh one.</param>
    public async Task<List<ModUpdate>> CheckUpdatesAsync(
        PackManifest manifest,
        string lockPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        ModUpdateCache? cache = null,
        bool force = false)
    {
        var locks = PackLock.Load(lockPath);

        var fingerprint = cache is null ? "" : ModUpdateCache.Fingerprint(manifest, locks);

        if (cache is not null && !force
            && cache.Get(manifest.Id, fingerprint, DateTimeOffset.UtcNow) is { } remembered)
            return remembered;

        var updates = new List<ModUpdate>();

        foreach (var want in manifest.Mods)
        {
            ct.ThrowIfCancellationRequested();
            if (want.Version is not null) continue;

            progress?.Report(want.ModId);

            var installed = locks?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, want.ModId, StringComparison.OrdinalIgnoreCase));

            ResolvedRelease? newest;
            try
            {
                newest = await moddb.ResolveAsync(want.ModId, manifest.GameVersion, null, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is ModDbException or HttpRequestException or JsonException)
            {
                continue;   // unreachable today says nothing about whether an update exists
            }

            if (newest is null) continue;

            // A mod not installed yet is not an update; the next sync will fetch it.
            if (installed is null) continue;

            if (!string.Equals(installed.Version, newest.ModVersion, StringComparison.OrdinalIgnoreCase))
                updates.Add(new ModUpdate(want.ModId, installed.Version, newest.ModVersion));
        }

        // Only a run that finished. A check cancelled halfway has looked at some mods and
        // not others, so remembering it would report "no updates" for the ones it never
        // reached, for as long as the answer stands.
        cache?.Save(manifest.Id, fingerprint, updates, DateTimeOffset.UtcNow);

        return updates;
    }

    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        // Download beside the target then move, so an interrupted sync never leaves a
        // truncated zip that the game would try to load.
        var tmp = target + ".partial";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
