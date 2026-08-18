using System.Text.Json.Nodes;

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
    /// The author is shipping a different version of a mod neither of you pins.
    ///
    /// Not a pin moving: nobody named a version anywhere in either manifest. Most authors
    /// pin nothing, so for most packs this is what a mod update *is* — the version lives in
    /// the lockfile, which is the document that makes a published pack reproduce, and the
    /// manifests on both ends stay byte-identical while five mods move under them.
    /// </summary>
    Relocked,

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
        ModChangeKind.Added => Theirs is null
                ? Lang.Get("packupdate-desc-added")
                : Lang.Get("packupdate-desc-added-pinned", Theirs),
            ModChangeKind.Removed => Lang.Get("packupdate-desc-removed"),
            ModChangeKind.Repinned or ModChangeKind.Relocked =>
                $"{Mine ?? Lang.Get("packupdate-newest")} → {Theirs ?? Lang.Get("packupdate-newest")}",
            ModChangeKind.DroppedByYou => Lang.Get("packupdate-desc-dropped"),
            ModChangeKind.PinConflict => Lang.Get("packupdate-desc-pin-conflict",
                Mine ?? Lang.Get("packupdate-newest"), Theirs ?? Lang.Get("packupdate-newest")),
            ModChangeKind.Yours => Lang.Get("packupdate-yours"),
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

    /// <summary>
    /// The keybinds of the revision this copy follows, which is what tells a binding the
    /// holder chose from one they were simply given. Without it every code the author has
    /// ever published reads as the follower's own — an unedited copy would pin the author's
    /// first set for ever, and they could never move a key again for anybody who already
    /// has the pack. Same three-way reasoning as the mod list, and the same fallback when
    /// there is no base: see <see cref="Between"/>.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string> _basisKeybinds;

    private PackUpdatePlan(
        PackManifest mine, PackManifest theirs, int fromRevision, int toRevision,
        IReadOnlyList<ModChange> changes, bool hadBase, HashSet<string> declined,
        IReadOnlyDictionary<string, string> basisKeybinds)
    {
        _mine = mine;
        _theirs = theirs;
        _declined = declined;
        _basisKeybinds = basisKeybinds;
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

    /// <summary>
    /// Take the author's pack exactly, discarding everything this copy did to it.
    ///
    /// Not a shortcut for answering every question their way: that keeps the mods you
    /// added, because the author has no opinion about those. This drops them too, which is
    /// the only way to get back to a copy that matches the one everyone else on the server
    /// is running — the usual reason to want it, and the reason it cannot simply be the
    /// default for anything.
    ///
    /// Deliberately a property on the plan rather than a separate action. What it would
    /// remove is knowable only by working out the merge, and it must be shown before it is
    /// agreed to, exactly like every other answer here.
    /// </summary>
    public bool Reset { get; set; }

    /// <summary>
    /// Mods a reset would take out of the pack: yours, and any the author has dropped.
    ///
    /// Worth its own list because it is the destructive half. Vintage Story worlds hold
    /// blocks and items from the mods that made them, so removing one from a pack a world
    /// was built in is not a change to a mod list — it is a change to the save.
    /// </summary>
    public IEnumerable<string> RemovedByReset
    {
        get
        {
            if (!Reset) return [];

            var theirs = _theirs.Mods.Select(m => m.ModId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _mine.Mods.Select(m => m.ModId).Where(id => !theirs.Contains(id));
        }
    }

    public bool ResetRemovesAnything => RemovedByReset.Any();

    public IEnumerable<ModChange> TheirChanges =>
        Changes.Where(c => c.Kind is ModChangeKind.Added or ModChangeKind.Removed
                               or ModChangeKind.Repinned or ModChangeKind.Relocked);

    /// <summary>
    /// Whether the author's server address changes, and what to.
    ///
    /// A pack's connect address is what launching joins, so it decides which server a
    /// person's client hands their session to. Import treats it as worth a warning box of
    /// its own; taking a revision took it silently, and left it out of
    /// <see cref="AnyChange"/> as well — so a revision that changed nothing but this
    /// reported "matches the author's revision" and then rerouted the pack. An attacker who
    /// cannot get a hostile address past the import screen publishes revision 2.
    /// </summary>
    public bool ConnectChanges => !string.Equals(
        _mine.Connect ?? "", _theirs.Connect ?? "", StringComparison.OrdinalIgnoreCase);

    public string? ConnectFrom => _mine.Connect;
    public string? ConnectTo => _theirs.Connect;

    /// <summary>
    /// Whether the author's keybinds change. Not a security question the way the connect
    /// address is — a keybind cannot reach anything — but it is a change to the pack that
    /// was being applied without appearing anywhere, which is the same defect.
    /// </summary>
    public bool KeybindsChange
    {
        get
        {
            var mine = _mine.Keybinds ?? [];
            var theirs = _theirs.Keybinds ?? [];

            return mine.Count != theirs.Count
                   || mine.Any(kv => !theirs.TryGetValue(kv.Key, out var v) || v != kv.Value);
        }
    }

    /// <summary>
    /// Whether the author's mod config changes.
    ///
    /// The third field to arrive after this check was written and the third to be left out
    /// of it, which is the pattern rather than the accident: the plan compares content, so
    /// every field added to a manifest has to be added here too or a revision changing only
    /// that field reports "already on the author's newest revision" and exits 0. A server
    /// following a pack sat on revision 10 while the author published 11, with no way to
    /// tell that anything was wrong.
    ///
    /// Compared per file with <see cref="JsonNode.DeepEquals"/>, since the value is a sparse
    /// object and two of them differing anywhere is a change.
    /// </summary>
    public bool ModConfigChanges
    {
        get
        {
            var mine = _mine.ModConfig ?? [];
            var theirs = _theirs.ModConfig ?? [];

            if (mine.Count != theirs.Count) return true;

            foreach (var (file, patch) in mine)
                if (!theirs.TryGetValue(file, out var other) || !JsonNode.DeepEquals(patch, other))
                    return true;

            return false;
        }
    }

    /// <summary>
    /// Whether applying this would alter anything at all.
    ///
    /// Includes the three fields above deliberately. They are taken from the author
    /// unconditionally — there is no question to answer about them, which is why they are
    /// not in <see cref="Choices"/> — but "nothing to do" has to mean nothing, or the
    /// dialog that says so is how a change gets made.
    ///
    /// The mod versions are in here through <see cref="TheirChanges"/>, and only because
    /// <see cref="Between"/> is given both lockfiles. A manifest need not name a version at
    /// all — see <see cref="ModChangeKind.Relocked"/> — so comparing manifests answered no
    /// for the commonest revision anybody publishes.
    /// </summary>
    public bool AnyChange =>
        GameVersionChanges || TheirChanges.Any() || Choices.Any()
        || ConnectChanges || KeybindsChange || ModConfigChanges;

    public string Summary()
    {
        var added = Changes.Count(c => c.Kind == ModChangeKind.Added);
        var removed = Changes.Count(c => c.Kind == ModChangeKind.Removed);
        var repinned = Changes.Count(c => c.Kind == ModChangeKind.Repinned);
        var relocked = Changes.Count(c => c.Kind == ModChangeKind.Relocked);

        var parts = new List<string>();
        if (added > 0) parts.Add(Lang.Get("packupdate-n-added", added));
        if (removed > 0) parts.Add(Lang.Get("packupdate-n-removed", removed));
        if (repinned > 0) parts.Add(Lang.Get("packupdate-n-repinned", repinned));

        // Counted apart from repinned, because they are different news. "Repinned" is the
        // author changing their mind about a version they had named; this is the ordinary
        // one — they took the mod updates — and it is what nearly every revision is.
        if (relocked > 0) parts.Add(Lang.Get("packupdate-n-updated", relocked));

        // Named rather than counted, and after the mods so it is the last thing read. This
        // is the one line somebody sees before agreeing, and where the pack points is not a
        // detail to fold into a tally.
        if (ConnectChanges)
        {
            parts.Add(string.IsNullOrWhiteSpace(ConnectTo)
                ? Lang.Get("packupdate-stops-joining")
                : Lang.Get("packupdate-joins", ConnectTo));
        }

        if (KeybindsChange) parts.Add(Lang.Get("packupdate-changes-keybinds"));
        if (ModConfigChanges) parts.Add(Lang.Get("packupdate-changes-modconfig"));

        return parts.Count == 0
            ? Lang.Get("packupdate-no-mod-changes", ToRevision)
            : Lang.Get("packupdate-summary", ToRevision, string.Join(", ", parts));
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
    /// <param name="myLock">This copy's lockfile. See <paramref name="theirLock"/>.</param>
    /// <param name="theirLock">
    /// The author's lockfile at the newer revision, as served beside their manifest.
    ///
    /// Without the pair of them a plan cannot see a mod update at all. A manifest entry is
    /// allowed to be nothing but <c>{"modid": "x"}</c>, and most are: pinning is the
    /// exception, and the version an author actually ships lives in their lock — which is
    /// the whole reason a published pack carries one. So an author who updates five mods
    /// and changes nothing else publishes a revision whose manifest is byte-identical to
    /// its predecessor, and a plan built from manifests alone reported no change: the
    /// launcher said the pack matched, and <c>cairn-server update</c> said "already on the
    /// author's newest revision" and exited 0, revision after revision.
    ///
    /// Null for a comparison that has no lock to hand, which loses nothing that was there
    /// before.
    /// </param>
    public static PackUpdatePlan Between(
        PackManifest mine,
        PackManifest theirs,
        PackManifest? @base,
        int fromRevision = 0,
        int toRevision = 0,
        PackLocalState? state = null,
        PackLock? myLock = null,
        PackLock? theirLock = null)
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

        // Indexed rather than ToDictionary: a lockfile is generated, but it is also a file
        // on somebody else's disk that arrived over the network, and a duplicate modid in
        // one is not a reason for a plan to throw.
        static Dictionary<string, string> Locked(PackLock? file)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in file?.Mods ?? []) map[mod.ModId] = mod.Version;
            return map;
        }

        var myVersions = Locked(myLock);
        var theirVersions = Locked(theirLock);

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

            // Both have it, and the pins agree — which includes both of them naming
            // nothing, the ordinary case. Agreeing on no pin is not agreeing on a mod:
            // sync installs what the lock says whenever the manifest asks for no
            // particular version, so the author's lock is the whole of what a mod update
            // to their pack consists of.
            if (string.Equals(myPin, theirPin, StringComparison.OrdinalIgnoreCase))
            {
                // A pin outranks either lock at install time — PackSyncer stops believing
                // a lock entry the moment it disagrees with the version asked for — so two
                // matching pins are settled however the locks read.
                if (myPin is not null) continue;

                if (!theirVersions.TryGetValue(id, out var theirVersion)
                    || string.IsNullOrWhiteSpace(theirVersion)) continue;

                // No entry of your own is still a change: it means this copy resolves
                // newest-compatible today and would reproduce the author's build after.
                var known = myVersions.TryGetValue(id, out var myVersion)
                            && !string.IsNullOrWhiteSpace(myVersion);

                if (known && string.Equals(myVersion, theirVersion, StringComparison.OrdinalIgnoreCase))
                    continue;

                changes.Add(new ModChange(
                    id, ModChangeKind.Relocked, known ? myVersion : null, theirVersion) { Take = true });
                continue;
            }

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
            mine, theirs, fromRevision, toRevision, changes, @base is not null, declined,
            (@base ?? mine).Keybinds ?? []);
    }

    /// <summary>
    /// The manifest this update would leave behind, with every decision applied.
    ///
    /// The author's, plus what is yours: their id, name, description, game version and
    /// server, because it is their pack and this is their revision of it.
    ///
    /// Every field here is named on purpose. A manifest field left out of this list is one
    /// that silently empties on every update, which is how the pack's hotkeys were lost
    /// between the revision that shipped them and the next — see <see cref="MergeKeybinds"/>.
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
            Keybinds = MergeKeybinds(),

            // Theirs, whole, and not merged the way keybinds are. The protection a merge
            // would provide already exists one layer down: ModConfigFiles keeps its own
            // record of what the pack last asked for, so a value somebody changed here stays
            // theirs at apply time no matter what the manifest declares. What the manifest
            // holds is the author's list of what the pack carries, and for a follower that
            // is the author's to write.
            //
            // Named at all because it was not, and the comment above said what that costs
            // before it happened a second time: an update silently emptied the field, so
            // taking any revision at all threw away every mod setting the pack carried.
            ModConfig = _theirs.ModConfig,
            Mods = [],
        };

        // Their list, whole. Every answer on this plan is about reconciling two sets of
        // changes, and a reset is the statement that there is only one set worth keeping —
        // so the questions are not consulted rather than being answered their way.
        if (Reset)
        {
            foreach (var mod in _theirs.Mods)
                merged.Mods.Add(new PackMod { ModId = mod.ModId, Version = mod.Version });

            return merged;
        }

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

    /// <summary>
    /// The hotkeys this update would leave behind.
    ///
    /// The author's, plus the ones this copy chose for itself. Reconciling twenty mods'
    /// collisions is the author's work and the reason the pack carries it at all, so a
    /// revision that moves a key has to reach the people already holding the pack — but
    /// somebody who sat down and rebound something did so on purpose, and an update
    /// putting it back is the bug they can never get out of.
    ///
    /// Told apart by the base, exactly as a mod's pin is: a code whose value here still
    /// matches the revision this copy follows was never chosen, it was inherited, and the
    /// author's newer answer stands. One whose value has moved is a decision, and it
    /// survives. A code the holder cleared stays cleared, for the same reason a mod they
    /// removed is not put back.
    ///
    /// No base means no way to tell those apart, so the basis falls back to this copy's
    /// own — under which nothing reads as chosen and the author's set wins whole. The same
    /// fallback the mod list makes, and for the same reason: an unedited follower merges
    /// perfectly, which is nearly all of them.
    ///
    /// Null rather than empty when it comes to nothing, so a pack that has never set one
    /// keeps the file it had.
    /// </summary>
    private Dictionary<string, string>? MergeKeybinds()
    {
        var theirs = _theirs.Keybinds ?? [];

        // Their answer, whole — a reset keeps nothing of this copy's own.
        if (Reset)
            return theirs.Count == 0 ? null : new Dictionary<string, string>(theirs, StringComparer.Ordinal);

        var mine = _mine.Keybinds ?? [];
        var merged = new Dictionary<string, string>(theirs, StringComparer.Ordinal);

        foreach (var code in _basisKeybinds.Keys.Concat(mine.Keys).Distinct(StringComparer.Ordinal))
        {
            var wasInherited = _basisKeybinds.TryGetValue(code, out var inherited);

            if (mine.TryGetValue(code, out var chosen))
            {
                // Untouched since it arrived, so this is simply their change.
                if (wasInherited && string.Equals(chosen, inherited, StringComparison.Ordinal)) continue;

                merged[code] = chosen;
                continue;
            }

            // Had it, cleared it. Left cleared rather than handed back on every revision.
            if (wasInherited) merged.Remove(code);
        }

        return merged.Count == 0 ? null : merged;
    }
}
