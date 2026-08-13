using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.Core.Tests;

public class ModDbUrlsTests
{
    [Fact]
    public void The_alias_is_used_when_there_is_one_because_it_reads_better()
        => Assert.Equal("https://mods.vintagestory.at/olla", ModDbUrls.Page(34157, "olla"));

    [Fact]
    public void The_asset_id_carries_the_link_when_there_is_no_alias()
    {
        // Measured against the live API: 110 of 384 results for one query had no alias,
        // so an alias-only link would silently 404 for about a quarter of all mods.
        Assert.Equal("https://mods.vintagestory.at/show/mod/61959", ModDbUrls.Page(61959));
        Assert.Equal("https://mods.vintagestory.at/show/mod/61959", ModDbUrls.Page(61959, ""));
        Assert.Equal("https://mods.vintagestory.at/show/mod/61959", ModDbUrls.Page(61959, "   "));
    }

    [Fact]
    public void An_entry_identifying_no_page_gets_no_link()
        => Assert.Null(ModDbUrls.Page(0));

    [Fact]
    public void An_alias_is_escaped()
    {
        // Aliases come from a remote API and end up in a URL handed to the OS.
        Assert.Equal("https://mods.vintagestory.at/a%20b", ModDbUrls.Page(1, "a b"));
        Assert.DoesNotContain(" ", ModDbUrls.Page(1, "some mod")!);
    }

    [Fact]
    public void It_reads_a_search_entry_and_a_mod_the_same_way()
    {
        var entry = new ModDbSearchEntry { AssetId = 34157, UrlAlias = "olla" };
        var mod = new ModDbMod { AssetId = 34157, UrlAlias = "olla" };

        Assert.Equal("https://mods.vintagestory.at/olla", ModDbUrls.Page(entry));
        Assert.Equal(ModDbUrls.Page(entry), ModDbUrls.Page(mod));
    }

    // ---- where a mod may be downloaded from ----

    [Theory]
    [InlineData("https://moddbcdn.vintagestory.at/olla_1.0.0.zip")]
    [InlineData("https://mods.vintagestory.at/download.php?fileid=1")]
    [InlineData("https://MODDBCDN.vintagestory.at/olla.zip")]
    public void A_CDN_url_has_nothing_wrong_with_it(string url)
    {
        Assert.Null(ModDbUrls.DownloadProblem(url));
        Assert.True(ModDbUrls.IsKnownDownloadHost(url));
    }

    /// <summary>
    /// The two reasons are told apart so a sync log distinguishes an attack from a host
    /// list that has gone stale — the message names the host, which is what somebody
    /// would report.
    /// </summary>
    [Fact]
    public void Plaintext_and_an_unknown_host_are_different_complaints()
    {
        var plaintext = ModDbUrls.DownloadProblem("http://moddbcdn.vintagestory.at/olla.zip");
        var unknown = ModDbUrls.DownloadProblem("https://attacker.example/payload.zip");

        Assert.Contains("https", plaintext);
        Assert.Contains("attacker.example", unknown);
        Assert.NotEqual(plaintext, unknown);
    }

    [Theory]
    [InlineData("https://moddbcdn.vintagestory.at@attacker.example/x.zip")]
    [InlineData("https://moddbcdn.vintagestory.at.attacker.example/x.zip")]
    [InlineData("https://moddbcdn.vintagestory.at./x.zip")]
    [InlineData("ftp://moddbcdn.vintagestory.at/x.zip")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused(string? url)
    {
        Assert.NotNull(ModDbUrls.DownloadProblem(url));
        Assert.False(ModDbUrls.IsKnownDownloadHost(url));
    }

    /// <summary>
    /// The two answers are one decision. IsKnownDownloadHost is the predicate form of
    /// DownloadProblem, so a caller cannot get a different answer depending on which it
    /// happened to ask.
    /// </summary>
    [Theory]
    [InlineData("https://moddbcdn.vintagestory.at/x.zip")]
    [InlineData("http://moddbcdn.vintagestory.at/x.zip")]
    [InlineData("https://attacker.example/x.zip")]
    [InlineData("")]
    public void The_predicate_and_the_reason_always_agree(string url) =>
        Assert.Equal(ModDbUrls.IsKnownDownloadHost(url), ModDbUrls.DownloadProblem(url) is null);
}
