namespace Cairn.Core.Packs;

/// <summary>What the Share button is offering, for one pack.</summary>
public enum ShareStatus
{
    /// <summary>Never published from this machine.</summary>
    Unshared,

    /// <summary>Published, and the local pack still matches what was sent.</summary>
    Shared,

    /// <summary>Published, but the pack has changed since.</summary>
    Pending,

    /// <summary>
    /// Imported from cairns.gg and still following its author. Publishing it would be
    /// republishing someone else's curation under your own name, so the button is not
    /// offered at all — Take over comes first.
    /// </summary>
    Following,

    /// <summary>
    /// Published once and then taken down by its author. Distinct from Unshared because
    /// the pack still has an address: the row survives on the site and the URL answers
    /// 410 with a tombstone rather than 404, since these links live in chat scrollback
    /// and committed pack.json files indefinitely.
    ///
    /// Reversible, and that is the point of naming it. Publishing again clears the
    /// tombstone and revives the pack where it was — a pack reported as never shared
    /// would leave its author guessing whether the old link comes back or a second copy
    /// appears beside it.
    /// </summary>
    Withdrawn,
}

/// <summary>
/// The Share button, worked out from a pack's link file and what it would publish now.
///
/// A projection rather than stored state: anything derived from the manifest and lock —
/// whether there is something to publish — has to be recomputed when they change, and
/// caching it is how a button ends up lying about a pack.
/// </summary>
public sealed record ShareState(ShareStatus Status, string? Url, string? Visibility = null)
{
    public static readonly ShareState NotShared = new(ShareStatus.Unshared, null);

    /// <summary>
    /// Reachable by its link and absent from browse. Worth saying wherever the URL is
    /// shown: the two are indistinguishable from outside, and which one a pack is decides
    /// whether handing the link around is sharing it or publishing it.
    /// </summary>
    public bool IsUnlisted => Visibility == "unlisted";

    /// <summary>False while following, where the button is hidden rather than disabled.</summary>
    public bool IsOffered => Status != ShareStatus.Following;

    /// <summary>
    /// Whether there is a URL at all. Not the same as one worth putting in front of
    /// somebody — a followed pack's belongs to its author and a withdrawn one no longer
    /// serves the pack — so callers showing it gate on the status too.
    /// </summary>
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public string Label => Status switch
    {
        ShareStatus.Unshared => Lang.Get("share-label-unshared"),
            ShareStatus.Shared => Lang.Get("share-label-shared"),
            ShareStatus.Pending => Lang.Get("share-publish-changes"),

        // Not "Share…", which would read as starting again somewhere new. This one has an
        // address waiting for it.
        ShareStatus.Withdrawn => Lang.Get("share-label-publish-again"),
        _ => "",
    };

    /// <summary>
    /// True only for <see cref="ShareStatus.Pending"/>. Play is what people open the app
    /// for; Share is roughly once per pack, and giving them the same weight would make the
    /// row read as two equal choices. It earns the accent only when something is actually
    /// outstanding.
    /// </summary>
    public bool IsUrgent => Status == ShareStatus.Pending;

    /// <param name="link">The pack's link file, or null if it has never been shared.</param>
    /// <param name="publishedNow">
    /// What publishing this pack right now would send — already carrying the same options
    /// the last publish used, so a stripped server address does not read as a change.
    /// Null when it cannot be worked out, which reports the pack as unchanged rather than
    /// inventing a difference.
    /// </param>
    public static ShareState For(PackLink? link, string? publishedNow)
    {
        if (link is null) return NotShared;

        if (link is { Role: PackRole.Follower, Following: true })
            return new ShareState(ShareStatus.Following, link.Url);

        // Before the check below, which this would otherwise fall through: withdrawing
        // clears the publish record, and reporting the pack as never shared would lose
        // the one thing its author needs to know — the address is still theirs.
        if (link is { Role: PackRole.Author, Withdrawn: true })
            return new ShareState(ShareStatus.Withdrawn, link.Url);

        // A taken-over pack is not published until it is published: it points at where it
        // came from, which is not a URL its owner can update.
        if (link.Role != PackRole.Author || link.Published is null)
            return NotShared;

        var changed = publishedNow is not null
                      && !string.Equals(
                          PackLink.Fingerprint(publishedNow),
                          link.Published.Fingerprint,
                          StringComparison.OrdinalIgnoreCase);

        return new ShareState(
            changed ? ShareStatus.Pending : ShareStatus.Shared,
            link.Url,
            link.Published.Visibility);
    }
}
