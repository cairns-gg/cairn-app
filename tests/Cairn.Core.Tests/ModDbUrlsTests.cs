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
}
