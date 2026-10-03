using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>The order somebody dragged the pack list into (cairns-gg/cairn-app#7).</summary>
public class PackOrderTests
{
    private static readonly string[] OnDisk = ["anego", "old-pack", "vanilla-qol"];

    [Fact]
    public void Nothing_arranged_yet_is_the_order_on_disk() =>
        Assert.Equal(OnDisk, PackOrder.Arrange(OnDisk, null));

    [Fact]
    public void An_arranged_order_is_kept()
    {
        string[] saved = ["vanilla-qol", "anego", "old-pack"];
        Assert.Equal(saved, PackOrder.Arrange(OnDisk, saved));
    }

    [Fact]
    public void A_pack_nobody_has_placed_goes_after_the_ones_they_have()
    {
        // Made or imported since the list was arranged: at the bottom, not shuffled in.
        Assert.Equal(["vanilla-qol", "anego", "old-pack"],
            PackOrder.Arrange(OnDisk, ["vanilla-qol", "anego"]));
    }

    [Fact]
    public void A_pack_that_has_gone_is_forgotten_and_case_does_not_matter()
    {
        Assert.Equal(["vanilla-qol", "anego", "old-pack"],
            PackOrder.Arrange(OnDisk, ["deleted-long-ago", "VANILLA-QOL", "anego", "anego"]));
    }

    [Theory]
    [InlineData("vanilla-qol", 0, new[] { "vanilla-qol", "anego", "old-pack" })]
    [InlineData("anego", 2, new[] { "old-pack", "vanilla-qol", "anego" })]
    [InlineData("anego", 99, new[] { "old-pack", "vanilla-qol", "anego" })]   // clamped
    [InlineData("missing", 0, new[] { "anego", "old-pack", "vanilla-qol" })]   // unchanged
    public void Moving_puts_the_pack_at_the_place_asked_for(string id, int index, string[] expected) =>
        Assert.Equal(expected, PackOrder.Move(OnDisk, id, index));
}
