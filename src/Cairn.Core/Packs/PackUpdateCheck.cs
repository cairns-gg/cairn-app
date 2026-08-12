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
        var bundle = await FetchAsync(link, http, ct).ConfigureAwait(false);
        if (bundle is null) return null;

        var latest = bundle.Revision ?? 0;

        // A revision that went backwards is a cache or a rollback rather than news.
        return latest > link!.Revision
            ? new PackUpdateAvailable(link.Revision, latest, bundle)
            : null;
    }

    /// <summary>
    /// The author's pack as it stands, whether or not it is newer than this copy.
    ///
    /// Split from <see cref="CheckAsync"/> because "is there an update" and "what does
    /// their pack look like" are different questions, and only the first one cares about
    /// the revision. Somebody who has edited a copy and wants it back is asking the second:
    /// they are already on the latest revision, so a check would say no and leave them with
    /// no way to reconcile a pack that has visibly diverged.
    /// </summary>
    /// <summary>
    /// The document behind a pack's page.
    ///
    /// A pack's canonical URL is where a person reads about it, and that address serves
    /// HTML. The bundle is the same address with <c>.json</c> on the end. Fetching the
    /// canonical URL directly got a web page, failed to parse, and reported it as the
    /// author being unreachable — which is what "could not reach the author's pack" meant
    /// every single time, for every pack, including ones whose server was perfectly well.
    ///
    /// A URL that already ends in <c>.json</c> is left alone, so a document served
    /// directly — a file on a static host, a dev server on loopback — still works.
    /// </summary>
    public static string DocumentUrl(string url)
    {
        var trimmed = url.TrimEnd('/');

        return trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ".json";
    }

    /// <summary>
    /// The inverse: the page a person reads, from the address a machine fetched.
    ///
    /// Recorded rather than the document URL because a pack's link is shown to whoever
    /// holds it and is the address they would open — and because the slug is read back out
    /// of it by taking the last segment, which ".json" would quietly become part of.
    /// </summary>
    public static string PageUrl(string documentUrl)
    {
        var trimmed = documentUrl.TrimEnd('/');

        return trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^".json".Length]
            : trimmed;
    }

    public static async Task<PackBundle?> FetchAsync(
        PackLink? link, HttpClient http, CancellationToken ct = default)
    {
        if (!CanCheck(link)) return null;

        PackBundle bundle;
        try
        {
            var json = await http.GetStringAsync(DocumentUrl(link!.Url), ct).ConfigureAwait(false);
            bundle = PackBundle.Parse(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or InvalidDataException or IOException)
        {
            // Withdrawn packs answer 410 here and land in the same place: there is nothing
            // at that address to reconcile against.
            return null;
        }

        // A document that lost its canonical URL is not this pack any more.
        return bundle.IsPublished && bundle.Pack is not null ? bundle : null;
    }
}
