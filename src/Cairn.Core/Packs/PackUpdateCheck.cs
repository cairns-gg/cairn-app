namespace Cairn.Core.Packs;

/// <summary>
/// A newer revision of a followed pack, waiting at its author's URL.
/// </summary>
public sealed record PackUpdateAvailable(int From, int To, PackBundle Bundle)
{
    public string Describe() => $"revision {To} is available (you have {From})";
}

/// <summary>
/// Asks a followed pack's author whether they have published since.
///
/// The canonical URL already serves the whole bundle — it is what import fetches — so
/// checking is one GET and a comparison of two integers. No new endpoint, and nothing that
/// needs a signed-in session: an unlisted pack answers at its own address, which is what
/// makes a link worth passing on.
///
/// Never throws. A pack update is the least urgent thing Cairn does and it runs behind a
/// window somebody is trying to play a game from.
/// </summary>
public static class PackUpdateCheck
{
    /// <summary>
    /// Whether this pack is one that could have updates at all: somebody else's, still
    /// followed, with an address to ask. Cheap and offline, so a caller can skip the
    /// request entirely.
    /// </summary>
    /// <summary>
    /// How often one pack's author is asked. A published revision is not urgent — an
    /// author ships one a week at most — and the cost of having no interval was paid by
    /// somebody else's server: selecting a pack asked, so clicking between two followed
    /// packs asked on every click, for ever.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);

    /// <summary>
    /// Whether this pack is worth asking about now. Cheap and offline, so the caller skips
    /// the request rather than making one and discarding it.
    /// </summary>
    public static bool IsDue(PackLocalState? state, DateTimeOffset? now = null) =>
        state?.LastChecked is not { } last
        || (now ?? DateTimeOffset.UtcNow) - last >= CheckInterval;

    public static bool CanCheck(PackLink? link) =>
        link is { Role: PackRole.Follower, Following: true }
        && !string.IsNullOrWhiteSpace(link.Url)
        && PackSources.IsRemote(link.Url)
        && !PackSources.IsRewritableInFlight(link.Url);

    /// <summary>
    /// The newer revision, or null for "no", "cannot ask" and "could not tell" alike —
    /// none of which is worth interrupting anybody over.
    /// </summary>
    public static async Task<PackUpdateAvailable?> CheckAsync(
        PackLink? link, HttpClient http, CancellationToken ct = default)
    {
        if (!CanCheck(link)) return null;

        PackBundle bundle;
        try
        {
            var json = await http.GetStringAsync(link!.Url, ct).ConfigureAwait(false);
            bundle = PackBundle.Parse(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or InvalidDataException or IOException)
        {
            // Withdrawn packs answer 410 here and land in the same place: an author who
            // took their pack down has not published a newer one.
            return null;
        }

        // A document that lost its canonical URL is not this pack any more, and a revision
        // that went backwards is a cache or a rollback rather than news.
        if (!bundle.IsPublished || bundle.Pack is null) return null;

        var latest = bundle.Revision ?? 0;

        return latest > link!.Revision
            ? new PackUpdateAvailable(link.Revision, latest, bundle)
            : null;
    }
}
