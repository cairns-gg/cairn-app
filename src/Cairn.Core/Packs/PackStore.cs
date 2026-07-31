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

    public PackManifest Create(string id, string gameVersion, string? name = null, string? connect = null)
    {
        if (Exists(id)) throw new InvalidOperationException($"Pack '{id}' already exists.");

        var manifest = new PackManifest
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
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
    /// <param name="pinToLock">
    /// Pin every mod to the version the author had installed, so the pack reproduces
    /// exactly. Set false to track newest-compatible instead.
    /// </param>
    public PackManifest Import(PackBundle bundle, string? asId = null, bool pinToLock = true)
    {
        var manifest = bundle.Pack
                       ?? throw new InvalidDataException("The bundle has no pack.");

        if (!string.IsNullOrWhiteSpace(asId)) manifest.Id = asId.Trim();

        var problem = DescribeIdProblem(manifest.Id);
        if (problem is not null) throw new InvalidOperationException(problem);

        if (pinToLock) bundle.PinToLock();

        Save(manifest);
        Directory.CreateDirectory(DataDir(manifest.Id));

        // Keep the author's lock so the first sync can verify it got identical files.
        bundle.Lock?.Save(LockPath(manifest.Id));

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
