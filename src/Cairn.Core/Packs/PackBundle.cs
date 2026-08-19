using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Which published revision this copy is. See <see cref="PublishedBy"/>.</summary>
    [JsonPropertyName("revision")] public int? Revision { get; set; }

    /// <summary>
    /// Whether this came off a server rather than out of a file somebody exported.
    ///
    /// The distinction decides whether importing it makes you a follower of someone else's
    /// pack or simply gives you a copy: a document with a canonical URL has an owner, and
    /// somewhere to check back with.
    /// </summary>
    [JsonIgnore]
    public bool IsPublished => !string.IsNullOrWhiteSpace(CanonicalUrl);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Whether two bundles describe the same pack: the same manifest and the same lock,
    /// whatever else they carry.
    ///
    /// The envelope is excluded because it is the server's to write — publishedBy, the
    /// canonical URL, the revision — and comparing it would report every fetched document as
    /// different from every local one. Compared structurally rather than as text, so a key
    /// order that differs between two writers of the same JSON is not a difference: it is
    /// the same pack, said in a different order, and treating that as a change is how a
    /// revision gets published that has nothing in it for anybody.
    /// </summary>
    public bool SameContentAs(PackBundle other) => JsonNode.DeepEquals(
        JsonNode.Parse(Serialize(Pack ?? new PackManifest(), Lock)),
        JsonNode.Parse(Serialize(other.Pack ?? new PackManifest(), other.Lock)));

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
            throw new InvalidDataException(Lang.Get("bundle-not-json", e.Message), e);
        }

        if (bundle?.Pack is null)
            throw new InvalidDataException(Lang.Get("bundle-no-pack-section"));

        if (bundle.FormatVersion > CurrentFormat)
            throw new InvalidDataException(Lang.Get("bundle-newer-format", bundle.FormatVersion));

        var problems = bundle.Pack.Validate().ToList();
        if (problems.Count > 0)
            throw new InvalidDataException(Lang.Get("bundle-invalid") + "\n  " + string.Join("\n  ", problems));

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
