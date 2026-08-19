using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What "has this pack changed since I published it" is answered with.
///
/// A hash of the bytes answers a different question — "were these written the same way" —
/// and the two came apart twice in one afternoon: a property that stopped being serialised,
/// and a dictionary rebuilt in a different key order. Each moved every published pack to
/// "Publish changes" over nothing, and settling that by publishing issues a revision
/// identical to its predecessor: an update every follower is told about and none of them
/// gets anything from.
/// </summary>
public class FingerprintTests
{
    [Fact]
    public void The_same_document_written_two_ways_fingerprints_the_same()
    {
        var a = """{"formatVersion":1,"pack":{"id":"anego","gameVersion":"1.22.5"}}""";
        var b = """
            {
              "pack": { "gameVersion": "1.22.5", "id": "anego" },
              "formatVersion": 1
            }
            """;

        Assert.Equal(PackLink.Fingerprint(a), PackLink.Fingerprint(b));
    }

    /// <summary>
    /// The case that started it: a dictionary rebuilt rather than edited hands its keys back
    /// in a different order, and nothing about the pack has changed.
    /// </summary>
    [Fact]
    public void Mod_settings_in_a_different_order_are_the_same_pack()
    {
        var a = """{"pack":{"modConfig":{"f.json":{"first":1,"second":2}}}}""";
        var b = """{"pack":{"modConfig":{"f.json":{"second":2,"first":1}}}}""";

        Assert.Equal(PackLink.Fingerprint(a), PackLink.Fingerprint(b));
    }

    /// <summary>
    /// And the mod list is not reordered, because there the order is the author's. Two packs
    /// holding the same mods in a different order are two different documents, and calling
    /// them one would hide a change somebody made on purpose.
    /// </summary>
    [Fact]
    public void A_mod_list_in_a_different_order_is_a_different_pack()
    {
        var a = """{"pack":{"mods":[{"modid":"carryon"},{"modid":"scribe"}]}}""";
        var b = """{"pack":{"mods":[{"modid":"scribe"},{"modid":"carryon"}]}}""";

        Assert.NotEqual(PackLink.Fingerprint(a), PackLink.Fingerprint(b));
    }

    [Fact]
    public void A_value_that_actually_changed_still_changes_the_fingerprint()
    {
        var a = """{"pack":{"modConfig":{"f.json":{"first":1}}}}""";
        var b = """{"pack":{"modConfig":{"f.json":{"first":2}}}}""";

        Assert.NotEqual(PackLink.Fingerprint(a), PackLink.Fingerprint(b));
    }

    /// <summary>
    /// Whitespace is not a change either, which is what makes this robust to the serialiser
    /// being reconfigured rather than only to the fields it writes.
    /// </summary>
    [Fact]
    public void Indentation_is_not_a_change()
    {
        Assert.Equal(
            PackLink.Fingerprint("""{"a":1,"b":[1,2]}"""),
            PackLink.Fingerprint("{\n  \"a\": 1,\n  \"b\": [ 1, 2 ]\n}"));
    }

    /// <summary>
    /// Something that will not parse is hashed as it stands. Unreachable, since this is only
    /// ever handed Cairn's own serialisation — but a fingerprint is no place to discover
    /// otherwise, because throwing here takes out the Share button rather than reporting
    /// anything.
    /// </summary>
    [Fact]
    public void Something_that_is_not_json_still_fingerprints()
    {
        Assert.StartsWith("sha256:", PackLink.Fingerprint("not json at all"));
        Assert.NotEqual(PackLink.Fingerprint("not json"), PackLink.Fingerprint("also not json"));
    }
}
