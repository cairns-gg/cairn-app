using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>
/// A pack in one file, for handing to someone else.
///
/// Carries the manifest (what the pack asks for) and optionally the lockfile (exactly
/// what the author had installed). Including the lock is what makes a shared pack
/// reproducible rather than merely similar — without it a recipient resolves the newest
/// compatible release, which may not be the one the author tested.
/// </summary>
public sealed class PackBundle
{
    /// <summary>Bumped if the shape ever changes incompatibly.</summary>
    public const int CurrentFormat = 1;

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormat;
    [JsonPropertyName("pack")] public PackManifest? Pack { get; set; }
    [JsonPropertyName("lock")] public PackLock? Lock { get; set; }

    /// <summary>
    /// Stamped by a server on publish; absent from a file someone exported by hand. Read
    /// rather than ignored so the import dialog can say where a pack came from and who put
    /// it there — the questions worth answering before taking on somebody else's mod list.
    ///
    /// Never written back out. <see cref="Serialize"/> builds a bundle from the manifest
    /// and lock alone, so exporting a pack you imported does not re-issue it under its
    /// original author's name.
    /// </summary>
    [JsonPropertyName("publishedBy")] public string? PublishedBy { get; set; }

    /// <summary>Where the server said this lives. See <see cref="PublishedBy"/>.</summary>
    [JsonPropertyName("canonicalUrl")] public string? CanonicalUrl { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PackManifest manifest, PackLock? locked = null) =>
        JsonSerializer.Serialize(
            new PackBundle { Pack = manifest, Lock = locked }, Options);

    /// <exception cref="InvalidDataException">The text is not a usable pack bundle.</exception>
    public static PackBundle Parse(string json)
    {
        PackBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<PackBundle>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Not valid JSON: {e.Message}", e);
        }

        if (bundle?.Pack is null)
            throw new InvalidDataException("This does not look like a shared pack — no 'pack' section.");

        if (bundle.FormatVersion > CurrentFormat)
            throw new InvalidDataException(
                $"This pack was exported by a newer Cairn (format {bundle.FormatVersion}).");

        var problems = bundle.Pack.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException("The shared pack is not valid:\n  " + string.Join("\n  ", problems));

        return bundle;
    }

    /// <summary>
    /// Drops every manifest pin, including ones the author set deliberately, so the pack
    /// resolves newest-compatible instead of reproducing what the author had.
    ///
    /// This is the whole of a loose import. Reproduction needs no counterpart: the
    /// author's lock constrains sync on its own — <c>PackSyncer</c> applies a lock entry
    /// whenever the manifest asks for no particular version — and the download is checked
    /// against the author's SHA-256 either way.
    /// </summary>
    public void ClearPins()
    {
        if (Pack is null) return;

        foreach (var mod in Pack.Mods)
            mod.Version = null;
    }
}
