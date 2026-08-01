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

    /// <summary>Whether there is a published URL to show and copy.</summary>
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public string Label => Status switch
    {
        ShareStatus.Unshared => "Share…",
        ShareStatus.Shared => "Shared",
        ShareStatus.Pending => "Publish changes",
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
