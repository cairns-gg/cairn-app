namespace Cairn.Core.ModDb;

/// <summary>Links back to the ModDB site, for showing a mod's own page.</summary>
public static class ModDbUrls
{
    public const string Site = "https://mods.vintagestory.at";

    /// <summary>
    /// The mod's page, or null if the entry identifies no page at all.
    ///
    /// Keyed on the asset id rather than the url alias: every mod has an asset id, but
    /// two in five have no alias, so an alias-only link would silently 404 for them. The
    /// alias is used when present purely because it reads better.
    ///
    /// That figure was "roughly a quarter" until tools/moddb-audit.cs read all 7,904 mods
    /// and found 3,175 without one. The decision is unchanged and the code was always
    /// right; the number mattered because it is the stated reason, and at a quarter this
    /// looks like a wrinkle worth optimising away rather than the majority-adjacent case
    /// it is.
    /// </summary>
    public static string? Page(int assetId, string? urlAlias = null)
    {
        if (!string.IsNullOrWhiteSpace(urlAlias))
            return $"{Site}/{Uri.EscapeDataString(urlAlias.Trim())}";

        return assetId > 0 ? $"{Site}/show/mod/{assetId}" : null;
    }

    public static string? Page(ModDbSearchEntry entry) => Page(entry.AssetId, entry.UrlAlias);

    public static string? Page(ModDbMod mod) => Page(mod.AssetId, mod.UrlAlias);

    /// <summary>Hosts ModDB is known to serve mod downloads from.</summary>
    private static readonly string[] DownloadHosts =
    [
        "moddbcdn.vintagestory.at",   // the CDN every release URL currently points at
        "mods.vintagestory.at",       // download.php, which redirects to the CDN
    ];

    /// <summary>
    /// Whether a download URL points somewhere ModDB actually serves files from.
    ///
    /// Deliberately NOT what keeps somebody else's lockfile from choosing where a mod is
    /// fetched from — it cannot be, and reading it that way was a real hole. A host
    /// allowlist answers "is this a host ModDB uses", never "did ModDB give us this URL",
    /// and since anyone may upload a mod, anyone may put a file on that host. Provenance
    /// is enforced by clearing those fields on the way in instead; see
    /// <see cref="Packs.PackLock.ClearResolvedLocations"/>.
    ///
    /// What this is for is staleness: ModDB's CDN host is a config value in its own source
    /// rather than a constant, so a URL this machine wrote down last week may point at a
    /// host that has since stopped serving. A caller that fails this check should resolve
    /// the mod again rather than refuse it.
    /// </summary>
    public static bool IsKnownDownloadHost(string? url) => DownloadProblem(url) is null;

    /// <summary>
    /// Why this URL is not one to fetch a mod from, phrased to finish "refusing a download
    /// that …", or null when there is nothing wrong with it.
    ///
    /// The reasons are kept apart because they mean different things to whoever reads a
    /// sync log. Plaintext transport is a fault in the URL itself and is never acceptable.
    /// An unfamiliar host, on the other hand, is as likely to mean this list has gone stale
    /// as it is to mean an attack — ModDB's CDN host is a config value in its own source
    /// rather than a constant — so the message names the host, which is what tells the two
    /// apart and what somebody would report.
    /// </summary>
    public static string? DownloadProblem(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "does not give a usable address";

        if (uri.Scheme != Uri.UriSchemeHttps)
            return $"arrives over {uri.Scheme} rather than https, where anybody on the "
                   + "network path could replace it";

        return DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"comes from {uri.Host}, which is not a host ModDB is known to serve mods from";
    }
}
