namespace Cairn.Core.ModDb;

/// <summary>Links back to the ModDB site, for showing a mod's own page.</summary>
public static class ModDbUrls
{
    public const string Site = "https://mods.vintagestory.at";

    /// <summary>
    /// The mod's page, or null if the entry identifies no page at all.
    ///
    /// Keyed on the asset id rather than the url alias: every mod has an asset id, but
    /// roughly a quarter have no alias, so an alias-only link would silently 404 for
    /// them. The alias is used when present purely because it reads better.
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
    /// This gates attacker-supplied input. A shared pack carries its own lockfile, and
    /// following a URL out of one would let the pack choose where a mod is fetched from,
    /// into the directory handed to the game. Mods are code.
    ///
    /// Deliberately not the only defence: ModDB's CDN host is a config value in its own
    /// source rather than a constant, so this list can go stale. A caller that fails this
    /// check should resolve the mod again rather than refuse it.
    /// </summary>
    public static bool IsKnownDownloadHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
