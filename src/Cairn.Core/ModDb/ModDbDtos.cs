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
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>"client", "server" or "both".</summary>
    [JsonPropertyName("side")] public string? Side { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("releases")] public List<ModDbRelease> Releases { get; set; } = [];
}

public sealed class ModDbRelease
{
    [JsonPropertyName("releaseid")] public int ReleaseId { get; set; }
    [JsonPropertyName("fileid")] public int FileId { get; set; }

    /// <summary>Direct download URL, already carrying a ?dl= filename hint.</summary>
    [JsonPropertyName("mainfile")] public string MainFile { get; set; } = "";

    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("modidstr")] public string ModIdStr { get; set; } = "";
    [JsonPropertyName("modversion")] public string ModVersion { get; set; } = "";

    /// <summary>Game versions this release is marked compatible with, e.g. ["1.22.0", "1.22.5"].</summary>
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
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
}
