using Cairn.Core.ModDb;

namespace Cairn.Core.Packs;

/// <summary>One mod as it would be published.</summary>
public sealed record PublishMod(string ModId, string? Version, bool Pinned, bool OnModDb)
{
    /// <summary>"glassview 1.3.0" or "unchisel 1.2.0 (pinned)".</summary>
    public string Describe() =>
        Version is null ? ModId : $"{ModId} {Version}{(Pinned ? " " + Lang.Get("share-pinned-suffix") : "")}";
}

/// <summary>
/// What publishing a pack right now would send, and everything about it worth reading
/// first. Checked but uncommitted: this object is built to be looked at and thrown away by
/// Publish or Cancel, so nothing is uploaded that was not first shown.
///
/// Deliberately parallel to <see cref="VersionChangePlan"/> — same shape, same worst-first
/// habit — because they are the same kind of screen: a decision that is hard to take back.
/// </summary>
/// <param name="ModConfigValues">
/// How many mod settings the pack carries, counted across every file it names.
/// </param>
/// <param name="Keybinds">How many hotkeys it carries.</param>
public sealed record PublishPlan(
    string PackId,
    IReadOnlyList<PublishMod> Mods,
    string? Connect,
    bool LockCovers,
    string? LockProblem,
    int ModConfigValues = 0,
    int Keybinds = 0)
{
    /// <summary>
    /// Mods with nothing on ModDB. They resolve on the author's machine and are a dead
    /// entry on everyone else's, which is the most likely way a shared pack disappoints
    /// its recipient.
    /// </summary>
    public IEnumerable<PublishMod> Unresolvable => Mods.Where(m => !m.OnModDb);

    public bool AnythingUnresolvable => Unresolvable.Any();

    /// <summary>The pack carries a real server address, which publishing would disclose.</summary>
    public bool HasConnect => !string.IsNullOrWhiteSpace(Connect);

    /// <summary>
    /// Publishing is refused, rather than warned about, when the lockfile does not cover
    /// the manifest. Including the lock is the whole reproducibility claim; shipping a
    /// partial one is worse than shipping none.
    /// </summary>
    public bool CanPublish => LockCovers;

    public string Summary() => Mods.Count == 0
        ? Lang.Get("share-nothing-to-publish")
        : Lang.Plural("share-publishing-mods", Mods.Count, Mods.Count);

    /// <summary>
    /// The rest of what a pack is, named rather than counted up by whoever is about to press
    /// Publish.
    ///
    /// The mod list is on screen and the other three things a pack carries are not, so this
    /// is the only place they appear before they are sent. Not a diff against the last
    /// revision — nothing keeps the old document to compare against, only a fingerprint of
    /// it — so it says what is going, which is the question somebody has anyway the first
    /// time they publish.
    ///
    /// Empty when there is nothing but mods, which is most packs: a line reading "and no
    /// settings, and no hotkeys" is noise on the screen where it matters most.
    /// </summary>
    public string Carries()
    {
        var parts = new List<string>();

        if (ModConfigValues > 0)
            parts.Add(Lang.Plural("share-carries-settings", ModConfigValues, ModConfigValues));

        if (Keybinds > 0)
            parts.Add(Lang.Plural("share-carries-hotkeys", Keybinds, Keybinds));

        return parts.Count == 0 ? "" : Lang.Get("share-carries", string.Join(", ", parts));
    }

    public bool CarriesAnything => ModConfigValues > 0 || Keybinds > 0;

    public string UnresolvableWarning()
    {
        var n = Unresolvable.Count();
        return Lang.Plural("share-unresolvable", n, n,
            string.Join(", ", Unresolvable.Take(3).Select(m => m.ModId)) + (n > 3 ? ", …" : ""));
    }

    /// <summary>
    /// Works out what would be sent, without sending anything.
    /// </summary>
    /// <param name="moddb">
    /// Asked only whether each mod exists at all. Null skips the check and reports every
    /// mod as resolvable, which is the honest reading of "we did not look" — the alternative
    /// is a dialog that accuses mods of being missing because the network was down.
    /// </param>
    /// <param name="syncFailures">
    /// Steps from the sync that was just run, when the caller ran one. A mod missing from
    /// the lock is otherwise a puzzle — it reads identically whether nothing has tried to
    /// install it or something tried and could not — and these are what tell the two apart
    /// and carry the actual reason. Null means no sync was run, not that none failed.
    /// </param>
    public static async Task<PublishPlan> PrepareAsync(
        PackManifest manifest,
        PackLock? locked,
        ModDbClient? moddb = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IReadOnlyList<SyncStep>? syncFailures = null)
    {
        var mods = new List<PublishMod>();

        foreach (var want in manifest.Mods.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(want.ModId);

            var installed = locked?.Mods.FirstOrDefault(
                m => string.Equals(m.ModId, want.ModId, StringComparison.OrdinalIgnoreCase));

            var onModDb = true;
            if (moddb is not null)
            {
                try
                {
                    onModDb = await moddb.ExistsAsync(want.ModId, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is ModDbException or HttpRequestException)
                {
                    // Unreachable says nothing about whether the mod is published.
                    onModDb = true;
                }
            }

            mods.Add(new PublishMod(
                want.ModId, installed?.Version ?? want.Version, want.Version is not null, onModDb));
        }

        var (covers, problem) = CheckLock(manifest, locked, syncFailures);

        return new PublishPlan(
            manifest.Id, mods, manifest.Connect, covers, problem,
            CountValues(manifest.ModConfig), manifest.Keybinds?.Count ?? 0);
    }

    /// <summary>
    /// Every leaf across every file, since one file may carry several settings and a count of
    /// files would read as a count of mods.
    /// </summary>
    private static int CountValues(IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject>? config)
    {
        if (config is null) return 0;

        var total = 0;
        foreach (var (_, file) in config) total += Leaves(file);
        return total;

        static int Leaves(System.Text.Json.Nodes.JsonObject node)
        {
            var n = 0;
            foreach (var (_, value) in node)
                n += value is System.Text.Json.Nodes.JsonObject section ? Leaves(section) : 1;
            return n;
        }
    }

    /// <summary>
    /// Whether the lock actually describes this manifest. A lock that names a different
    /// game version, or misses mods the manifest asks for, would publish a claim of
    /// reproducibility that is not true.
    /// </summary>
    private static (bool Covers, string? Problem) CheckLock(
        PackManifest manifest, PackLock? locked, IReadOnlyList<SyncStep>? syncFailures)
    {
        if (manifest.Mods.Count == 0)
            return (false, Lang.Get("share-no-mods"));

        if (locked is null)
            return (false, Lang.Get("share-never-synced"));

        if (!string.Equals(locked.GameVersion, manifest.GameVersion, StringComparison.OrdinalIgnoreCase))
            return (false,
                $"The lockfile is for game {locked.GameVersion} but the pack targets "
                + $"{manifest.GameVersion}. Sync it first.");

        var lockedIds = locked.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = manifest.Mods.Where(m => !lockedIds.Contains(m.ModId)).ToList();

        if (missing.Count == 0) return (true, null);

        // Publishing syncs first, so by the time this is read the mods really cannot be
        // installed rather than merely not having been. Saying "sync the pack first" here
        // sent people to press a button that had already been pressed on their behalf; the
        // reason the sync gave is the only thing that moves them forward.
        var explained = missing
            .Select(m => (m.ModId, Why: Reason(m.ModId)))
            .Where(x => x.Why is not null)
            .ToList();

        if (explained.Count > 0)
            return (false,
                $"{missing.Count} mod{(missing.Count == 1 ? "" : "s")} could not be installed: "
                + string.Join("; ", explained.Take(3).Select(x => $"{x.ModId} — {x.Why}"))
                + (explained.Count > 3 ? "; …" : "") + ".");

        // No sync was run, or one was and said nothing about these. Still not a claim about
        // which — a mod added moments ago and a mod nothing has reached leave the same trace.
        return (false,
            $"{missing.Count} mod{(missing.Count == 1 ? " is" : "s are")} not installed "
            + $"({string.Join(", ", missing.Take(3).Select(m => m.ModId))}"
            + $"{(missing.Count > 3 ? ", …" : "")}). Sync the pack first.");

        string? Reason(string modId) => syncFailures?
            .FirstOrDefault(s => s.Action == SyncAction.Failed
                                 && string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase))
            ?.Detail;
    }
}
