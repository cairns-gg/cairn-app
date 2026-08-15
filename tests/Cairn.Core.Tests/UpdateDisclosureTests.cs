using System.Text.Json.Nodes;
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
        Dictionary<string, string>? keybinds = null, string? modConfig = null) => new()
    {
        Id = "anego",
        GameVersion = "1.22.5",
        Mods = [new PackMod { ModId = "glassview" }],
        Connect = connect,
        Keybinds = keybinds,
        ModConfig = modConfig is null ? null
            : new Dictionary<string, JsonObject>
            {
                ["watersheds.yaml"] = (JsonNode.Parse(modConfig) as JsonObject)!,
            },
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
    /// The same defect a third time. Mod config arrived after this check was written and was
    /// left out of it, so an author who published a revision changing nothing but a mod
    /// setting — which is a normal thing to publish — was told by every follower that they
    /// were already on the newest revision. A server sat on revision 10 against an upstream
    /// 11 and exited 0 about it.
    /// </summary>
    [Fact]
    public void A_revision_that_only_changes_mod_config_is_a_change()
    {
        var plan = Between(
            Pack(modConfig: """{ "flow_multiplier": 1 }"""),
            Pack(modConfig: """{ "flow_multiplier": 4 }"""));

        Assert.True(plan.ModConfigChanges);
        Assert.True(plan.AnyChange);
        Assert.Contains("changes mod settings", plan.Summary());
    }

    [Fact]
    public void Mod_config_appearing_or_going_away_is_a_change_too()
    {
        Assert.True(Between(Pack(), Pack(modConfig: """{ "flow_multiplier": 4 }""")).ModConfigChanges);
        Assert.True(Between(Pack(modConfig: """{ "flow_multiplier": 4 }"""), Pack()).ModConfigChanges);
    }

    /// <summary>
    /// The half that actually loses data. Merge names every field it carries across, and a
    /// field left out empties on every update — which the comment there already said, having
    /// been written when the pack's hotkeys were lost that way. Mod config was added without
    /// being named, so taking any revision at all threw away everything the pack carried.
    /// </summary>
    [Fact]
    public void An_update_carries_the_authors_mod_config_across_rather_than_emptying_it()
    {
        var mine = Pack(modConfig: """{ "flow_multiplier": 1 }""");
        var theirs = Pack(modConfig: """{ "flow_multiplier": 4 }""");
        theirs.GameVersion = "1.22.6";

        var merged = Between(mine, theirs).Merge();

        Assert.NotNull(merged.ModConfig);
        Assert.Equal(4, merged.ModConfig!["watersheds.yaml"]["flow_multiplier"]!.GetValue<int>());
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
                 keybinds: new Dictionary<string, string> { ["walk"] = "W" },
                 modConfig: """{ "flow_multiplier": 1 }"""),
            Pack(connect: "play.example:42420",
                 keybinds: new Dictionary<string, string> { ["walk"] = "W" },
                 modConfig: """{ "flow_multiplier": 1 }"""));

        Assert.False(same.ConnectChanges);
        Assert.False(same.KeybindsChange);
        Assert.False(same.ModConfigChanges);
        Assert.False(same.AnyChange);
    }

    [Fact]
    public void A_server_address_differing_only_in_case_is_not_a_change()
    {
        var plan = Between(Pack(connect: "Play.Example:42420"), Pack(connect: "play.example:42420"));

        Assert.False(plan.ConnectChanges);
    }
}
