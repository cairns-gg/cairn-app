using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Core.Packs;

/// <summary>Which end of a shared pack this copy is.</summary>
public enum PackRole
{
    /// <summary>This copy was imported from cairns.gg; someone else publishes it.</summary>
    Follower,

    /// <summary>This copy is the one being published.</summary>
    Author,
}

/// <summary>What was sent the last time this pack was published.</summary>
public sealed class PublishRecord
{
    /// <summary>Hash of the published document, for spotting local changes since.</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";

    /// <summary>"public" or "unlisted".</summary>
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = "unlisted";

    /// <summary>"stripped" or "included" — whether the pack's server address was sent.</summary>
    [JsonPropertyName("connect")] public string Connect { get; set; } = "stripped";

    /// <summary>
    /// Whether publishing this document with these options would send anything new.
    ///
    /// The options count, not only the bytes. A pack republished with the same document
    /// but flipped from unlisted to public is a real change; one with the same document
    /// and the same choices is a revision differing from its predecessor in nothing but
    /// its number — and every follower is told there is an update that isn't one.
    /// </summary>
    public bool WouldChange(string publishedJson, bool @public, bool strip) =>
        !string.Equals(Fingerprint, PackLink.Fingerprint(publishedJson),
            StringComparison.OrdinalIgnoreCase)
        || Visibility != (@public ? "public" : "unlisted")
        || Connect != (strip ? "stripped" : "included");
}

/// <summary>
/// A pack's relationship to cairns.gg, as seen from this machine: where it came from, or
/// where it goes.
///
/// Deliberately not in <c>pack.json</c>. That file is shareable intent, so a link stored
/// there would travel — re-exporting a pack you imported would hand the next person a
/// manifest pointing at somebody else's canonical URL. Where your copy came from is a
/// property of your copy.
///
/// One file for both ends rather than two describing the same relationship in opposite
/// directions, so there is one place to look when asking what a pack's relationship to the
/// site is.
/// </summary>
public sealed class PackLink
{
    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter<PackRole>))]
    public PackRole Role { get; set; }

    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>The published revision this copy corresponds to.</summary>
    [JsonPropertyName("revision")] public int Revision { get; set; }

    /// <summary>
    /// Follower only: whether the author's revisions are still being taken. Cleared by
    /// Take over, which keeps <see cref="Url"/> so the pack can still say what it
    /// diverged from.
    /// </summary>
    [JsonPropertyName("following")] public bool Following { get; set; }

    /// <summary>Author only.</summary>
    [JsonPropertyName("published")] public PublishRecord? Published { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PackLink? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<PackLink>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // A pack whose link file is unreadable is still a working pack — it just does
            // not know where it came from. Losing that is not worth refusing to open it.
            return null;
        }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    /// <summary>
    /// Hashes what would be sent to the server, so "has this changed since I published it"
    /// can be answered without asking the server.
    ///
    /// Takes the serialized bundle rather than the pack on disk, because the two are not
    /// the same thing: a pack published with its server address stripped differs from its
    /// local manifest permanently, and comparing against the local copy would report
    /// unpublished changes forever.
    /// </summary>
    public static string Fingerprint(string publishedJson) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(publishedJson)));
}
