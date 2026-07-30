using Xunit;

namespace Cairn.Core.Tests;

public class GameVersionComparerTests
{
    [Fact]
    public void Sorts_numerically_not_lexically()
    {
        var sorted = GameVersionComparer.Descending(["1.9.14", "1.10.0", "1.22.5", "1.2.0"]).ToList();

        // A lexical sort would put 1.9.14 above 1.10.0 and 1.22.5.
        Assert.Equal(["1.22.5", "1.10.0", "1.9.14", "1.2.0"], sorted);
    }

    [Fact]
    public void Pre_releases_rank_below_the_stable_release_they_lead_to()
    {
        var sorted = GameVersionComparer.Descending(
            ["1.22.0", "1.22.0-rc.1", "1.22.0-pre.5", "1.22.0-rc.10", "1.21.9"]).ToList();

        Assert.Equal(["1.22.0", "1.22.0-rc.10", "1.22.0-rc.1", "1.22.0-pre.5", "1.21.9"], sorted);
    }

    [Fact]
    public void Installed_and_published_versions_interleave_correctly()
    {
        // The bug this replaced: installed versions were listed first, so an older
        // installed version sat above newer published ones.
        var installed = new[] { "1.22.5", "1.21.5" };
        var published = new[] { "1.22.4", "1.22.3", "1.21.7" };

        var sorted = GameVersionComparer.Descending(installed.Concat(published)).ToList();

        Assert.Equal(["1.22.5", "1.22.4", "1.22.3", "1.21.7", "1.21.5"], sorted);
    }

    [Fact]
    public void Duplicates_and_equivalents_do_not_break_the_ordering()
    {
        var sorted = GameVersionComparer.Descending(["1.22", "1.22.0", "1.21.5"]).ToList();

        Assert.Equal(3, sorted.Count);
        Assert.Equal("1.21.5", sorted[^1]);
    }

    [Fact]
    public void Is_a_total_order_so_sorting_is_stable()
    {
        // Comparing only by "is newer" returns -1 both ways for equivalents, which makes
        // OrderBy's behaviour undefined.
        var c = GameVersionComparer.Ascending;

        Assert.Equal(0, c.Compare("1.22.5", "1.22.5"));
        Assert.True(c.Compare("1.22.5", "1.22.4") > 0);
        Assert.True(c.Compare("1.22.4", "1.22.5") < 0);
        Assert.Equal(-c.Compare("1.22.4", "1.22.5"), c.Compare("1.22.5", "1.22.4"));
    }

    [Fact]
    public void Nulls_and_junk_are_ordered_rather_than_throwing()
    {
        var sorted = GameVersionComparer.Descending(["1.22.5", "garbage", ""]).ToList();
        Assert.Equal(3, sorted.Count);
        Assert.Equal("1.22.5", sorted[0]);
    }
}
