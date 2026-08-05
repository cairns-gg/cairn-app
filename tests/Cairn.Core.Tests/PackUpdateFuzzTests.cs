using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The merge, against thousands of randomly generated three-way situations.
///
/// The example tests say what should happen in the cases somebody thought of. This says
/// what must hold in all of them, which is a different question and the one that catches
/// the combination nobody pictured — a mod removed by the author that the follower had
/// also pinned, a pack where every mod is in conflict at once, a base that agrees with
/// neither side.
///
/// The properties are deliberately stated as invariants rather than as a second
/// implementation of the merge. A reimplementation would only prove the two agree, and the
/// obvious way to write it is the same way, so it would agree about the same mistakes.
///
/// Seeded and printed on failure, so a red run is reproducible rather than a rumour.
/// </summary>
public class PackUpdateFuzzTests
{
    private const int Runs = 3000;

    /// <summary>Enough ids to force collisions and overlaps; few enough to keep cases dense.</summary>
    private static readonly string[] Ids =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

    private static readonly string?[] Pins = [null, "1.0.0", "2.0.0", "3.0.0"];

    private sealed record Situation(
        PackManifest Base, PackManifest Mine, PackManifest Theirs, int Seed)
    {
        public override string ToString() =>
            $"seed {Seed}\n  base   {Show(Base)}\n  mine   {Show(Mine)}\n  theirs {Show(Theirs)}";

        private static string Show(PackManifest m) =>
            m.Mods.Count == 0
                ? "(empty)"
                : string.Join(", ", m.Mods.Select(x => $"{x.ModId}={x.Version ?? "-"}"));
    }

    /// <summary>
    /// One random three-way case. Each mod independently appears or not in each of the
    /// three manifests, with an independently chosen pin — so every combination of
    /// present/absent and agreeing/differing pins comes up, including the ones that only
    /// occur together.
    /// </summary>
    private static Situation Generate(int seed)
    {
        var rng = new Random(seed);

        var @base = New();
        var mine = New();
        var theirs = New();

        foreach (var id in Ids)
        {
            if (rng.Next(3) > 0) @base.Mods.Add(new PackMod { ModId = id, Version = Pin(rng) });
            if (rng.Next(3) > 0) mine.Mods.Add(new PackMod { ModId = id, Version = Pin(rng) });
            if (rng.Next(3) > 0) theirs.Mods.Add(new PackMod { ModId = id, Version = Pin(rng) });
        }

        // The game version moves sometimes, because retargeting is the change that touches
        // everything and it must not disturb the mod merge.
        theirs.GameVersion = rng.Next(4) == 0 ? "1.23.0" : "1.22.5";

        return new Situation(@base, mine, theirs, seed);

        static PackManifest New() => new()
        {
            Id = "anego", Name = "Anego", GameVersion = "1.22.5", Mods = [],
        };

        static string? Pin(Random r) => Pins[r.Next(Pins.Length)];
    }

    private static string? PinOf(PackManifest m, string id) =>
        m.Mods.FirstOrDefault(x => x.ModId == id)?.Version;

    private static bool Has(PackManifest m, string id) => m.Mods.Any(x => x.ModId == id);

    /// <summary>
    /// Runs one property over every generated situation, reporting the first failure with
    /// the case that caused it.
    /// </summary>
    private static void ForEachSituation(Action<Situation, PackUpdatePlan, PackManifest> check)
    {
        for (var seed = 0; seed < Runs; seed++)
        {
            var s = Generate(seed);
            var plan = PackUpdatePlan.Between(s.Mine, s.Theirs, s.Base);
            var merged = plan.Merge();

            try
            {
                check(s, plan, merged);
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"{e.Message}\n\n{s}");
            }
        }
    }

    [Fact]
    public void A_mod_only_you_have_always_survives_untouched()
    {
        // "If they've added a mod, don't worry about it" — the one rule with no exception,
        // so it is the one most worth checking against every shape of the rest.
        ForEachSituation((s, _, merged) =>
        {
            foreach (var id in Ids)
            {
                if (Has(s.Base, id) || Has(s.Theirs, id) || !Has(s.Mine, id)) continue;

                Assert.True(Has(merged, id), $"'{id}' was yours alone and went missing");
                Assert.Equal(PinOf(s.Mine, id), PinOf(merged, id));
            }
        });
    }

    [Fact]
    public void Nothing_is_ever_invented()
    {
        // Every mod in the result came from one side or the other. A merge that can
        // produce a mod nobody asked for would install it.
        ForEachSituation((s, _, merged) =>
        {
            foreach (var mod in merged.Mods)
                Assert.True(Has(s.Mine, mod.ModId) || Has(s.Theirs, mod.ModId),
                    $"'{mod.ModId}' is in neither side");

            // And it never lands twice, which would put two lock entries on one file.
            Assert.Equal(merged.Mods.Count,
                merged.Mods.Select(m => m.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void A_mod_you_removed_is_never_put_back_without_being_asked()
    {
        // The failure that would be reported as "it keeps re-adding mods I deleted", and
        // the reason the base exists at all.
        ForEachSituation((s, plan, merged) =>
        {
            foreach (var id in Ids)
            {
                if (!Has(s.Base, id) || Has(s.Mine, id) || !Has(s.Theirs, id)) continue;

                Assert.False(Has(merged, id), $"'{id}' came back on its own");

                var change = plan.Changes.Single(c => c.ModId == id);
                Assert.Equal(ModChangeKind.DroppedByYou, change.Kind);
                Assert.True(change.IsChoice, $"'{id}' was reinstated without being asked");
            }
        });
    }

    [Fact]
    public void A_pin_you_chose_is_never_moved_without_being_asked()
    {
        // Nothing else in Cairn moves a pinned mod. An update doing it quietly would be the
        // one exception, and the one nobody would look for.
        ForEachSituation((s, plan, merged) =>
        {
            foreach (var id in Ids)
            {
                if (!Has(s.Mine, id) || !Has(s.Theirs, id)) continue;

                var mine = PinOf(s.Mine, id);
                var theirs = PinOf(s.Theirs, id);

                if (mine == theirs) continue;

                // You had touched it if the base disagrees with you, or there is no base
                // entry to have followed.
                var youChose = !Has(s.Base, id) || PinOf(s.Base, id) != mine;
                if (!youChose) continue;

                Assert.Equal(mine, PinOf(merged, id));
                Assert.True(plan.Changes.Single(c => c.ModId == id).IsChoice);
            }
        });
    }

    [Fact]
    public void Taking_every_choice_gives_the_authors_answer_for_every_mod_they_ship()
    {
        // The other end of the range. With every question answered their way, the result
        // must be their pack plus whatever is only yours — no residue of your edits.
        for (var seed = 0; seed < Runs; seed++)
        {
            var s = Generate(seed);
            var plan = PackUpdatePlan.Between(s.Mine, s.Theirs, s.Base);

            foreach (var choice in plan.Choices) choice.Take = true;

            var merged = plan.Merge();

            foreach (var mod in s.Theirs.Mods)
            {
                Assert.True(Has(merged, mod.ModId),
                    $"took everything and '{mod.ModId}' is still missing\n\n{s}");

                Assert.True(mod.Version == PinOf(merged, mod.ModId),
                    $"took everything and '{mod.ModId}' kept {PinOf(merged, mod.ModId)}\n\n{s}");
            }
        }
    }

    [Fact]
    public void Every_mod_in_either_side_is_accounted_for_exactly_once()
    {
        // A mod with no ModChange is a mod the dialog never mentions, which is how an
        // update makes a change nobody was shown.
        ForEachSituation((s, plan, _) =>
        {
            var involved = s.Mine.Mods.Select(m => m.ModId)
                .Concat(s.Theirs.Mods.Select(m => m.ModId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var id in involved)
            {
                // Unchanged mods are deliberately silent — same on both sides, nothing to
                // report — so only differences must appear.
                var same = Has(s.Mine, id) && Has(s.Theirs, id)
                           && PinOf(s.Mine, id) == PinOf(s.Theirs, id);

                if (same) continue;

                Assert.Single(plan.Changes, c =>
                    string.Equals(c.ModId, id, StringComparison.OrdinalIgnoreCase));
            }
        });
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time()
    {
        // Convergence. Merging, then merging the same author revision into the result with
        // the same answers, must be a no-op — otherwise repeated updates drift.
        for (var seed = 0; seed < Runs; seed++)
        {
            var s = Generate(seed);

            var first = PackUpdatePlan.Between(s.Mine, s.Theirs, s.Base);
            var once = first.Merge();

            // The base after applying is the author's list, as PackStore records it.
            var second = PackUpdatePlan.Between(once, s.Theirs, s.Theirs);

            foreach (var choice in second.Choices)
                Assert.False(choice.Take, $"a settled pack still wants to change\n\n{s}");

            var twice = second.Merge();

            Assert.Equal(
                once.Mods.Select(m => $"{m.ModId}={m.Version}").Order().ToArray(),
                twice.Mods.Select(m => $"{m.ModId}={m.Version}").Order().ToArray());
        }
    }

    [Fact]
    public void The_game_version_always_becomes_the_authors()
    {
        // It is their pack and their revision of it; a merge that kept yours would leave
        // the mods resolved for a version the pack no longer targets.
        ForEachSituation((s, _, merged) => Assert.Equal(s.Theirs.GameVersion, merged.GameVersion));
    }

    [Fact]
    public void An_unedited_copy_always_merges_to_exactly_the_authors_pack()
    {
        // The commonest case by far, and the one that must never need a decision: somebody
        // who has changed nothing should be able to take an update without reading it.
        for (var seed = 0; seed < Runs; seed++)
        {
            var s = Generate(seed);

            var plan = PackUpdatePlan.Between(s.Base, s.Theirs, s.Base);

            Assert.True(!plan.Choices.Any(), $"an untouched copy was asked a question\n\n{s}");

            var merged = plan.Merge();

            Assert.Equal(
                s.Theirs.Mods.Select(m => $"{m.ModId}={m.Version}").Order().ToArray(),
                merged.Mods.Select(m => $"{m.ModId}={m.Version}").Order().ToArray());
        }
    }
}
