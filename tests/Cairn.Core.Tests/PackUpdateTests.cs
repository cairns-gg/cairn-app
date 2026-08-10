using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Merging an author's newer revision into a copy somebody has been living in.
///
/// The rules are asymmetric on purpose. What the author did is applied; what the follower
/// did is respected; and the two places they can genuinely disagree — a mod taken out, a
/// version pinned — are asked about rather than decided. Getting that backwards produces
/// the worst kind of bug: one that quietly undoes a deliberate choice every time an update
/// lands, and looks like the update working.
/// </summary>
public class PackUpdateTests
{
    private static PackManifest Pack(string gameVersion, params (string Id, string? Pin)[] mods) => new()
    {
        Id = "anego",
        Name = "Anego Server",
        GameVersion = gameVersion,
        Mods = [.. mods.Select(m => new PackMod { ModId = m.Id, Version = m.Pin })],
    };

    private static (string, string?) Mod(string id, string? pin = null) => (id, pin);

    [Fact]
    public void A_mod_the_author_added_is_taken()
    {
        var @base = Pack("1.22.5", Mod("carryon"));
        var mine = Pack("1.22.5", Mod("carryon"));
        var theirs = Pack("1.22.5", Mod("carryon"), Mod("betterruins"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        var change = plan.Changes.Single(c => c.ModId == "betterruins");
        Assert.Equal(ModChangeKind.Added, change.Kind);
        Assert.True(change.Take);
        Assert.False(change.IsChoice);

        Assert.Contains("betterruins", plan.Merge().Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_mod_the_author_removed_goes()
    {
        var @base = Pack("1.22.5", Mod("carryon"), Mod("betterruins"));
        var mine = Pack("1.22.5", Mod("carryon"), Mod("betterruins"));
        var theirs = Pack("1.22.5", Mod("carryon"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        Assert.Equal(ModChangeKind.Removed, plan.Changes.Single(c => c.ModId == "betterruins").Kind);
        Assert.DoesNotContain("betterruins", plan.Merge().Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_mod_you_added_is_left_alone()
    {
        var @base = Pack("1.22.5", Mod("carryon"));
        var mine = Pack("1.22.5", Mod("carryon"), Mod("myfavourite"));
        var theirs = Pack("1.22.5", Mod("carryon"), Mod("betterruins"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        // "If they've added a mod, don't worry about it" — not a question, not a warning,
        // and above all still there afterwards.
        var mineOnly = plan.Changes.Single(c => c.ModId == "myfavourite");
        Assert.Equal(ModChangeKind.Yours, mineOnly.Kind);
        Assert.False(mineOnly.IsChoice);

        var merged = plan.Merge().Mods.Select(m => m.ModId).ToList();
        Assert.Contains("myfavourite", merged);
        Assert.Contains("betterruins", merged);
    }

    [Fact]
    public void A_mod_you_removed_is_asked_about_and_stays_out_by_default()
    {
        var @base = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));
        var mine = Pack("1.22.5", Mod("carryon"));               // you took it out
        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        var dropped = plan.Changes.Single(c => c.ModId == "heavyweight");
        Assert.Equal(ModChangeKind.DroppedByYou, dropped.Kind);
        Assert.True(dropped.IsChoice);

        // Default respects the removal: silently reinstating it on every update would be a
        // decision the person can never make stick.
        Assert.False(dropped.Take);
        Assert.DoesNotContain("heavyweight", plan.Merge().Mods.Select(m => m.ModId));

        // And taking it puts it back.
        dropped.Take = true;
        Assert.Contains("heavyweight", plan.Merge().Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_version_you_pinned_against_theirs_is_a_choice_that_keeps_yours()
    {
        var @base = Pack("1.22.5", Mod("carryon", "1.0.0"));
        var mine = Pack("1.22.5", Mod("carryon", "1.1.0"));      // you pinned your own
        var theirs = Pack("1.22.5", Mod("carryon", "2.0.0"));    // they moved theirs

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        var conflict = plan.Changes.Single(c => c.ModId == "carryon");
        Assert.Equal(ModChangeKind.PinConflict, conflict.Kind);
        Assert.True(conflict.IsChoice);
        Assert.Equal("1.1.0", conflict.Mine);
        Assert.Equal("2.0.0", conflict.Theirs);

        // A pin is an instruction to stay put — nothing else in Cairn moves a pinned mod,
        // and an update should not be the exception that does it without asking.
        Assert.False(conflict.Take);
        Assert.Equal("1.1.0", plan.Merge().Mods.Single().Version);

        conflict.Take = true;
        Assert.Equal("2.0.0", plan.Merge().Mods.Single().Version);
    }

    [Fact]
    public void A_pin_you_never_touched_simply_follows_theirs()
    {
        var @base = Pack("1.22.5", Mod("carryon", "1.0.0"));
        var mine = Pack("1.22.5", Mod("carryon", "1.0.0"));      // untouched
        var theirs = Pack("1.22.5", Mod("carryon", "2.0.0"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        var change = plan.Changes.Single();
        Assert.Equal(ModChangeKind.Repinned, change.Kind);
        Assert.False(change.IsChoice);       // nothing to ask; you had no opinion
        Assert.True(change.Take);
        Assert.Equal("2.0.0", plan.Merge().Mods.Single().Version);
    }

    [Fact]
    public void An_untouched_copy_merges_to_exactly_the_authors_pack()
    {
        var @base = Pack("1.22.5", Mod("carryon"), Mod("betterruins", "1.0.0"));
        var mine = Pack("1.22.5", Mod("carryon"), Mod("betterruins", "1.0.0"));
        var theirs = Pack("1.22.6", Mod("carryon", "3.0.0"), Mod("newthing"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        Assert.Empty(plan.Choices);

        var merged = plan.Merge();
        Assert.Equal("1.22.6", merged.GameVersion);
        Assert.Equal(["carryon", "newthing"], merged.Mods.Select(m => m.ModId).ToArray());
        Assert.Equal("3.0.0", merged.Mods.Single(m => m.ModId == "carryon").Version);
    }

    [Fact]
    public void Retargeting_the_game_is_called_out_on_its_own()
    {
        var @base = Pack("1.22.5", Mod("carryon"));
        var mine = Pack("1.22.5", Mod("carryon"));
        var theirs = Pack("1.23.0", Mod("carryon"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        // It moves every mod in the pack, so it is not something to leave buried in a list.
        Assert.True(plan.GameVersionChanges);
        Assert.Equal("1.22.5", plan.PreviousGameVersion);
        Assert.Equal("1.23.0", plan.GameVersion);
        Assert.True(plan.AnyChange);
    }

    [Fact]
    public void The_pack_stays_yours_by_id_and_theirs_by_everything_else()
    {
        var @base = Pack("1.22.5", Mod("carryon"));
        var mine = Pack("1.22.5", Mod("carryon"));
        mine.Id = "my-own-id";

        var theirs = Pack("1.22.5", Mod("carryon"));
        theirs.Name = "Anego Server (season 2)";
        theirs.Description = "now with more rust";

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        // Renaming the directory under somebody is not an update; the rest is their pack.
        Assert.Equal("my-own-id", merged.Id);
        Assert.Equal("Anego Server (season 2)", merged.Name);
        Assert.Equal("now with more rust", merged.Description);
    }

    [Fact]
    public void Without_a_base_a_removal_cannot_be_told_from_an_addition_and_it_says_so()
    {
        // A pack imported before the base was recorded. The mod you took out looks exactly
        // like one the author has just added, so it comes through as an addition — and the
        // plan reports that it was working blind rather than implying it knew.
        var mine = Pack("1.22.5", Mod("carryon"));
        var theirs = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base: null);

        Assert.False(plan.HasBase);
        Assert.Equal(ModChangeKind.Added, plan.Changes.Single(c => c.ModId == "heavyweight").Kind);
    }

    [Fact]
    public void An_unedited_follower_with_no_base_still_merges_correctly()
    {
        // The common case for old packs: nobody edited anything, so falling back to the
        // local manifest as the base gives the same answer a real base would.
        var mine = Pack("1.22.5", Mod("carryon", "1.0.0"));
        var theirs = Pack("1.22.5", Mod("carryon", "2.0.0"), Mod("newthing"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base: null);

        Assert.Empty(plan.Choices);
        Assert.Equal(["carryon", "newthing"], plan.Merge().Mods.Select(m => m.ModId).ToArray());
        Assert.Equal("2.0.0", plan.Merge().Mods.First().Version);
    }

    /// <summary>
    /// Reset is the one answer here that removes mods nobody asked to remove, which is why
    /// it is a separate statement rather than a shortcut for answering everything their way.
    /// </summary>
    [Fact]
    public void A_reset_takes_the_authors_pack_exactly()
    {
        var @base = Pack("1.22.5", Mod("carryon"), Mod("heavyweight"));
        var mine = Pack("1.22.5", Mod("carryon", "1.1.0"), Mod("myfavourite"));
        var theirs = Pack("1.22.6", Mod("carryon", "2.0.0"), Mod("heavyweight"), Mod("newthing"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);
        plan.Reset = true;
        var merged = plan.Merge();

        // Their list, their pins, their game version, whole.
        Assert.Equal(["carryon", "heavyweight", "newthing"],
            merged.Mods.Select(m => m.ModId).Order().ToArray());
        Assert.Equal("2.0.0", merged.Mods.Single(m => m.ModId == "carryon").Version);
        Assert.Equal("1.22.6", merged.GameVersion);

        // Yours is gone, which is the whole point and the whole danger.
        Assert.DoesNotContain("myfavourite", merged.Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_reset_names_what_it_would_remove()
    {
        var @base = Pack("1.22.5", Mod("carryon"));
        var mine = Pack("1.22.5", Mod("carryon"), Mod("myfavourite"), Mod("another"));
        var theirs = Pack("1.22.5", Mod("carryon"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        // Nothing to warn about until it is asked for.
        Assert.False(plan.ResetRemovesAnything);
        Assert.Empty(plan.RemovedByReset);

        plan.Reset = true;

        // Named, because "your changes" is not something anybody can weigh against a world.
        Assert.Equal(["another", "myfavourite"], plan.RemovedByReset.Order().ToArray());
        Assert.True(plan.ResetRemovesAnything);
    }

    [Fact]
    public void A_reset_ignores_the_answers_rather_than_taking_them()
    {
        var @base = Pack("1.22.5", Mod("carryon", "1.0.0"), Mod("heavyweight"));
        var mine = Pack("1.22.5", Mod("carryon", "1.1.0"));      // pinned, and removed one
        var theirs = Pack("1.22.5", Mod("carryon", "2.0.0"), Mod("heavyweight"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);

        // Answers left at their defaults — keep my pin, leave the mod out — and then reset.
        Assert.All(plan.Choices, c => Assert.False(c.Take));
        plan.Reset = true;

        var merged = plan.Merge();

        // Reset is not "answer everything their way": it does not consult the answers at
        // all, which is what makes it predictable when a dozen of them are outstanding.
        Assert.Equal("2.0.0", merged.Mods.Single(m => m.ModId == "carryon").Version);
        Assert.Contains("heavyweight", merged.Mods.Select(m => m.ModId));
    }

    [Fact]
    public void A_reset_of_an_unedited_copy_removes_nothing()
    {
        // The reassuring case: somebody who changed nothing loses nothing by resetting, so
        // the warning stays quiet rather than crying wolf.
        var @base = Pack("1.22.5", Mod("carryon"));
        var theirs = Pack("1.22.5", Mod("carryon"), Mod("newthing"));

        var plan = PackUpdatePlan.Between(@base, theirs, @base);
        plan.Reset = true;

        Assert.False(plan.ResetRemovesAnything);
        Assert.Equal(["carryon", "newthing"], plan.Merge().Mods.Select(m => m.ModId).Order().ToArray());
    }

    [Fact]
    public void An_update_that_changes_nothing_says_nothing()
    {
        var same = Pack("1.22.5", Mod("carryon"), Mod("betterruins"));

        var plan = PackUpdatePlan.Between(same, same, same);

        Assert.False(plan.AnyChange);
        Assert.Empty(plan.Changes);
        Assert.Contains("changes no mods", plan.Summary());
    }

    // ---- the hotkeys the pack carries ----
    //
    // Same three-way rules as a pin, and they matter for the same reason: the author's
    // reconciliation has to reach people who already hold the pack, and a follower who sat
    // down and rebound something must not have it taken back on every revision.

    private static PackManifest WithKeys(
        PackManifest pack, params (string Code, string Key)[] keys)
    {
        pack.Keybinds = keys.Length == 0 ? null : keys.ToDictionary(k => k.Code, k => k.Key);
        return pack;
    }

    [Fact]
    public void The_authors_hotkeys_survive_an_update()
    {
        // The bug this whole thing exists to stop: Merge names the fields it carries, and
        // a field it does not name empties on every revision.
        var @base = WithKeys(Pack("1.22.5", Mod("carryon")), ("scribepinhud", "Ctrl-P"));
        var mine = WithKeys(Pack("1.22.5", Mod("carryon")), ("scribepinhud", "Ctrl-P"));
        var theirs = WithKeys(Pack("1.22.5", Mod("carryon"), Mod("scribe")), ("scribepinhud", "Ctrl-P"));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        Assert.Equal("Ctrl-P", Assert.Single(merged.Keybinds!).Value);
    }

    [Fact]
    public void A_hotkey_the_author_moved_moves_for_a_follower_who_never_touched_it()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "Ctrl-P"));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        // Inherited, not chosen. The author reconciled a new collision and everybody gets it.
        Assert.Equal("Ctrl-P", merged.Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void A_hotkey_you_rebound_yourself_survives_the_authors_change()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "K"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "Ctrl-P"));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        // You sat down and moved it. Putting it back every revision is the bug you could
        // never get out of.
        Assert.Equal("K", merged.Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void A_hotkey_the_author_adds_arrives()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "K"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe"), Mod("carryon")),
            ("scribepinhud", "P"), ("carryonswap", "Ctrl-C"));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        Assert.Equal("K", merged.Keybinds!["scribepinhud"]);      // yours, still
        Assert.Equal("Ctrl-C", merged.Keybinds!["carryonswap"]);  // theirs, new
    }

    [Fact]
    public void A_hotkey_you_cleared_stays_cleared()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        // Reset here means "use whatever the mod ships", which is a decision like any
        // other — handing it back on every revision undoes it silently.
        Assert.Null(merged.Keybinds);
    }

    [Fact]
    public void A_hotkey_of_your_own_is_left_alone()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "K"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")));

        var merged = PackUpdatePlan.Between(mine, theirs, @base).Merge();

        Assert.Equal("K", merged.Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void With_no_base_the_authors_hotkeys_win_whole()
    {
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "K"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "Ctrl-P"));

        // Nothing can be told from nothing. The mod list falls back the same way, and for
        // the same reason: an unedited follower merges perfectly, which is nearly all of them.
        var merged = PackUpdatePlan.Between(mine, theirs, null).Merge();

        Assert.Equal("Ctrl-P", merged.Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void A_reset_takes_the_authors_hotkeys()
    {
        var @base = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "P"));
        var mine = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "K"));
        var theirs = WithKeys(Pack("1.22.5", Mod("scribe")), ("scribepinhud", "Ctrl-P"));

        var plan = PackUpdatePlan.Between(mine, theirs, @base);
        plan.Reset = true;

        // A reset is the statement that there is only one set worth keeping.
        Assert.Equal("Ctrl-P", plan.Merge().Keybinds!["scribepinhud"]);
    }

    [Fact]
    public void A_pack_with_no_hotkeys_keeps_the_file_it_had()
    {
        var same = Pack("1.22.5", Mod("carryon"));

        // Null rather than an empty object: a pack that never set one must look exactly as
        // it did, or every follower reports a change nobody made.
        Assert.Null(PackUpdatePlan.Between(same, same, same).Merge().Keybinds);
    }
}
