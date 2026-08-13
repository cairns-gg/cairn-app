using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What a revision is allowed to change without saying so.
///
/// Import treats a pack's connect address as worth a warning box of its own, because
/// launching joins it and the client hands over a session there. Taking a revision took it
/// silently and did not even count it as a change — so a revision altering nothing else
/// reported "matches the author's revision" and then rerouted the pack. Somebody who cannot
/// get a hostile address past the import screen publishes revision 2.
/// </summary>
public class UpdateDisclosureTests
{
    private static PackManifest Pack(string? connect = null,
        Dictionary<string, string>? keybinds = null) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "glassview" }],
        Connect = connect,
        Keybinds = keybinds,
    };

    private static PackUpdatePlan Between(PackManifest mine, PackManifest theirs) =>
        PackUpdatePlan.Between(mine, theirs, mine, 1, 2, null);

    [Fact]
    public void A_revision_that_only_moves_the_server_is_a_change()
    {
        var plan = Between(Pack(), Pack(connect: "evil.example:42420"));

        Assert.True(plan.ConnectChanges);
        Assert.True(plan.AnyChange);
    }

    [Fact]
    public void And_the_summary_names_the_address_rather_than_counting_it()
    {
        var plan = Between(Pack(), Pack(connect: "evil.example:42420"));

        Assert.Contains("evil.example:42420", plan.Summary());
    }

    [Fact]
    public void Losing_a_server_address_is_said_too()
    {
        var plan = Between(Pack(connect: "play.example:42420"), Pack());

        Assert.True(plan.ConnectChanges);
        Assert.Contains("stops joining a server", plan.Summary());
    }

    [Fact]
    public void A_changed_keybind_is_a_change()
    {
        var plan = Between(
            Pack(keybinds: new Dictionary<string, string> { ["walk"] = "W" }),
            Pack(keybinds: new Dictionary<string, string> { ["walk"] = "K" }));

        Assert.True(plan.KeybindsChange);
        Assert.True(plan.AnyChange);
        Assert.Contains("changes keybinds", plan.Summary());
    }

    /// <summary>
    /// And an identical revision is still nothing, or the dialog cries wolf on every check
    /// and stops being read — which is the failure this replaces, in the other direction.
    /// </summary>
    [Fact]
    public void An_identical_revision_still_changes_nothing()
    {
        var same = Between(
            Pack(connect: "play.example:42420",
                 keybinds: new Dictionary<string, string> { ["walk"] = "W" }),
            Pack(connect: "play.example:42420",
                 keybinds: new Dictionary<string, string> { ["walk"] = "W" }));

        Assert.False(same.ConnectChanges);
        Assert.False(same.KeybindsChange);
        Assert.False(same.AnyChange);
    }

    [Fact]
    public void A_server_address_differing_only_in_case_is_not_a_change()
    {
        var plan = Between(Pack(connect: "Play.Example:42420"), Pack(connect: "play.example:42420"));

        Assert.False(plan.ConnectChanges);
    }
}
