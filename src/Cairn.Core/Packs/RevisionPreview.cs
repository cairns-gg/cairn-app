using Cairn.Core.Launch;

namespace Cairn.Core.Packs;

/// <summary>What <see cref="PackStore.RestoreRevision"/> did.</summary>
/// <param name="Settings">
/// Every carried setting, written or not. <see cref="NotWritten"/> is the part to say out
/// loud: a value left in its file at the broken setting is one the next publish sends again.
/// </param>
public sealed record RestoredRevision(PackManifest Manifest, IReadOnlyList<ModConfigChange> Settings)
{
    public IEnumerable<ModConfigChange> NotWritten =>
        Settings.Where(c => c.Outcome is not (ModConfigOutcome.Applied or ModConfigOutcome.Released));
}

/// <summary>
/// What restoring an earlier revision would do to the pack as it stands — read before
/// <see cref="PackStore.RestoreRevision"/>, and deciding nothing.
///
/// Per mod as well as counted, unlike the line the Share window shows. Somebody restoring is
/// usually hunting for the one mod that broke their world, and "3 at different versions"
/// does not say which.
/// </summary>
/// <param name="Changes">
/// Each mod the restore would add, remove or move, in the update plan's terms. That plan is
/// a three-way merge, and is run here with the current pack as its own base — which is what
/// makes every difference a statement rather than a question: nothing in a restore is
/// "yours" to keep, because the whole point is to stop having it.
/// </param>
public sealed record RevisionPreview(int Revision, IReadOnlyList<ModChange> Changes, PublishDelta Delta)
{
    /// <param name="number">
    /// The revision that was asked for. Taken from the request rather than from the
    /// document's own <c>revision</c> field, which only says what the document claims to be.
    /// </param>
    public static RevisionPreview Of(PackStore store, string id, int number, PackBundle revision)
    {
        var theirs = revision.Pack
                     ?? throw new InvalidDataException(Lang.Get("bundle-has-no-pack"));

        var current = store.Load(id);

        var plan = PackUpdatePlan.Between(
            current, theirs, @base: current,
            myLock: store.LoadLock(id), theirLock: revision.Lock);

        // The pack as it would be published now, with its server address treated the way
        // the restore will treat it: kept when the revision carries none. Without that, a
        // revision published with the address stripped would read as removing it.
        var now = PackBundle.Parse(store.PublishedDocument(id, stripConnect: theirs.Connect is null));

        return new RevisionPreview(
            number,
            [.. plan.Changes.Where(c => c.Kind != ModChangeKind.Yours)],
            PublishDelta.Between(now, revision));
    }

    /// <summary>
    /// Whether restoring would change anything. The delta alone answers it: its mod counts
    /// come from the same plan <see cref="Changes"/> lists.
    /// </summary>
    public bool Anything => Delta.Anything;

    /// <summary>
    /// What restoring does to one mod, in words for the person restoring. The update plan's
    /// own wording is a follower's — "removed by the author" — and here the author is the
    /// one reading.
    /// </summary>
    public static string Describe(ModChange change) => change.Kind switch
    {
        ModChangeKind.Added => change.Theirs is { } pinned
            ? Lang.Get("restore-change-back-pinned", pinned)
            : Lang.Get("restore-change-back"),
        ModChangeKind.Removed => Lang.Get("restore-change-removed"),
        _ => change.Describe(),
    };

    /// <summary>
    /// Whether the revision is for an older game than the pack targets now. Worth its own
    /// sentence in <see cref="WorldWarning"/>: a world a newer game has opened may not load
    /// in an older one, whatever the mods do.
    /// </summary>
    public bool OlderGame =>
        Delta.GameVersionFrom is { } from
        && GameVersionComparer.Ascending.Compare(Delta.GameVersionTo, from) < 0;

    /// <summary>
    /// What a restore can do to the worlds, said before anybody presses the button.
    ///
    /// The restore itself touches no world — the damage comes on the next launch, when a
    /// world played with the mods the pack has now is opened without some of them, or with
    /// older ones. Blocks, items and creatures a missing mod added are lost from that world
    /// for good, or it does not load. Nothing in Cairn can undo that afterwards, and a
    /// restore is exactly the moment somebody is in a hurry to get back to playing, so it
    /// is said every time, by name when the pack's worlds are known, rather than only when
    /// a heuristic thinks a particular change is risky.
    /// </summary>
    /// <param name="worlds">The worlds in the pack's own data path, by name.</param>
    /// <param name="preview">The chosen revision, once fetched; null before then.</param>
    public static string WorldWarning(IReadOnlyList<string> worlds, RevisionPreview? preview)
    {
        var warning = worlds.Count == 0
            ? Lang.Get("restore-worlds-elsewhere")
            : Lang.Plural("restore-worlds", worlds.Count, string.Join(", ", worlds));

        return preview is { OlderGame: true }
            ? warning + " " + Lang.Get("restore-worlds-older-game",
                preview.Delta.GameVersionFrom, preview.Delta.GameVersionTo)
            : warning;
    }

    /// <summary>The one line above the list.</summary>
    public string Summary => Anything
        ? Lang.Get("restore-would", Revision, Delta.Describe())
        : Lang.Get("restore-same", Revision);
}
