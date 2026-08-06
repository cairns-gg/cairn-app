using Cairn.App.ViewModels;
using Cairn.Core.ModDb;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Choosing which version a pack pins.
///
/// The window replaced a combo box on every row, so what it has to earn is the extra click:
/// it lands on the version somebody almost certainly wants, and each row says the thing a
/// version number alone cannot — which game versions it is marked for, and how old it is.
/// </summary>
public class PinVersionTests
{
    private static ResolvedRelease Release(
        string version, string[]? gameVersions = null, string? created = null) =>
        new("olla", version, $"olla_{version}.zip", "https://example/x.zip", 1, 2,
            MatchQuality.Exact, "client", null, gameVersions, created);

    private static PinVersionViewModel Choose(
        string? pinned, string? installed, params ResolvedRelease[] releases) =>
        new("olla", "Olla", pinned, installed, releases, "1.22.5");

    [Fact]
    public void Follow_is_offered_first_and_is_not_a_version()
    {
        var vm = Choose(null, null, Release("2.0.0"));

        // In the old dropdown "latest" sat among the versions looking like one of them.
        Assert.True(vm.Choices[0].IsTrackNewest);
        Assert.Contains(vm.Choices.Where(c => !c.IsTrackNewest), c => c.Version == "2.0.0");
    }

    [Fact]
    public void It_lands_on_what_is_installed()
    {
        var vm = Choose(pinned: null, installed: "1.5.0", Release("2.0.0"), Release("1.5.0"));

        // Pinning is nearly always "freeze what works", so the common case is a confirm
        // rather than a hunt — which is what pays for the window being a window.
        Assert.Equal("1.5.0", vm.Selected!.Version);
        Assert.Equal("1.5.0", vm.Result);
    }

    [Fact]
    public void An_existing_pin_wins_over_what_is_installed()
    {
        // They differ whenever a pin was changed and not yet synced, and the window is
        // about the pin.
        var vm = Choose(pinned: "2.0.0", installed: "1.5.0", Release("2.0.0"), Release("1.5.0"));

        Assert.Equal("2.0.0", vm.Selected!.Version);
    }

    [Fact]
    public void With_nothing_installed_it_lands_on_following()
    {
        var vm = Choose(null, null, Release("2.0.0"), Release("1.5.0"));

        // Guessing a version for somebody who has never installed this mod would be Cairn
        // choosing, and the pack does not need a pin to work.
        Assert.True(vm.Selected!.IsTrackNewest);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void The_installed_release_is_marked()
    {
        var vm = Choose(null, "1.5.0", Release("2.0.0"), Release("1.5.0"));

        Assert.Equal("installed now", vm.Choices.Single(c => c.Version == "1.5.0").Note);
        Assert.Equal("", vm.Choices.Single(c => c.Version == "2.0.0").Note);
    }

    [Fact]
    public void A_release_says_which_game_versions_it_is_for()
    {
        var vm = Choose(null, null, Release("2.0.0", ["1.22.0", "1.22.4", "1.22.2"]));

        // A range rather than the list: releases routinely carry five or six tags, and the
        // question is whether it covers the pack's version.
        Assert.Equal("for 1.22.0 – 1.22.4", vm.Choices.Single(c => !c.IsTrackNewest).GameVersions);
    }

    [Fact]
    public void One_game_version_is_not_written_as_a_range()
    {
        var vm = Choose(null, null, Release("2.0.0", ["1.22.5"]));

        Assert.Equal("for 1.22.5", vm.Choices.Single(c => !c.IsTrackNewest).GameVersions);
    }

    [Fact]
    public void A_release_with_no_tags_says_nothing_rather_than_something_empty()
    {
        var vm = Choose(null, null, Release("2.0.0"));

        var row = vm.Choices.Single(c => !c.IsTrackNewest);
        Assert.False(row.HasGameVersions);
        Assert.False(row.HasWhen);
    }

    [Fact]
    public void A_date_it_cannot_read_is_left_out()
    {
        // ModDB has been willing to put surprises in fields that look reliable; a row
        // saying nothing beats a row saying "unknown".
        var vm = Choose(null, null, Release("2.0.0", null, "not a date"));

        Assert.False(vm.Choices.Single(c => !c.IsTrackNewest).HasWhen);
    }

    [Fact]
    public void A_recent_release_is_described_in_words()
    {
        var when = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd HH:mm:ss");
        var vm = Choose(null, null, Release("2.0.0", null, when));

        Assert.Equal("3 days ago", vm.Choices.Single(c => !c.IsTrackNewest).When);
    }

    [Fact]
    public void Nothing_to_choose_from_says_so_rather_than_showing_an_empty_list()
    {
        var vm = Choose(null, null);

        Assert.False(vm.HasReleases);
        Assert.Contains("1.22.5", vm.EmptyNote);
    }

    [Fact]
    public void The_confirm_button_says_what_it_will_do()
    {
        var vm = Choose(null, "1.5.0", Release("2.0.0"), Release("1.5.0"));

        Assert.Equal("Pin this version", vm.ConfirmLabel);

        // Following is a legitimate thing to choose here, so the button says so rather
        // than the window forbidding it — "Pin this version" would be the opposite of
        // what pressing it does.
        vm.Selected = vm.Choices.Single(c => c.IsTrackNewest);

        Assert.Equal("Follow this mod", vm.ConfirmLabel);
        Assert.Null(vm.Result);
    }
}
