using System.Text.Json.Serialization;

namespace Cairn.Core.ModDb;

// Shapes below mirror the public ModDB API at https://mods.vintagestory.at/api/*.
// Only the fields Cairn relies on are modelled; the API returns considerably more.

public sealed class ModDbModResponse
{
    [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
    [JsonPropertyName("mod")] public ModDbMod? Mod { get; set; }
}

public sealed class ModDbMod
{
    [JsonPropertyName("modid")] public int NumericId { get; set; }

    /// <summary>Identifies the mod's page on the site; present on every mod.</summary>
    [JsonPropertyName("assetid")] public int AssetId { get; set; }

    /// <summary>A prettier page slug. Often absent, so it can only ever be a preference.</summary>
    [JsonPropertyName("urlalias")] public string? UrlAlias { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>Icon URL on the ModDB CDN. Not every mod has one.</summary>
    [JsonPropertyName("logofile")] public string? Logo { get; set; }

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];

    /// <summary>"client", "server" or "both".</summary>
    [JsonPropertyName("side")] public string? Side { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("releases")] public List<ModDbRelease> Releases { get; set; } = [];
}

public sealed class ModDbRelease
{
    /// <summary>
    /// Nullable because ModDB keeps the row for a release whose file has gone — jaunt
    /// 3.0.0-rc.1 is served as <c>"fileid": null</c> with an empty <c>mainfile</c>. As a
    /// plain int that one row made the whole mod undeserialisable, so every mod requiring
    /// jaunt failed mid-sync with a JSON error instead of installing.
    ///
    /// Both ids are recorded in the lock for provenance and nothing resolves against them,
    /// so a missing one costs nothing; <see cref="MainFile"/> is what says a release is
    /// actually installable.
    /// </summary>
    [JsonPropertyName("releaseid")] public int? ReleaseId { get; set; }

    [JsonPropertyName("fileid")] public int? FileId { get; set; }

    /// <summary>
    /// Direct download URL, already carrying a ?dl= filename hint. Empty on a release
    /// whose file is gone, which is the only reliable marker of one.
    /// </summary>
    [JsonPropertyName("mainfile")] public string MainFile { get; set; } = "";

    [JsonPropertyName("filename")] public string FileName { get; set; } = "";

    /// <summary>
    /// The mod's own id, which a release need not carry: ModDB serves
    /// <c>"modidstr": null</c> for a listing that has no modid to give — Cairn's own entry
    /// is one, being a download link rather than a mod.
    ///
    /// Nullable because the <c>= ""</c> default only ever applied when the key was absent;
    /// an explicit null overwrote it, so the non-nullable declaration was a promise the
    /// deserialiser had already broken. Callers must fall back to the mod's name.
    /// </summary>
    [JsonPropertyName("modidstr")] public string? ModIdStr { get; set; }
    [JsonPropertyName("modversion")] public string ModVersion { get; set; } = "";

    /// <summary>Game versions this release is marked compatible with, e.g. ["1.22.0", "1.22.5"].</summary>
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}

/// <summary>One game version as ModDB knows it; searching by version needs its tag id.</summary>
public sealed class ModDbGameVersion
{
    /// <summary>A large negative number in practice, hence long rather than int.</summary>
    [JsonPropertyName("tagid")] public long TagId { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class ModDbGameVersionsResponse
{
    [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
    [JsonPropertyName("gameversions")] public List<ModDbGameVersion> GameVersions { get; set; } = [];
}

public sealed class ModDbSearchResponse
{
    [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
    [JsonPropertyName("mods")] public List<ModDbSearchEntry> Mods { get; set; } = [];
}

public sealed class ModDbSearchEntry
{
    [JsonPropertyName("modid")] public int NumericId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("downloads")] public int Downloads { get; set; }
    [JsonPropertyName("modidstrs")] public List<string> ModIdStrs { get; set; } = [];

    /// <summary>Identifies the mod's page on the site; present on every search result.</summary>
    [JsonPropertyName("assetid")] public int AssetId { get; set; }

    /// <summary>A prettier page slug, absent for two mods in five — measured, see ModDbUrls.Page.</summary>
    [JsonPropertyName("urlalias")] public string? UrlAlias { get; set; }

    /// <summary>Icon URL on the ModDB CDN. Roughly one mod in ten has none.</summary>
    [JsonPropertyName("logo")] public string? Logo { get; set; }

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}
