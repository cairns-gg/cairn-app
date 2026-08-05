namespace Cairn.Core.Packs;

/// <summary>What an update would do to one mod, and why.</summary>
public enum ModChangeKind
{
    /// <summary>The author added it. Taking it is the point of updating.</summary>
    Added,

    /// <summary>The author dropped it, and you had not touched it.</summary>
    Removed,

    /// <summary>The author changed which version they pin, and you had not touched it.</summary>
    Repinned,

    /// <summary>
    /// You removed a mod the author still ships. Nothing is wrong, but an update silently
    /// putting it back would undo a deliberate decision, so it is asked about rather than
    /// assumed either way.
    /// </summary>
    DroppedByYou,

    /// <summary>
    /// You and the author both name a version, and they differ. A pin is an instruction to
    /// stay put, so this is the one case that genuinely needs an answer.
    /// </summary>
    PinConflict,

    /// <summary>Yours alone. An update has no opinion about it.</summary>
    Yours,
}

/// <summary>
/// One mod's fate in an update.
/// </summary>
/// <param name="Mine">The version this copy names, or null for "whatever is newest".</param>
/// <param name="Theirs">The version the author names, or null for the same.</param>
public sealed record ModChange(
    string ModId,
    ModChangeKind Kind,
    string? Mine,
    string? Theirs)
{
    /// <summary>
    /// Whether to do what the author did. Settable because two of the kinds are questions
    /// rather than statements, and the answer belongs to the person holding the pack.
    /// </summary>
    public bool Take { get; set; }

    /// <summary>
    /// Stop raising this one. Only meaningful for a mod you removed that the author still
    /// ships, which is the difference that stays true for ever and would otherwise be
    /// mentioned at every revision.
    ///
    /// Set by a person ticking a box and by nothing else. Ignored when
    /// <see cref="Take"/> is set, because putting the mod back leaves nothing to ask about.
    /// </summary>
    public bool Silence { get; set; }

    public bool CanSilence => Kind == ModChangeKind.DroppedByYou;

    /// <summary>Whether this one is a question. The rest are reported, not asked.</summary>
    public bool IsChoice => Kind is ModChangeKind.PinConflict or ModChangeKind.DroppedByYou;

    public string Describe() => Kind switch
    {
        ModChangeKind.Added => Theirs is null ? "added" : $"added, pinned to {Theirs}",
        ModChangeKind.Removed => "removed by the author",
        ModChangeKind.Repinned => $"{Mine ?? "newest"} → {Theirs ?? "newest"}",
        ModChangeKind.DroppedByYou => "you removed it; the author still includes it",
        ModChangeKind.PinConflict => $"you pin {Mine ?? "newest"}, the author pins {Theirs ?? "newest"}",
        ModChangeKind.Yours => "yours",
        _ => "",
    };
}

/// <summary>
/// What applying the author's newer revision would do to this copy of their pack.
///
/// A three-way merge, and it has to be: the follower's manifest and the author's newest
/// one are not enough on their own to say what a difference means. A mod present upstream
/// and absent here is either one the author has just added — take it, that is the whole
/// point — or one this person deliberately removed, and putting it back every time an
/// update lands would be a bug they could never get out of. The base
/// (<see cref="PackStore.LoadUpstream"/>) is what tells those apart.
///
/// Built and thrown away, like <see cref="PublishPlan"/> and VersionChangePlan: shown,
/// answered, applied or abandoned. Nothing here writes anything.
/// </summary>
public sealed class PackUpdatePlan
{
    private readonly PackManifest _mine;
    private readonly PackManifest _theirs;

    /// <summary>
    /// Mods left out of <see cref="Changes"/> because they were already declined. Merge has
    /// to know: a mod with no change reads as "nothing to decide, take theirs", which for
    /// a declined one would put it back — silencing the question by granting it.
    /// </summary>
    private readonly HashSet<string> _declined;

    private PackUpdatePlan(
        PackManifest mine, PackManifest theirs, int fromRevision, int toRevision,
        IReadOnlyList<ModChange> changes, bool hadBase, HashSet<string> declined)
    {
        _mine = mine;
        _theirs = theirs;
        _declined = declined;
        FromRevision = fromRevision;
        ToRevision = toRevision;
        Changes = changes;
        HasBase = hadBase;
    }

    public int FromRevision { get; }
    public int ToRevision { get; }
    public IReadOnlyList<ModChange> Changes { get; }

    /// <summary>
    /// Whether a merge base was available. False for a pack imported before Cairn recorded
    /// one, where a mod you removed cannot be told from a mod the author added — so those
    /// come through as additions, and the pack says as much rather than pretending.
    /// </summary>
    public bool HasBase { get; }

    public string GameVersion => _theirs.GameVersion;

    /// <summary>The author retargeted the pack. Worth its own line: it moves every mod.</summary>
    public bool GameVersionChanges =>
        !string.Equals(_mine.GameVersion, _theirs.GameVersion, StringComparison.OrdinalIgnoreCase);

    public string? PreviousGameVersion => GameVersionChanges ? _mine.GameVersion : null;

    public IEnumerable<ModChange> Choices => Changes.Where(c => c.IsChoice);

    public IEnumerable<ModChange> TheirChanges =>
        Changes.Where(c => c.Kind is ModChangeKind.Added or ModChangeKind.Removed
                               or ModChangeKind.Repinned);

    /// <summary>Whether applying this would alter anything at all.</summary>
    public bool AnyChange => GameVersionChanges || TheirChanges.Any() || Choices.Any();

    public string Summary()
    {
        var added = Changes.Count(c => c.Kind == ModChangeKind.Added);
        var removed = Changes.Count(c => c.Kind == ModChangeKind.Removed);
        var repinned = Changes.Count(c => c.Kind == ModChangeKind.Repinned);

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} added");
        if (removed > 0) parts.Add($"{removed} removed");
        if (repinned > 0) parts.Add($"{repinned} repinned");

        return parts.Count == 0
            ? $"Revision {ToRevision} changes no mods."
            : $"Revision {ToRevision}: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// Works out the merge. Nothing is written, and the answers to the questions start at
    /// their defaults — see <see cref="ModChange.Take"/>.
    /// </summary>
    /// <param name="mine">This copy's manifest, as edited by whoever holds it.</param>
    /// <param name="theirs">The author's manifest at the newer revision.</param>
    /// <param name="base">
    /// The author's manifest at the revision this copy follows. Null when there is none,
    /// which costs the ability to recognise a local removal.
    /// </param>
    /// <param name="state">
    /// What this machine has already decided. A mod declined here is left out silently
    /// rather than raised again — the difference is still real, but it has been answered,
    /// and asking once per revision for ever is the thing the answer was given to stop.
    /// </param>
    public static PackUpdatePlan Between(
        PackManifest mine,
        PackManifest theirs,
        PackManifest? @base,
        int fromRevision = 0,
        int toRevision = 0,
        PackLocalState? state = null)
    {
        // No base is not the same as an empty base: an empty one would call every mod they
        // have an addition and every mod you have yours, which is accidentally the right
        // answer for additions and the wrong one for everything else. Falling back to your
        // own manifest at least means an unedited follower merges perfectly, and an edited
        // one is told the base was missing.
        var basis = (@base ?? mine).Mods.ToDictionary(
            m => m.ModId, m => m.Version, StringComparer.OrdinalIgnoreCase);

        var local = mine.Mods.ToDictionary(
            m => m.ModId, m => m.Version, StringComparer.OrdinalIgnoreCase);

        var author = theirs.Mods.ToDictionary(
            m => m.ModId, m => m.Version, StringComparer.OrdinalIgnoreCase);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in basis.Keys.Concat(local.Keys).Concat(author.Keys)) ids.Add(id);

        var changes = new List<ModChange>();
        var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids.Order(StringComparer.OrdinalIgnoreCase))
        {
            var inBase = basis.TryGetValue(id, out var wasPin);
            var inMine = local.TryGetValue(id, out var myPin);
            var inTheirs = author.TryGetValue(id, out var theirPin);

            switch (inMine, inTheirs)
            {
                // Nobody has it any more, or you both dropped it. Nothing to say.
                case (false, false):
                    continue;

                // Only you have it. Yours to keep — an update has no opinion about a mod
                // the author never shipped.
                case (true, false) when !inBase:
                    changes.Add(new ModChange(id, ModChangeKind.Yours, myPin, null));
                    continue;

                // They had it and dropped it.
                case (true, false):
                    changes.Add(new ModChange(id, ModChangeKind.Removed, myPin, null) { Take = true });
                    continue;

                // They ship it and you do not. Which of the two meanings depends on the base.
                case (false, true) when inBase:
                    // You took it out on purpose. Left out by default, because undoing that
                    // silently on every update is worse than an update that leaves a gap —
                    // and left out of the plan entirely once you have said not to ask.
                    if (state?.HasDeclined(id) == true) { declined.Add(id); continue; }

                    changes.Add(new ModChange(id, ModChangeKind.DroppedByYou, null, theirPin) { Take = false });
                    continue;

                case (false, true):
                    changes.Add(new ModChange(id, ModChangeKind.Added, null, theirPin) { Take = true });
                    continue;
            }

            // Both have it; only the pin can differ.
            if (string.Equals(myPin, theirPin, StringComparison.OrdinalIgnoreCase)) continue;

            // You never touched it, so this is simply their change.
            if (inBase && string.Equals(myPin, wasPin, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new ModChange(id, ModChangeKind.Repinned, myPin, theirPin) { Take = true });
                continue;
            }

            // You chose a version. A pin is an instruction to stay put — Cairn never offers
            // a pinned mod an update anywhere else — so the default keeps yours, and the
            // question is put rather than answered.
            changes.Add(new ModChange(id, ModChangeKind.PinConflict, myPin, theirPin) { Take = false });
        }

        return new PackUpdatePlan(
            mine, theirs, fromRevision, toRevision, changes, @base is not null, declined);
    }

    /// <summary>
    /// The manifest this update would leave behind, with every decision applied.
    ///
    /// The author's, plus what is yours: their id, name, description, game version and
    /// server, because it is their pack and this is their revision of it.
    /// </summary>
    public PackManifest Merge()
    {
        var merged = new PackManifest
        {
            Id = _mine.Id,          // yours: renaming a pack under somebody is not an update
            Name = _theirs.Name,
            Description = _theirs.Description,
            GameVersion = _theirs.GameVersion,
            Connect = _theirs.Connect,
            Mods = [],
        };

        var decided = Changes.ToDictionary(c => c.ModId, StringComparer.OrdinalIgnoreCase);

        // Walk the author's list first so their order survives, then append what is yours.
        foreach (var mod in _theirs.Mods)
        {
            // Declined earlier, so it was never raised — and must not arrive by the back
            // door of having nothing recorded against it.
            if (_declined.Contains(mod.ModId)) continue;

            if (!decided.TryGetValue(mod.ModId, out var change))
            {
                merged.Mods.Add(new PackMod { ModId = mod.ModId, Version = mod.Version });
                continue;
            }

            switch (change.Kind)
            {
                case ModChangeKind.DroppedByYou when !change.Take:
                    continue;   // stays out, as you left it

                case ModChangeKind.PinConflict when !change.Take:
                    merged.Mods.Add(new PackMod { ModId = mod.ModId, Version = change.Mine });
                    continue;

                default:
                    merged.Mods.Add(new PackMod { ModId = mod.ModId, Version = mod.Version });
                    continue;
            }
        }

        // Mods only you have, in the order you had them.
        var theirIds = _theirs.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _mine.Mods)
        {
            if (theirIds.Contains(mod.ModId)) continue;

            // A mod the author removed goes; one that was never theirs stays.
            if (decided.TryGetValue(mod.ModId, out var change)
                && change.Kind == ModChangeKind.Removed && change.Take) continue;

            merged.Mods.Add(new PackMod { ModId = mod.ModId, Version = mod.Version });
        }

        return merged;
    }
}
