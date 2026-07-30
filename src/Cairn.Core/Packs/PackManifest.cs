using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>A mod the pack asks for. Version is optional; when set it is an exact pin.</summary>
public sealed class PackMod
{
    [JsonPropertyName("modid")] public string ModId { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary>
/// Declared intent, hand-editable and meant to be committed and shared: which mods,
/// for which game version, and optionally which server this pack is for.
/// Exact resolved versions live in <see cref="PackLock"/>, not here.
/// </summary>
public sealed class PackManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Game version to resolve against, e.g. "1.22.5".</summary>
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";

    /// <summary>Optional "host:port" — lets a pack launch straight into its server.</summary>
    [JsonPropertyName("connect")] public string? Connect { get; set; }

    [JsonPropertyName("mods")] public List<PackMod> Mods { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            yield return "Pack 'id' is required.";

        if (!GameVersions.IsPlausibleVersion(GameVersion))
            yield return $"Pack 'gameVersion' is not a usable version string: '{GameVersion}'. "
                         + "Write a bare version like \"1.22.5\" — the game silently reads "
                         + "\">=1.22.5\" as major version 0, which matches everything.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mods)
        {
            if (string.IsNullOrWhiteSpace(m.ModId))
            {
                yield return "A mod entry has an empty 'modid'.";
                continue;
            }

            if (!seen.Add(m.ModId))
                yield return $"'{m.ModId}' is listed more than once.";

            if (m.Version is not null && !GameVersions.IsPlausibleVersion(m.Version))
                yield return $"'{m.ModId}' has an unusable version pin '{m.Version}'. "
                             + "Pins must be bare versions like \"1.3.0\".";
        }
    }

    /// <summary>
    /// Synchronous by design. Manifests are small local files, and callers include UI
    /// constructors — an async load there invites sync-over-async deadlocks on the
    /// Avalonia UI thread.
    /// </summary>
    public static PackManifest Load(string path)
        => JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidDataException($"{path} is empty or not a pack manifest.");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public sealed class LockedMod
{
    [JsonPropertyName("modid")] public string ModId { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseId")] public int ReleaseId { get; set; }
    [JsonPropertyName("fileId")] public int FileId { get; set; }

    /// <summary>Computed by Cairn on first download; ModDB publishes no hash.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    [JsonPropertyName("side")] public string? Side { get; set; }
}

/// <summary>
/// Exactly what was installed, so a pack reproduces byte-for-byte for anyone who
/// clones it. Generated — edit the manifest instead.
/// </summary>
public sealed class PackLock
{
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("mods")] public List<LockedMod> Mods { get; set; } = [];

    public static PackLock? Load(string path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<PackLock>(File.ReadAllText(path), PackManifest.JsonOptions)
            : null;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, PackManifest.JsonOptions));
    }
}
