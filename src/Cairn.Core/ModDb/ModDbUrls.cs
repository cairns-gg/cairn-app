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
}
