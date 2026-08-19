using System.Buffers;
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

/// <summary>
/// What somebody decided a copy of a published pack is, when they imported it.
///
/// The question only exists for a document that came off a server: it names an owner and
/// an address, and taking it on could mean either "keep me in step with theirs" or "this
/// is where mine starts". Cairn used to answer it by itself — always the first — which
/// left no way to make a pack of your own out of somebody else's, and no way back, since
/// taking over is specced in TODO.md and unimplemented.
///
/// It is also the point where the document's own claim about where it lives either does
/// or does not get acted on, which is why the choice belongs to a person and not to a
/// default. See <see cref="PackStore.Import"/>.
/// </summary>
public enum ImportIntent
{
    /// <summary>Somebody else's pack; this copy is kept in step with theirs.</summary>
    Follow,

    /// <summary>
    /// The start of a pack of your own. No owner, nothing to check back with, and yours to
    /// publish or export — which is the whole difference.
    /// </summary>
    Fork,
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

    /// <summary>
    /// Author only: taken down from the site, with <see cref="Published"/> cleared and
    /// <see cref="Url"/> kept.
    ///
    /// Recorded rather than inferred from the absence of a publish record, because a
    /// taken-over pack looks identical from that angle — Author, a URL, nothing published
    /// — and the two mean opposite things. This one had an address and gave it up; that
    /// one never had one.
    ///
    /// The server treats an author's withdrawal as reversible: publishing again clears
    /// the tombstone and revives the pack at the same address. So this is a state to come
    /// back from, not an ending, and the next publish writes a link without it.
    /// </summary>
    [JsonPropertyName("withdrawn")] public bool Withdrawn { get; set; }

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
    ///
    /// Hashed over the document's shape rather than its bytes — see <see cref="Canonical"/>.
    /// A hash of the bytes answers "were these written the same way", and the question being
    /// asked is "is this the same pack". The two came apart twice in one afternoon: a
    /// property that stopped being serialised, and a dictionary rebuilt in a different key
    /// order. Both moved every published pack to "Publish changes" over nothing, and
    /// publishing to settle one issues a revision identical to its predecessor — an update
    /// every follower is told about and none of them gets anything from.
    /// </summary>
    public static string Fingerprint(string publishedJson) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(publishedJson))));

    /// <summary>
    /// The same document, written one way: object keys in order, no whitespace.
    ///
    /// Arrays keep the order they came in, because there it is the author's — the mod list
    /// is a list somebody arranged, and sorting it would call two different packs the same.
    /// Only objects are reordered, where JSON gives the order no meaning and the writer
    /// picks it: a Dictionary hands them back in insertion order, so where a value was
    /// rebuilt rather than edited the keys move without anything about the pack changing.
    ///
    /// Anything that will not parse is hashed as it stands. This is only ever handed Cairn's
    /// own serialisation, so that is unreachable — and a fingerprint is not the place to
    /// discover it, since throwing here would take out the Share button rather than
    /// reporting anything.
    /// </summary>
    private static string Canonical(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer)) Write(document.RootElement, writer);

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json;
        }

        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var property in element.EnumerateObject()
                                 .OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
