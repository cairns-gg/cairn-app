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

    // ---- the mod versions, which are not in the manifest at all ----

    private static PackLock Locked(params (string Id, string Version)[] mods) => new()
    {
        GameVersion = "1.22.5",
        Mods = [.. mods.Select(m => new LockedMod { ModId = m.Id, Version = m.Version })],
    };

    private static PackUpdatePlan Between(
        PackManifest mine, PackManifest theirs, PackLock? myLock, PackLock? theirLock) =>
        PackUpdatePlan.Between(mine, theirs, mine, 1, 2, null, myLock, theirLock);

    /// <summary>
    /// The same defect a fourth time, and the widest of them: it is not one field that was
    /// left out, it is the entire mod list.
    ///
    /// A manifest entry may be nothing but a modid, and most are — pinning is the exception
    /// — so the version an author ships lives only in their lockfile. An author who takes
    /// five mod updates and changes nothing else therefore publishes a revision whose pack
    /// object is byte-identical to its predecessor, and a plan built from manifests alone
    /// saw no difference: every follower reported itself current, and cairn-server update
    /// said "already on the author's newest revision" and exited 0, revision after
    /// revision, with no way to tell that anything was wrong.
    /// </summary>
    [Fact]
    public void A_revision_that_only_moves_a_mod_version_in_the_lock_is_a_change()
    {
        var plan = Between(
            Pack(), Pack(),
            Locked(("glassview", "1.1.1")),
            Locked(("glassview", "1.2.1")));

        Assert.True(plan.AnyChange);

        var change = Assert.Single(plan.TheirChanges);
        Assert.Equal(ModChangeKind.Relocked, change.Kind);
        Assert.Equal("glassview", change.ModId);
        Assert.Equal("1.1.1", change.Mine);
        Assert.Equal("1.2.1", change.Theirs);
    }

    [Fact]
    public void And_the_summary_counts_it_as_an_update_rather_than_a_repin()
    {
        var plan = Between(
            Pack(), Pack(),
            Locked(("glassview", "1.1.1")),
            Locked(("glassview", "1.2.1")));

        Assert.Contains("1 updated", plan.Summary());
        Assert.DoesNotContain("repinned", plan.Summary());
    }

    /// <summary>
    /// Nobody pinned anything, so taking the update must not start. A version written into
    /// the manifest here would be an instruction to stay put — Cairn never offers a pinned
    /// mod an update — so this copy would be frozen at the author's version of the day it
    /// last updated, which is the opposite of following them.
    /// </summary>
    [Fact]
    public void Taking_it_leaves_the_mod_unpinned()
    {
        var merged = Between(
            Pack(), Pack(),
            Locked(("glassview", "1.1.1")),
            Locked(("glassview", "1.2.1"))).Merge();

        Assert.Null(Assert.Single(merged.Mods).Version);
    }

    [Fact]
    public void The_same_locked_version_is_not_a_change()
    {
        var plan = Between(
            Pack(), Pack(),
            Locked(("glassview", "1.2.1")),
            Locked(("glassview", "1.2.1")));

        Assert.False(plan.AnyChange);
        Assert.Empty(plan.Changes);
    }

    /// <summary>
    /// A pin outranks either lock at install time — PackSyncer stops believing a lock entry
    /// the moment it disagrees with the version asked for — so two manifests pinning the
    /// same version are settled however their locks read. Raising it would be a change that
    /// applying could not make.
    /// </summary>
    [Fact]
    public void A_pin_both_sides_share_settles_it_whatever_the_locks_say()
    {
        var mine = Pack();
        var theirs = Pack();
        mine.Mods[0].Version = "1.1.1";
        theirs.Mods[0].Version = "1.1.1";

        var plan = Between(mine, theirs, Locked(("glassview", "1.1.1")), Locked(("glassview", "1.2.1")));

        Assert.False(plan.AnyChange);
    }

    /// <summary>
    /// A mod this copy has never installed still counts: it resolves newest-compatible
    /// today and reproduces the author's build afterwards, which is a difference in what
    /// the next sync puts on disk. Shown as "newest", because that is what it is.
    /// </summary>
    [Fact]
    public void No_lock_entry_of_your_own_is_still_a_change()
    {
        var plan = Between(Pack(), Pack(), myLock: null, theirLock: Locked(("glassview", "1.2.1")));

        Assert.True(plan.AnyChange);
        Assert.Null(Assert.Single(plan.TheirChanges).Mine);
    }

    /// <summary>
    /// And the other way round is nothing to take. An author whose lock says nothing about
    /// a mod has published no version of it to reproduce.
    /// </summary>
    [Fact]
    public void An_author_with_no_lock_entry_offers_nothing_to_take()
    {
        var plan = Between(Pack(), Pack(), Locked(("glassview", "1.1.1")), theirLock: null);

        Assert.False(plan.AnyChange);
    }

    /// <summary>
    /// A plan with no locks to hand is the comparison that existed before, and must still
    /// be quiet about a pack nothing else distinguishes.
    /// </summary>
    [Fact]
    public void Comparing_without_locks_reports_what_it_always_did()
    {
        Assert.False(Between(Pack(), Pack()).AnyChange);
    }
}
