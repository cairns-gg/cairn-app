using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the share window is built from. The checks here are the ones that separate a pack
/// that works for its author from one that works for everybody — which is the worst failure
/// a sharing site has, because it only shows up on somebody else's machine.
/// </summary>
public class PublishPlanTests
{
    private static PackManifest Pack(string? connect = null, params string[] mods) => new()
    {
        Id = "anego",
        Name = "Anego Server",
        GameVersion = "1.22.5",
        Connect = connect,
        Mods = [.. mods.Select(m => new PackMod { ModId = m })],
    };

    private static PackLock Lock(string gameVersion = "1.22.5", params string[] mods) => new()
    {
        GameVersion = gameVersion,
        Mods = [.. mods.Select(m => new LockedMod { ModId = m, Version = "1.0.0" })],
    };

    [Fact]
    public async Task A_synced_pack_can_be_published()
    {
        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview", "unchisel"), Lock("1.22.5", "glassview", "unchisel"));

        Assert.True(plan.CanPublish);
        Assert.Null(plan.LockProblem);
        Assert.Equal(2, plan.Mods.Count);
    }

    [Fact]
    public async Task Mods_carry_the_version_the_lock_says_is_installed()
    {
        var locked = Lock("1.22.5", "glassview");
        locked.Mods[0].Version = "1.3.0";

        var plan = await PublishPlan.PrepareAsync(Pack(null, "glassview"), locked);

        // The manifest names mods without versions; what recipients get is the lock.
        Assert.Equal("1.3.0", plan.Mods.Single().Version);
        Assert.False(plan.Mods.Single().Pinned);
    }

    [Fact]
    public async Task A_pack_that_was_never_synced_cannot_be_published()
    {
        var plan = await PublishPlan.PrepareAsync(Pack(null, "glassview"), locked: null);

        Assert.False(plan.CanPublish);
        Assert.Contains("never been synced", plan.LockProblem);
    }

    [Fact]
    public async Task A_lock_that_misses_a_mod_cannot_be_published()
    {
        // Adding a mod and sharing before syncing. Including the lock is the whole
        // reproducibility claim, so a partial one is refused rather than warned about.
        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview", "unchisel"), Lock("1.22.5", "glassview"));

        Assert.False(plan.CanPublish);
        Assert.Contains("unchisel", plan.LockProblem);
    }

    /// <summary>
    /// Publishing syncs first, so a mod still missing afterwards is one that could not be
    /// installed rather than one nobody had got round to. The reason the sync gave is the
    /// only thing that moves the author forward — "sync the pack first" was an instruction
    /// to repeat something that had already happened.
    /// </summary>
    [Fact]
    public async Task A_mod_the_sync_could_not_install_is_reported_with_its_reason()
    {
        var failures = new List<SyncStep>
        {
            new(SyncAction.Downloaded, "glassview", "1.3.0"),
            new(SyncAction.Failed, "unchisel", "no release marked for game 1.22.5"),
        };

        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview", "unchisel"), Lock("1.22.5", "glassview"),
            syncFailures: failures);

        Assert.False(plan.CanPublish);
        Assert.Contains("could not be installed", plan.LockProblem);
        Assert.Contains("unchisel — no release marked for game 1.22.5", plan.LockProblem);

        // The old wording sent people to press a button that had already been pressed.
        Assert.DoesNotContain("Sync the pack first", plan.LockProblem);
    }

    [Fact]
    public async Task A_missing_mod_with_no_sync_to_explain_it_still_says_to_sync()
    {
        // Nothing ran, so there is no reason to give and the old advice is the right one.
        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview", "unchisel"), Lock("1.22.5", "glassview"),
            syncFailures: null);

        Assert.False(plan.CanPublish);
        Assert.Contains("Sync the pack first", plan.LockProblem);
    }

    [Fact]
    public async Task A_sync_that_failed_a_mod_the_lock_covers_does_not_block_publishing()
    {
        // A mod can fail one sync and still be installed from an earlier one. What decides
        // publishing is whether the lock covers the manifest, never the step list.
        var failures = new List<SyncStep>
        {
            new(SyncAction.Failed, "glassview", "ModDB timed out"),
        };

        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview"), Lock("1.22.5", "glassview"), syncFailures: failures);

        Assert.True(plan.CanPublish);
        Assert.Null(plan.LockProblem);
    }

    [Fact]
    public async Task A_lock_for_another_game_version_cannot_be_published()
    {
        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview"), Lock("1.21.5", "glassview"));

        Assert.False(plan.CanPublish);
        Assert.Contains("1.21.5", plan.LockProblem);
    }

    [Fact]
    public async Task An_empty_pack_cannot_be_published()
    {
        var plan = await PublishPlan.PrepareAsync(Pack(), Lock());

        Assert.False(plan.CanPublish);
        Assert.Contains("no mods", plan.LockProblem);
    }

    [Fact]
    public async Task The_server_address_is_surfaced_when_the_pack_has_one()
    {
        var plan = await PublishPlan.PrepareAsync(
            Pack("anego.example.com:42420", "glassview"), Lock("1.22.5", "glassview"));

        // Publishing a real host and port is not something to find out afterwards.
        Assert.True(plan.HasConnect);
        Assert.Equal("anego.example.com:42420", plan.Connect);
    }

    [Fact]
    public async Task A_pack_without_a_server_says_nothing_about_one()
    {
        var plan = await PublishPlan.PrepareAsync(Pack(null, "glassview"), Lock("1.22.5", "glassview"));

        Assert.False(plan.HasConnect);
    }

    [Fact]
    public async Task A_pin_is_reported_as_a_pin()
    {
        var manifest = Pack(null, "unchisel");
        manifest.Mods[0].Version = "1.2.0";

        var plan = await PublishPlan.PrepareAsync(manifest, Lock("1.22.5", "unchisel"));

        Assert.True(plan.Mods.Single().Pinned);
    }

    [Fact]
    public async Task Without_ModDB_no_mod_is_accused_of_being_missing()
    {
        var plan = await PublishPlan.PrepareAsync(
            Pack(null, "glassview"), Lock("1.22.5", "glassview"), moddb: null);

        // "We did not look" must not render as "recipients cannot install this" — a dialog
        // that blames mods because the network was down is worse than one that says less.
        Assert.False(plan.AnythingUnresolvable);
    }
}
