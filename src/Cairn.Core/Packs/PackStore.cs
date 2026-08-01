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
    public PackManifest Import(PackBundle bundle, string? asId = null, bool reproduce = true)
    {
        var manifest = bundle.Pack
                       ?? throw new InvalidDataException("The bundle has no pack.");

        if (!string.IsNullOrWhiteSpace(asId)) manifest.Id = asId.Trim();

        var problem = DescribeIdProblem(manifest.Id);
        if (problem is not null) throw new InvalidOperationException(problem);

        if (!reproduce) bundle.ClearPins();

        Save(manifest);
        Directory.CreateDirectory(DataDir(manifest.Id));

        // The author's lock is what reproduces their set: sync installs from it and checks
        // the download against their SHA-256. Their manifest travels unchanged alongside
        // it, so mods they deliberately pinned stay pinned and the rest stay followed —
        // the recipient gets identical bytes now and is still offered updates later.
        if (reproduce) bundle.Lock?.Save(LockPath(manifest.Id));

        // A published pack arrives with an owner, and this copy follows theirs. Recorded
        // now, at the one moment it is knowable — without it the pack looks exactly like
        // one you made yourself, and Share would offer to publish somebody else's curation
        // under your name.
        //
        // A bundle from a file gets no link: nobody's URL is behind it, so there is
        // nothing to follow and nothing to take over.
        if (bundle.IsPublished)
            SaveLink(manifest.Id, new PackLink
            {
                Role = PackRole.Follower,
                Url = bundle.CanonicalUrl!,
                Revision = bundle.Revision ?? 0,
                Following = true,
            });

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
