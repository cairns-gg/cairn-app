using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Taking an author's revision, and the state it leaves behind.
///
/// The merge itself is covered in PackUpdateTests. What matters here is that the four
/// things written afterwards agree: the manifest, the base for next time, the lock, and
/// the revision. The base is the one that compounds — every future merge is measured from
/// it, so recording the merged list instead of the author's would make a mod you removed
/// read as yours to remove again, once per update, forever.
/// </summary>
public class PackUpdateApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-apply-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public PackUpdateApplyTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PackStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PackManifest Pack(string gameVersion, params (string Id, string? Pin)[] mods) => new()
    {
        Id = "anego",
        Name = "Anego Server",
        GameVersion = gameVersion,
        Mods = [.. mods.Select(m => new PackMod { ModId = m.Id, Version = m.Pin })],
    };

    private static (string, string?) Mod(string id, string? pin = null) => (id, pin);

    private static PackLock Lock(string gameVersion, params (string Id, string Version)[] mods) => new()
    {
        GameVersion = gameVersion,
        Mods =
        [
            .. mods.Select(m => new LockedMod
            {
                ModId = m.Id, Version = m.Version, FileName = $"{m.Id}_{m.Version}.zip",
                Url = $"https://moddbcdn.vintagestory.at/{m.Id}_{m.Version}.zip",
                Sha256 = new string('a', 64),
            }),
        ],
    };

    private static PackBundle Bundle(PackManifest pack, PackLock? locked, int revision) => new()
    {
        Pack = pack,
        Lock = locked,
        CanonicalUrl = "https://cairns.gg/dizzyd/anego",
        Revision = revision,
    };

    /// <summary>Imports revision 1, so the pack exists as a follower with a base.</summary>
    private PackManifest Follow(PackManifest theirs, PackLock? locked = null)
    {
        var imported = _store.Import(
            Bundle(theirs, locked, revision: 1),
            sourceUrl: "https://cairns.gg/dizzyd/anego");

        return imported;
    }

    /// <summary>
    /// The escape from the standing notice: told once to stop asking, it stops.
    ///
    /// Only ever set by somebody ticking a box. An inferred version would be worse than
    /// none — a pack that silently stops mentioning a mod is precisely what a person who
    /// never asked for silence would want to know about.
    /// </summary>
    [Fact]
    public void A_removal_you_asked_not_to_be_asked_about_again_is_not_raised_again()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")));

        var mine = Pack("1.22.5", Mod("carryon"));
        _store.Save(mine);

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));

        var first = PackUpdatePlan.Between(
            mine, theirs, _store.LoadUpstream("anego"), state: _store.LoadLocalState("anego"));

        var dropped = first.Choices.Single();
        Assert.True(dropped.CanSilence);
        dropped.Silence = true;

        _store.ApplyUpdate("anego", first, Bundle(theirs, null, revision: 2));

        Assert.True(_store.LoadLocalState("anego").HasDeclined("heavyweight"));

        // The next revision says nothing about it, and still leaves it out.
        var next = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"), Mod("newthing"));
        var second = PackUpdatePlan.Between(
            _store.Load("anego"), next, _store.LoadUpstream("anego"),
            state: _store.LoadLocalState("anego"));

        Assert.Empty(second.Choices);
        Assert.DoesNotContain("heavyweight", second.Changes.Select(c => c.ModId));
        Assert.DoesNotContain("heavyweight",
            second.Merge().Mods.Select(m => m.ModId));
    }

    [Fact]
    public void Putting_the_mod_back_beats_silencing_it()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")));
        _store.Save(Pack("1.22.5", Mod("carryon")));

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));
        var plan = PackUpdatePlan.Between(
            _store.Load("anego"), theirs, _store.LoadUpstream("anego"),
            state: _store.LoadLocalState("anego"));

        // Both boxes ticked is contradictory: taking the mod back leaves nothing to be
        // asked about, so nothing is recorded and the pack simply has the mod.
        var dropped = plan.Choices.Single();
        dropped.Take = true;
        dropped.Silence = true;

        _store.ApplyUpdate("anego", plan, Bundle(theirs, null, revision: 2));

        Assert.False(_store.LoadLocalState("anego").HasDeclined("heavyweight"));
        Assert.Contains("heavyweight", _store.Load("anego").Mods.Select(m => m.ModId));
    }

    [Fact]
    public void Adding_the_mod_back_by_hand_forgets_that_you_declined_it()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")));
        _store.Save(Pack("1.22.5", Mod("carryon")));

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));

        var first = PackUpdatePlan.Between(
            _store.Load("anego"), theirs, _store.LoadUpstream("anego"),
            state: _store.LoadLocalState("anego"));

        first.Choices.Single().Silence = true;
        _store.ApplyUpdate("anego", first, Bundle(theirs, null, revision: 2));
        Assert.True(_store.LoadLocalState("anego").HasDeclined("heavyweight"));

        // You change your mind and add it yourself. That is a clearer statement than the
        // box was, and removing it again later must be mentioned rather than swallowed.
        var withIt = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));
        _store.Save(withIt);

        var next = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"), Mod("newthing"));
        var second = PackUpdatePlan.Between(
            withIt, next, _store.LoadUpstream("anego"), state: _store.LoadLocalState("anego"));

        _store.ApplyUpdate("anego", second, Bundle(next, null, revision: 3));

        Assert.False(_store.LoadLocalState("anego").HasDeclined("heavyweight"));
    }

    [Fact]
    public void A_pack_nobody_declined_anything_in_gets_no_state_file()
    {
        Follow(Pack("1.22.5", Mod("carryon")));

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("newthing"));
        var plan = PackUpdatePlan.Between(
            _store.Load("anego"), theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan, Bundle(theirs, null, revision: 2));

        // An empty document in every pack directory, saying nothing, is clutter people
        // then have to wonder about.
        Assert.False(File.Exists(_store.LocalStatePath("anego")));
    }

    [Fact]
    public void Importing_a_published_pack_records_the_base_it_will_be_merged_against()
    {
        Follow(Pack("1.22.5", Mod("carryon")));

        var @base = _store.LoadUpstream("anego");

        Assert.NotNull(@base);
        Assert.Equal(["carryon"], @base.Mods.Select(m => m.ModId).ToArray());
    }

    [Fact]
    public void A_pack_from_a_file_records_no_base_because_it_follows_nobody()
    {
        _store.Import(new PackBundle { Pack = Pack("1.22.5", Mod("carryon")) });

        Assert.Null(_store.LoadUpstream("anego"));
    }

    [Fact]
    public void Applying_records_the_authors_list_as_the_base_not_the_merged_one()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")));

        // Take out a mod they ship, then take an update that still ships it.
        var mine = Pack("1.22.5", Mod("carryon"));
        _store.Save(mine);

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"), Mod("newthing"));
        var plan = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan, Bundle(theirs, null, revision: 2));

        // The base is theirs, so the next update still knows heavyweight is a mod you
        // removed rather than one they have just added. Recording the merged list here is
        // the bug that would ask the same question every single time.
        var @base = _store.LoadUpstream("anego");
        Assert.Contains("heavyweight", @base!.Mods.Select(m => m.ModId));

        // And your removal survived the update.
        Assert.DoesNotContain("heavyweight", _store.Load("anego").Mods.Select(m => m.ModId));
    }

    [Fact]
    public void Applying_moves_the_revision_being_followed()
    {
        Follow(Pack("1.22.5", Mod("carryon")));
        Assert.Equal(1, _store.LoadLink("anego")!.Revision);

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("newthing"));
        var plan = PackUpdatePlan.Between(_store.Load("anego"), theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan, Bundle(theirs, null, revision: 7));

        var link = _store.LoadLink("anego")!;
        Assert.Equal(7, link.Revision);

        // Still theirs. Taking an update is not taking ownership.
        Assert.Equal(PackRole.Follower, link.Role);
        Assert.True(link.Following);
    }

    [Fact]
    public void The_authors_lock_is_taken_and_your_own_mods_keep_theirs()
    {
        Follow(Pack("1.22.5", Mod("carryon")), Lock("1.22.5", ("carryon", "1.0.0")));

        // You add a mod of your own and sync records it.
        var mine = Pack("1.22.5", Mod("carryon"), Mod("myfavourite"));
        _store.Save(mine);
        Lock("1.22.5", ("carryon", "1.0.0"), ("myfavourite", "5.0.0")).Save(_store.LockPath("anego"));

        var theirs = Pack("1.22.5", Mod("carryon"));
        var plan = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan,
            Bundle(theirs, Lock("1.22.5", ("carryon", "2.0.0")), revision: 2));

        var locked = _store.LoadLock("anego")!;

        // Theirs reproduces their set exactly; yours is not re-downloaded for no reason.
        Assert.Equal("2.0.0", locked.Mods.Single(m => m.ModId == "carryon").Version);
        Assert.Equal("5.0.0", locked.Mods.Single(m => m.ModId == "myfavourite").Version);
    }

    [Fact]
    public void An_update_cannot_move_a_mod_you_already_have_to_a_url_of_its_choosing()
    {
        Follow(Pack("1.22.5", Mod("carryon")), Lock("1.22.5", ("carryon", "1.0.0")));

        var mine = Pack("1.22.5", Mod("carryon"));
        _store.Save(mine);

        // A revision that adds nothing and removes nothing, but rewrites where carryon
        // comes from. The plan diffs manifests, so this is invisible there — it would read
        // as "matches the author's revision" while silently replacing the download.
        var theirs = Pack("1.22.5", Mod("carryon"));
        var poisoned = Lock("1.22.5", ("carryon", "1.0.0"));
        poisoned.Mods.Single().Url = "https://moddbcdn.vintagestory.at/attacker/payload.zip";
        poisoned.Mods.Single().FileName = "payload.zip";

        var plan = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));
        _store.ApplyUpdate("anego", plan, Bundle(theirs, poisoned, revision: 2));

        var locked = _store.LoadLock("anego")!.Mods.Single();

        // Import is not the only way somebody else's lock entries arrive, so it is not the
        // only place the rule applies.
        Assert.Equal("carryon", locked.ModId);
        Assert.Equal("", locked.Url);
        Assert.Equal("", locked.FileName);
    }

    [Fact]
    public void A_retargeted_pack_drops_lock_entries_chosen_for_the_old_game_version()
    {
        Follow(Pack("1.22.5", Mod("carryon")), Lock("1.22.5", ("carryon", "1.0.0")));

        var mine = Pack("1.22.5", Mod("carryon"), Mod("myfavourite"));
        _store.Save(mine);
        Lock("1.22.5", ("carryon", "1.0.0"), ("myfavourite", "5.0.0")).Save(_store.LockPath("anego"));

        var theirs = Pack("1.23.0", Mod("carryon"));
        var plan = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan,
            Bundle(theirs, Lock("1.23.0", ("carryon", "3.0.0")), revision: 2));

        var locked = _store.LoadLock("anego")!;

        // Your mod's old entry is gone rather than carried across: the lock now says 1.23.0,
        // so sync would otherwise trust a file that was chosen for 1.22.5 and install it.
        Assert.Equal("1.23.0", locked.GameVersion);
        Assert.DoesNotContain("myfavourite", locked.Mods.Select(m => m.ModId));

        // The mod itself is still in the pack; only its recorded file went.
        Assert.Contains("myfavourite", _store.Load("anego").Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_lock_never_mentions_a_mod_the_merge_left_out()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")),
            Lock("1.22.5", ("carryon", "1.0.0"), ("heavyweight", "1.0.0")));

        var mine = Pack("1.22.5", Mod("carryon"));      // you removed heavyweight
        _store.Save(mine);

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));
        var plan = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));

        _store.ApplyUpdate("anego", plan,
            Bundle(theirs, Lock("1.22.5", ("carryon", "2.0.0"), ("heavyweight", "2.0.0")),
                revision: 2));

        // Their lock offered it; the merge said no. A lock entry for a mod the manifest
        // does not name would have sync install it and then sweep it, every run.
        Assert.DoesNotContain("heavyweight", _store.LoadLock("anego")!.Mods.Select(m => m.ModId));
    }

    /// <summary>
    /// A mod you removed that the author still ships is reported at every revision, not
    /// only the first.
    ///
    /// It is a standing difference between your copy and theirs, and it stays true for as
    /// long as both remain the case — the base records the author's list, which is what
    /// makes their changes legible, and no amount of it can record that you answered a
    /// question once. Suppressing the notice would need a separate record of declined mods,
    /// and the failure mode of that is worse than a repeated line: a pack you took a mod
    /// out of a year ago silently never mentioning it again.
    ///
    /// What must not recur is the work. The default keeps your removal every time, so this
    /// is a line to read rather than a question to re-answer, and taking update after
    /// update never reinstates the mod behind you.
    /// </summary>
    [Fact]
    public void A_mod_you_removed_is_reported_every_revision_but_never_reinstated()
    {
        Follow(Pack("1.22.5", Mod("carryon"), Mod("heavyweight")));

        var mine = Pack("1.22.5", Mod("carryon"));
        _store.Save(mine);

        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));

        var first = PackUpdatePlan.Between(mine, theirs, _store.LoadUpstream("anego"));
        Assert.Single(first.Choices);
        _store.ApplyUpdate("anego", first, Bundle(theirs, null, revision: 2));

        Assert.DoesNotContain("heavyweight", _store.Load("anego").Mods.Select(m => m.ModId));

        // The author publishes again. The notice comes back, because it is still true.
        var againTheirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"), Mod("newthing"));
        var second = PackUpdatePlan.Between(
            _store.Load("anego"), againTheirs, _store.LoadUpstream("anego"));

        var dropped = second.Choices.Single();
        Assert.Equal("heavyweight", dropped.ModId);
        Assert.False(dropped.Take);

        // And their genuinely new mod is still an addition, not tangled up in it.
        Assert.Equal(ModChangeKind.Added, second.Changes.Single(c => c.ModId == "newthing").Kind);

        _store.ApplyUpdate("anego", second, Bundle(againTheirs, null, revision: 3));

        var after = _store.Load("anego").Mods.Select(m => m.ModId).ToList();
        Assert.DoesNotContain("heavyweight", after);
        Assert.Contains("newthing", after);
    }
}
