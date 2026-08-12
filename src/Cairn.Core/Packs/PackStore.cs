using System.Text.Json;

namespace Cairn.Core.Packs;

/// <summary>
/// Owns the on-disk pack collection. Every mutation the UI or CLI performs goes through
/// here so validation and path handling live in one place.
/// </summary>
public sealed class PackStore
{
    private readonly string _packsRoot;

    public PackStore(string? packsRoot = null) => _packsRoot = packsRoot ?? CairnPaths.PacksRoot;

    public string PacksRoot => _packsRoot;

    public string ManifestPath(string id) => Path.Combine(PackDir(id), "pack.json");
    public string LockPath(string id) => Path.Combine(PackDir(id), "pack.lock.json");
    public string ModsDir(string id) => Path.Combine(PackDir(id), "Mods");

    /// <summary>Where this pack came from, or where it is published. See PackLink.</summary>
    public string LinkPath(string id) => Path.Combine(PackDir(id), "cairns.json");

    public PackLink? LoadLink(string id) => PackLink.Load(LinkPath(id));

    public void SaveLink(string id, PackLink link) => link.Save(LinkPath(id));

    /// <summary>
    /// The author's manifest as it stood at the revision this copy follows — the base an
    /// update is merged against, and never edited by the person holding it.
    ///
    /// Without it a follower's changes cannot be told from the author's. A mod present
    /// upstream and absent locally means either "they just added it" or "I took it out",
    /// and those want opposite outcomes: one is the whole point of updating, the other
    /// would silently undo a deliberate removal every time an update landed.
    ///
    /// Its own file rather than a corner of cairns.json, which is small, hand-readable and
    /// rewritten whole on every publish; a manifest living inside it would be noise there
    /// and would be lost by anything that wrote the link without knowing to preserve it.
    /// </summary>
    public string UpstreamPath(string id) => Path.Combine(PackDir(id), "upstream.json");

    /// <summary>
    /// The merge base, or null when there is none — a pack imported before this was
    /// recorded, or one that was never anybody else's.
    /// </summary>
    public PackManifest? LoadUpstream(string id)
    {
        try
        {
            return File.Exists(UpstreamPath(id)) ? PackManifest.Load(UpstreamPath(id)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            // An unreadable base is no base. The plan says so rather than guessing at
            // which of the two meanings a difference has.
            return null;
        }
    }

    public void SaveUpstream(string id, PackManifest manifest) =>
        manifest.Save(UpstreamPath(id));

    /// <summary>
    /// Decisions this machine has made about this pack. Never shared — see PackLocalState.
    /// </summary>
    public string LocalStatePath(string id) => Path.Combine(PackDir(id), "local.json");

    public PackLocalState LoadLocalState(string id) => PackLocalState.Load(LocalStatePath(id));

    public void SaveLocalState(string id, PackLocalState state) =>
        state.Save(LocalStatePath(id));

    /// <summary>
    /// Whether this copy still names exactly what its author's did at the revision it
    /// follows — same mods, same pins, same game version.
    ///
    /// Answered from the recorded base rather than by asking the server, so it costs
    /// nothing and can be read on every render. It says whether there is anything of yours
    /// to undo, which is a different question from whether the author has published since.
    ///
    /// A pack with no base cannot answer, and says no: claiming a match on no evidence
    /// would offer to relock a copy that has quietly diverged.
    /// </summary>
    public bool MatchesUpstream(string id)
    {
        var upstream = LoadUpstream(id);
        if (upstream is null) return false;

        PackManifest mine;
        try
        {
            mine = Load(id);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            return false;
        }

        if (!string.Equals(mine.GameVersion, upstream.GameVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        static string Key(PackMod m) => $"{m.ModId.ToLowerInvariant()}={m.Version ?? ""}";

        return mine.Mods.Select(Key).Order().SequenceEqual(upstream.Mods.Select(Key).Order());
    }

    /// <summary>
    /// Takes the author's newer revision, with the plan's answers applied.
    ///
    /// Writes four things and they have to agree: the merged manifest, the author's
    /// manifest as the base for next time, a lock that reproduces what it can, and the
    /// revision now being followed. Getting the base wrong is the one that compounds —
    /// every future merge is measured from it, so it must be the author's list and not
    /// the merged one, or a mod you removed reads as yours to remove again next time.
    /// </summary>
    /// <param name="bundle">The revision being taken, as served at the pack's URL.</param>
    public PackManifest ApplyUpdate(string id, PackUpdatePlan plan, PackBundle bundle)
    {
        var theirs = bundle.Pack
                     ?? throw new InvalidDataException("The update has no pack.");

        var merged = plan.Merge();
        merged.Id = id;

        Save(merged);
        SaveUpstream(id, theirs);
        MergeLock(id, merged, bundle.Lock);
        RecordDeclines(id, plan, merged);

        if (LoadLink(id) is { } link)
        {
            link.Revision = bundle.Revision ?? link.Revision;
            SaveLink(id, link);
        }

        return merged;
    }

    /// <summary>
    /// Remembers the mods this update was told to stop asking about, and forgets the ones
    /// that are back in the pack.
    ///
    /// Only what a person ticked. Taking the mod back clears any earlier decline with it,
    /// because putting a mod in by hand is a clearer statement than the box ever was — and
    /// leaving the record would mean removing it a second time went unmentioned for ever.
    /// </summary>
    private void RecordDeclines(string id, PackUpdatePlan plan, PackManifest merged)
    {
        var state = LoadLocalState(id);
        var before = state.DeclinedMods.Count;

        foreach (var change in plan.Changes)
        {
            if (change.CanSilence && change.Silence && !change.Take) state.Decline(change.ModId);
        }

        foreach (var mod in merged.Mods) state.Restore(mod.ModId);

        // Writing an empty file for the overwhelmingly common case — nobody declined
        // anything — would put a file in every pack directory to say nothing.
        if (before == 0 && state.DeclinedMods.Count == 0 && !File.Exists(LocalStatePath(id))) return;

        SaveLocalState(id, state);
    }

    /// <summary>
    /// The author's lock for their mods, and this copy's for the rest.
    ///
    /// Their lock is what makes the update reproduce their set rather than merely resemble
    /// it. Anything it does not cover — a mod only this copy has — keeps whatever was
    /// already recorded, so taking an update does not re-download the rest of the pack.
    ///
    /// Except across a game version change, where the old entries describe files chosen for
    /// a version nobody is targeting any more. Those are dropped rather than carried, and
    /// the next sync resolves them properly; keeping them would let a lock that matches on
    /// game version install a mod built for the previous one.
    /// </summary>
    private void MergeLock(string id, PackManifest merged, PackLock? theirs)
    {
        var mine = LoadLock(id);

        // Nothing to reproduce and nothing worth keeping: let sync build it from scratch.
        if (theirs is null && mine is null) return;

        // Their lock is their document, and an update is the second chance to plant one:
        // the plan diffs manifests, so an entry whose URL and hash moved underneath an
        // unchanged mod id would present as no change at all. Same rule as import, applied
        // here as well because this is the other way somebody else's lock entries reach
        // this machine. See PackLock.ClearResolvedLocations.
        theirs?.ClearResolvedLocations();

        var retargeted = mine is not null && !string.Equals(
            mine.GameVersion, merged.GameVersion, StringComparison.OrdinalIgnoreCase);

        var next = new PackLock { GameVersion = merged.GameVersion };

        var wanted = merged.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in theirs?.Mods ?? [])
            if (wanted.Contains(entry.ModId))
                next.Mods.Add(entry);

        if (!retargeted)
        {
            var covered = next.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in mine?.Mods ?? [])
                if (wanted.Contains(entry.ModId) && !covered.Contains(entry.ModId))
                    next.Mods.Add(entry);
        }

        next.Save(LockPath(id));
    }

    /// <summary>
    /// Records that the site no longer serves this pack: the address stays, the publish
    /// record goes.
    ///
    /// Dropping the record is the point rather than tidiness. Publishing refuses a
    /// revision identical to its predecessor — a revision differing from its predecessor
    /// in nothing but its number tells every follower there is an update and then has none
    /// for them — and that refusal is exactly wrong for a pack that is down, where
    /// republishing it unchanged is how its author brings it back. With nothing to compare
    /// against, there is nothing to refuse.
    ///
    /// Reached two ways: withdrawing from here, and finding out that somebody withdrew it
    /// from the site. One place for the mutation so both leave the pack in the same state.
    /// </summary>
    public void MarkWithdrawn(string id)
    {
        if (LoadLink(id) is not { } link) return;

        link.Published = null;
        link.Withdrawn = true;
        SaveLink(id, link);
    }

    /// <summary>
    /// Exactly what publishing this pack right now would send. Always carries the lock —
    /// a published pack is reproducible or it is not worth publishing.
    /// </summary>
    /// <param name="stripConnect">
    /// Leave the pack's server address out. Loading gives a fresh manifest each time, so
    /// clearing it here does not touch the file.
    /// </param>
    public string PublishedDocument(string id, bool stripConnect)
    {
        var manifest = Load(id);
        if (stripConnect) manifest.Connect = null;

        return PackBundle.Serialize(manifest, LoadLock(id));
    }

    /// <summary>The Share button's state for this pack. See <see cref="ShareState"/>.</summary>
    public ShareState ShareStateFor(string id)
    {
        var link = LoadLink(id);
        if (link?.Published is null) return ShareState.For(link, null);

        string? now;
        try
        {
            now = PublishedDocument(id, link.Published.Connect == "stripped");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // Unreadable pack: report it unchanged rather than inventing a difference and
            // inviting someone to publish over a good revision with a broken one.
            now = null;
        }

        return ShareState.For(link, now);
    }

    /// <summary>
    /// This pack's game data path — its worlds, mod configs and settings. Inside the pack
    /// because the pack is the instance: see PackData for why they are no longer shared.
    /// </summary>
    public string DataDir(string id) => Path.Combine(PackDir(id), "data");

    public string PackDir(string id)
    {
        // A pack id becomes a directory name and now arrives from a text box, so it is
        // validated rather than trusted. Without this, an id of "../../etc" would let a
        // pack write outside the store.
        if (!IsValidId(id))
            throw new ArgumentException(
                $"'{id}' is not a valid pack id. Use letters, digits, '-' and '_' only.", nameof(id));

        return Path.Combine(_packsRoot, id);
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Explains why an id is unusable, or null when it is fine.</summary>
    public string? DescribeIdProblem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Enter a pack id.";
        if (!IsValidId(id)) return "Use letters, digits, '-' and '_' only (max 64 characters).";
        if (Exists(id!)) return $"A pack called '{id}' already exists.";
        return null;
    }

    public bool Exists(string id) => IsValidId(id) && File.Exists(ManifestPath(id));

    /// <summary>
    /// The id a pack called <paramref name="name"/> would get: its slug, made unique
    /// against what is already here. Nobody is asked to invent a directory name.
    /// </summary>
    public string SuggestId(string? name) =>
        PackId.MakeUnique(PackId.FromOrFallback(name), Exists);

    public IEnumerable<string> ListIds()
    {
        if (!Directory.Exists(_packsRoot)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(_packsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (IsValidId(id) && File.Exists(Path.Combine(dir, "pack.json")))
                yield return id;
        }
    }

    public PackManifest Load(string id) => PackManifest.Load(ManifestPath(id));

    public PackLock? LoadLock(string id) => PackLock.Load(LockPath(id));

    public void Save(PackManifest manifest)
    {
        var problems = manifest.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join("\n", problems));

        manifest.Save(ManifestPath(manifest.Id));
        Directory.CreateDirectory(ModsDir(manifest.Id));
    }

    public PackManifest Create(
        string id, string gameVersion, string? name = null, string? connect = null,
        string? description = null)
    {
        if (Exists(id)) throw new InvalidOperationException($"Pack '{id}' already exists.");

        var manifest = new PackManifest
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            GameVersion = gameVersion,
            Connect = string.IsNullOrWhiteSpace(connect) ? null : connect,
        };

        Save(manifest);

        // Only new packs get their own data path. Doing this in Save would flip every
        // existing pack the first time its settings were edited, silently moving people
        // off the worlds they already have — the directory is the flag.
        Directory.CreateDirectory(DataDir(id));

        return manifest;
    }

    /// <summary>One-file representation of a pack, for sharing.</summary>
    public string Export(string id, bool includeLock = true) =>
        PackBundle.Serialize(Load(id), includeLock ? LoadLock(id) : null);

    /// <summary>
    /// Creates a pack from a shared bundle.
    /// </summary>
    /// <param name="asId">Override the author's id, e.g. when it collides with an existing pack.</param>
    /// <param name="reproduce">
    /// Keep the author's lock, so the first sync installs their exact versions and
    /// verifies the bytes. Set false for a loose import: the lock is discarded and every
    /// pin dropped, so the pack resolves newest-compatible instead.
    /// </param>
    /// <param name="sourceUrl">
    /// The address this document was actually fetched from, or null when it came out of a
    /// file. This — not anything the document says about itself — is what a follow
    /// relationship is recorded against whenever it exists.
    /// </param>
    /// <param name="intent">
    /// Whether this copy follows the author or starts a pack of your own. Null lets the
    /// answer follow from what can be verified: see the comment on the decision below.
    /// </param>
    public PackManifest Import(
        PackBundle bundle, string? asId = null, bool reproduce = true, string? sourceUrl = null,
        ImportIntent? intent = null)
    {
        var manifest = bundle.Pack
                       ?? throw new InvalidDataException("The bundle has no pack.");

        if (!string.IsNullOrWhiteSpace(asId)) manifest.Id = asId.Trim();

        var problem = DescribeIdProblem(manifest.Id);
        if (problem is not null) throw new InvalidOperationException(problem);

        if (!reproduce) bundle.ClearPins();

        Save(manifest);
        Directory.CreateDirectory(DataDir(manifest.Id));

        // The author's lock is what reproduces their set: sync resolves their exact
        // versions and checks the download against their SHA-256. Their manifest travels
        // unchanged alongside it, so mods they deliberately pinned stay pinned and the rest
        // stay followed — the recipient gets identical bytes now and is still offered
        // updates later.
        //
        // Stripped of where each mod came from first. See PackLock.ClearResolvedLocations:
        // the author says which mod at which version, ModDB says where it lives.
        if (reproduce)
        {
            bundle.Lock?.ClearResolvedLocations();
            bundle.Lock?.Save(LockPath(manifest.Id));
        }

        // Which of the two a published document became. Front-ends ask; this is what
        // happens when nobody said, and it follows from what can be verified rather than
        // from what the document asserts. Fetched from an address, following is the
        // default and that address is what gets followed. Handed over as a file, the only
        // address on offer is the document's own word — so it forks, because taking that
        // word unasked is what lets a file choose where a launcher checks back for ever.
        var decided = intent ?? (sourceUrl is not null ? ImportIntent.Follow : ImportIntent.Fork);

        // A followed pack has an owner, and this copy is in step with theirs. Recorded now,
        // at the one moment it is knowable — without it the pack looks exactly like one you
        // made yourself, and Share would offer to publish somebody else's curation under
        // your name.
        //
        // A fork deliberately gets none of this: no link and no merge base, because there
        // is nobody to reconcile with. That is the whole of what forking means here, and it
        // is the only way to get a copy that is yours to publish.
        if (bundle.IsPublished && decided == ImportIntent.Follow)
        {
            // The base for every future merge, recorded at the one moment it is certainly
            // the author's own: right now, before anybody has edited a line of it.
            SaveUpstream(manifest.Id, manifest);

            SaveLink(manifest.Id, new PackLink
            {
                Role = PackRole.Follower,

                // Where this came from, not where it says it came from — believing the
                // document's canonicalUrl would let a pack choose the address its updates
                // are fetched from ever after, which is the same trick as a lock choosing
                // a download URL and just as invisible.
                //
                // The claim is only reached for a document with no fetch behind it, and
                // only once somebody has been shown that address and chosen to follow it.
                // A claim a person approved is a different thing from a claim believed,
                // which is why front-ends must show the URL where they offer the choice.
                Url = PackUpdateCheck.PageUrl(sourceUrl ?? bundle.CanonicalUrl!),
                Revision = bundle.Revision ?? 0,
                Following = true,
            });
        }

        return manifest;
    }

    /// <summary>
    /// Removes the pack, its downloaded mods, and — for a pack with its own data path —
    /// its worlds, configs and settings.
    ///
    /// That last part is why callers must say what is about to go: a world made under this
    /// pack's mod set generally cannot be opened without it, so there is nothing kind about
    /// leaving it behind, but there is nothing recoverable about removing it either.
    /// </summary>
    public void Delete(string id)
    {
        var dir = PackDir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
