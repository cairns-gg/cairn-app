using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// ModDB's text search matches mod descriptions and returns them in no useful order:
/// searching "olla" yields 384 hits with the mod actually called Olla at position 194,
/// behind things like "Furio's Telescope" that merely contain the letters somewhere.
/// Ranking is therefore Cairn's job, and these pin it.
/// </summary>
public class ModSearchRankingTests
{
    private static ModDbSearchEntry Entry(string name, string? id = null, int downloads = 0,
        string? summary = null) => new()
    {
        Name = name,
        ModIdStrs = id is null ? [] : [id],
        Downloads = downloads,
        Summary = summary,
    };

    [Fact]
    public void An_exact_mod_id_wins_over_anything_else()
    {
        var results = ModDbClient.Rank(
        [
            Entry("Furio's Telescope", "telescopemod", 15, "collapsible telescope"),
            Entry("CollapseStory", "collapsestory", 9000),
            Entry("Olla", "olla", 2172),
        ], "olla");

        Assert.Equal("olla", results[0].ModIdStrs.Single());
    }

    [Fact]
    public void A_description_only_match_ranks_last_however_popular_it_is()
    {
        var results = ModDbClient.Rank(
        [
            Entry("Something Huge", "huge", 500_000, "mentions olla in passing"),
            Entry("Olla", "olla", 1),
        ], "olla");

        // Popularity must not outrank actually being the thing you asked for.
        Assert.Equal("Olla", results[0].Name);
    }

    [Fact]
    public void Prefix_matches_beat_substring_matches()
    {
        var results = ModDbClient.Rank(
        [
            Entry("UnChiseledPatch", "unchiseledpatch", 1629),
            Entry("Unchisel Option", "unchiseloption", 269),
            Entry("unchisel", "unchisel", 19448),
        ], "unchisel");

        Assert.Equal("unchisel", results[0].ModIdStrs.Single());
    }

    [Fact]
    public void Downloads_break_ties_within_the_same_relevance_band()
    {
        var results = ModDbClient.Rank(
        [
            Entry("Packrat Fork", "packratfork", 1248),
            Entry("Packrat Extras", "packratextras", 9000),
        ], "packrat");

        Assert.Equal("packratextras", results[0].ModIdStrs.Single());
    }

    [Theory]
    [InlineData("olla", "olla", 0)]          // exact id
    [InlineData("Olla", "somethingelse", 1)] // exact name
    [InlineData("ollamania", "ollamania", 2)]// id prefix
    [InlineData("Olla Extras", "extras", 3)] // name prefix
    [InlineData("x", "myollamod", 4)]        // id substring
    [InlineData("An Olla Thing", "thing", 5)]// name substring
    [InlineData("Unrelated", "unrelated", 6)]// description-only
    public void Relevance_bands_are_ordered_as_documented(string name, string id, int expected)
        => Assert.Equal(expected, ModDbClient.Relevance(Entry(name, id), "olla"));

    [Fact]
    public void Entries_without_a_mod_id_do_not_throw()
    {
        // ModDB returns some entries with an empty modidstrs array.
        var results = ModDbClient.Rank([Entry("TSearch"), Entry("Olla", "olla")], "olla");
        Assert.Equal("olla", results[0].ModIdStrs.Single());
    }

    [Fact]
    public void Ranking_keeps_every_result_rather_than_filtering()
    {
        var input = new[] { Entry("A", "a"), Entry("B", "b"), Entry("Olla", "olla") };
        Assert.Equal(input.Length, ModDbClient.Rank(input, "olla").Count);
    }
}
