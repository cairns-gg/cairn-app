using Cairn.Core;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Moving everything Cairn keeps to another disk.
///
/// The refusals matter more than the copy: every one of them is a way to end up with the
/// data in two places and Cairn reading neither, and they all have to be decided before a
/// single byte is written. The copy itself has two properties that are invisible until
/// somebody's install stops working — the executable bit, and links staying links.
/// </summary>
[Collection(HomeEnvironment.Collection)]
public class HomeMigrationTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("cairn-move-").FullName;

    private string From => Path.Combine(_tmp, "old");
    private string To => Path.Combine(_tmp, "new");

    private static readonly Func<string, long?> PlentyOfRoom = _ => long.MaxValue;

    public HomeMigrationTests()
    {
        Directory.CreateDirectory(Path.Combine(From, "packs", "demo"));
        File.WriteAllText(Path.Combine(From, "packs", "demo", "pack.json"), """{"id":"demo"}""");
        File.WriteAllText(Path.Combine(From, "settings.json"), """{"UiScale":1.0}""");
    }

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private MovePlan PlanTo(string to, string? environment = null) =>
        HomeMigration.Plan(From, to, environment, PlentyOfRoom);

    /// <summary>Where the move would have repointed Cairn, had this been the real thing.</summary>
    private string? _repointedTo;

    /// <summary>
    /// Never the real CairnHome.SetPointer: that writes to the running user's own home
    /// directory, so a test calling it would leave the developer's launcher pointed at a
    /// temporary directory this class deletes on the way out.
    /// </summary>
    private MoveResult Move(MovePlan plan) =>
        HomeMigration.Move(plan, repoint: to => _repointedTo = to);

    [Fact]
    public void A_plan_measures_what_it_would_copy()
    {
        var plan = PlanTo(To);

        Assert.True(plan.CanMove);
        Assert.Equal(2, plan.Files);
        Assert.True(plan.Bytes > 0);
    }

    [Fact]
    public void Refused_when_CAIRN_HOME_is_set()
    {
        // The pointer would be written and then ignored: it looks like it worked, nothing
        // changes, and the reason is invisible.
        var plan = PlanTo(To, environment: "/somewhere/else");

        Assert.False(plan.CanMove);
        Assert.Contains("CAIRN_HOME", plan.Problem);
    }

    [Fact]
    public void Refused_when_the_destination_is_inside_the_source()
    {
        // Copying a tree into itself does not terminate.
        var plan = PlanTo(Path.Combine(From, "inner"));

        Assert.False(plan.CanMove);
        Assert.Contains("is inside", plan.Problem);
    }

    [Fact]
    public void Refused_when_the_destination_contains_the_source()
    {
        var plan = PlanTo(_tmp);

        Assert.False(plan.CanMove);
        Assert.Contains("contains", plan.Problem);
    }

    /// <summary>
    /// The review's reproduction: a link to the root, and a destination under the link. As
    /// strings the two are unrelated, so the plan allowed it and the copy walked into its
    /// own output — payload, nested/payload, nested/nested/payload — until stopped.
    /// </summary>
    [Fact]
    public void Refused_when_the_destination_is_inside_the_source_by_way_of_a_link()
    {
        var alias = Path.Combine(_tmp, "alias");
        Directory.CreateSymbolicLink(alias, From);

        var plan = PlanTo(Path.Combine(alias, "nested"));

        Assert.False(plan.CanMove);
        Assert.Contains("inside", plan.Problem);
    }

    [Fact]
    public void A_link_made_after_planning_is_still_refused_when_the_move_starts()
    {
        // A plan that was sound when it was made: the link does not exist yet. Built directly,
        // because Plan would also refuse a destination whose parent is not there.
        var alias = Path.Combine(_tmp, "alias");
        var plan = new MovePlan(From, Path.Combine(alias, "nested"), 2, 0, 1, null);

        Directory.CreateSymbolicLink(alias, From);

        Assert.Throws<MoveFailed>(() => Move(plan));
        Assert.False(Directory.Exists(Path.Combine(From, "nested")));
        Assert.Null(_repointedTo);
    }

    [Fact]
    public void A_sibling_with_a_shared_prefix_is_not_nesting()
    {
        // /data/cairn-old only reads as inside /data/cairn if the separator is forgotten,
        // and refusing it would block the obvious thing to call the new directory.
        var source = Path.Combine(_tmp, "cairn");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");

        var plan = HomeMigration.Plan(
            source, Path.Combine(_tmp, "cairn-old"), null, PlentyOfRoom);

        Assert.True(plan.CanMove);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void Refused_when_the_destination_is_not_empty()
    {
        Directory.CreateDirectory(To);
        File.WriteAllText(Path.Combine(To, "something-else.txt"), "not ours");

        var plan = PlanTo(To);

        Assert.False(plan.CanMove);
        Assert.Contains("not empty", plan.Problem);
    }

    [Fact]
    public void The_pointer_left_behind_does_not_block_moving_back()
    {
        // Moving away from the default empties that directory except for the pointer, which
        // is Cairn's own bookkeeping. Counting it as an occupant made the trip one-way:
        // somebody who moved to another disk and wanted to come back was told the original
        // was "not empty" — by the one file the move itself had put there.
        var previous = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", To);

        try
        {
            Directory.CreateDirectory(To);
            File.WriteAllText(Path.Combine(To, CairnHome.PointerName), From);

            Assert.True(HomeMigration.Plan(From, To, null, PlentyOfRoom).CanMove);

            // Anything else in there is still somebody's, and still a refusal.
            File.WriteAllText(Path.Combine(To, "notes.txt"), "mine");

            Assert.False(HomeMigration.Plan(From, To, null, PlentyOfRoom).CanMove);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
        }
    }

    [Fact]
    public void Refused_when_a_server_is_running()
    {
        // A socket means a process with files open on the tree about to be copied.
        Directory.CreateDirectory(Path.Combine(From, "run"));
        File.WriteAllText(Path.Combine(From, "run", "anego.sock"), "");

        var plan = PlanTo(To);

        Assert.False(plan.CanMove);
        Assert.Contains("anego", plan.Problem);
    }

    [Fact]
    public void Refused_when_there_is_not_enough_room()
    {
        var plan = HomeMigration.Plan(From, To, null, _ => 1);

        Assert.False(plan.CanMove);
        Assert.Contains("free", plan.Problem);
    }

    [Fact]
    public void Unknown_free_space_is_not_a_refusal()
    {
        // Not being able to tell is not a reason to stop; the copy finds out.
        Assert.True(HomeMigration.Plan(From, To, null, _ => null).CanMove);
    }

    [Fact]
    public void Moving_relocates_the_tree_and_removes_the_original()
    {
        var result = Move(PlanTo(To));

        Assert.True(File.Exists(Path.Combine(To, "packs", "demo", "pack.json")));
        Assert.True(File.Exists(Path.Combine(To, "settings.json")));

        // A move, not a copy. Somebody doing this is out of disk space, and leaving both
        // would answer that with two of everything.
        Assert.False(Directory.Exists(From));
        Assert.True(result.Freed > 0);
        Assert.Null(result.RemovalProblem);
        Assert.Equal(From, result.OldRoot);
    }

    [Fact]
    public void A_cancel_after_the_repoint_is_a_finished_move_with_its_clean_up_stopped()
    {
        // Ctrl-C landing just after the commit point. It used to escape as a cancelled move,
        // and the CLI then said nothing had been repointed and to delete the part-copy at
        // the destination — which was by then the live root.
        using var cts = new CancellationTokenSource();

        var result = HomeMigration.Move(PlanTo(To), repoint: to =>
        {
            _repointedTo = to;
            cts.Cancel();
        }, ct: cts.Token);

        Assert.Equal(To, _repointedTo);
        Assert.Equal(Lang.Get("move-cleanup-cancelled"), result.RemovalProblem);
        Assert.True(File.Exists(Path.Combine(To, "packs", "demo", "pack.json")));
    }

    [Fact]
    public void A_cancel_before_the_repoint_is_still_a_cancelled_move()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HomeMigration.Move(PlanTo(To), ct: cts.Token, repoint: to => _repointedTo = to));

        Assert.Null(_repointedTo);
        Assert.True(File.Exists(Path.Combine(From, "settings.json")));
    }

    [Fact]
    public void A_log_still_being_written_does_not_fail_the_move()
    {
        var log = Path.Combine(From, "logs", "cairn.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "before\n");

        // The launcher goes on logging while it moves — every file copied is a chance for a
        // line to land in the original after its copy was taken. Synchronous, unlike
        // Progress<T>, so the append happens between the copy and the check.
        var appending = new Appending(() => File.AppendAllText(log, "during\n"));

        var result = HomeMigration.Move(PlanTo(To), appending, repoint: to => _repointedTo = to);

        Assert.Equal(To, _repointedTo);
        Assert.True(File.Exists(Path.Combine(To, "logs", "cairn.log")));
        Assert.Null(result.RemovalProblem);
    }

    private sealed class Appending(Action append) : IProgress<MoveProgress>
    {
        public void Report(MoveProgress value) => append();
    }

    [Fact]
    public void The_executable_bit_survives()
    {
        // Without it every game binary arrives unrunnable and nothing launches. File.Copy
        // does carry the mode — checked here so it stays true.
        if (OperatingSystem.IsWindows()) return;

        var exe = Path.Combine(From, "games", "1.22.5", "Vintagestory");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "#!/bin/sh\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Move(PlanTo(To));

        var copied = Path.Combine(To, "games", "1.22.5", "Vintagestory");
        Assert.True(File.GetUnixFileMode(copied).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void A_link_arrives_as_a_link_rather_than_a_copy()
    {
        // Following it would flatten a macOS .app bundle and break its signature, and would
        // silently duplicate gigabytes for anybody who had already symlinked games/ onto
        // another disk to get out of this very problem.
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "big.zip"), new string('x', 4096));

        Directory.CreateSymbolicLink(Path.Combine(From, "linked"), elsewhere);

        var plan = PlanTo(To);
        Assert.Equal(1, plan.Links);

        // The link's 4 KB target is not counted, because it is not being copied.
        Assert.Equal(2, plan.Files);

        Move(plan);

        // A link, pointing where the original pointed — not a directory that happens to
        // hold the same 4 KB.
        var arrived = new DirectoryInfo(Path.Combine(To, "linked"));
        Assert.Equal(elsewhere, arrived.LinkTarget);
    }

    [Fact]
    public void Cairn_is_repointed_last_and_at_the_new_root()
    {
        // The ordering is the safety property: everything above it can fail with the old
        // root still live, and nothing is ever pointed at a tree still being written.
        Assert.Null(_repointedTo);

        Move(PlanTo(To));

        Assert.Equal(To, _repointedTo);
    }

    [Fact]
    public void Moving_from_somewhere_that_is_not_the_default_leaves_nothing_to_keep()
    {
        // The pointer lives at the default location. These temp roots are not it, so there
        // is nothing inside the old one that clearing it out would destroy.
        Assert.Null(Move(PlanTo(To)).KeepInOldRoot);
    }

    [Fact]
    public void Discarding_keeps_the_file_it_is_told_to()
    {
        // The pointer, when the old root was the default. Taking it would send Cairn back to
        // a default root that is now empty — the move undone by the tidying up.
        var pointer = Path.Combine(From, "home");
        File.WriteAllText(pointer, To);

        HomeMigration.DeleteOldRoot(From, keep: pointer);

        Assert.True(File.Exists(pointer));
        Assert.Equal(To, File.ReadAllText(pointer));

        // Everything else went, and the directory survives holding one line of text.
        Assert.False(Directory.Exists(Path.Combine(From, "packs")));
        Assert.Single(Directory.EnumerateFileSystemEntries(From));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Discarding_refuses_to_delete_the_live_root(bool rootExists)
    {
        // A mistyped path is how somebody would ask for this, and it is the one mistake that
        // cannot be walked back.
        //
        // Both cases, because the first version of this asked only the ambient one and so
        // proved nothing portable: it passed on a machine where ~/.cairn exists and failed
        // on CI, where the guard was being skipped entirely rather than being satisfied.
        var live = Path.Combine(_tmp, "live");
        if (rootExists) Directory.CreateDirectory(live);

        var previous = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", live);

        try
        {
            Assert.Equal(live, CairnPaths.Root);
            Assert.Throws<MoveFailed>(() => HomeMigration.DeleteOldRoot(live, null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
        }
    }

    [Fact]
    public void Discarding_the_folder_that_holds_the_live_root_is_refused_and_deletes_nothing()
    {
        // The review's reproduction: moved to <old>/live, then <old> discarded. Equality was
        // the whole check, so the CLI said the live root was not touched and then took it.
        var old = Path.Combine(_tmp, "old-parent");
        var live = Path.Combine(old, "live");
        var save = Path.Combine(live, "packs", "demo", "data", "Saves", "world.vcdbs");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllText(save, "a world");

        var defaultRoot = Path.Combine(_tmp, "default");
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(Path.Combine(defaultRoot, CairnHome.PointerName), live);

        var previous = Environment.GetEnvironmentVariable("CAIRN_DEFAULT_HOME");
        Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", defaultRoot);

        try
        {
            Assert.Equal(live, CairnPaths.Root);
            Assert.NotNull(HomeMigration.DiscardProblem(old));
            Assert.Throws<MoveFailed>(() => HomeMigration.DeleteOldRoot(old, null));
            Assert.True(File.Exists(save));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAIRN_DEFAULT_HOME", previous);
        }
    }

    [Fact]
    public void The_live_root_is_recognised_through_a_link_and_in_another_case()
    {
        var old = Path.Combine(_tmp, "old-parent");
        var live = Path.Combine(old, "live");
        Directory.CreateDirectory(live);

        var alias = Path.Combine(_tmp, "alias");
        Directory.CreateSymbolicLink(alias, old);

        var pointer = Path.Combine(_tmp, "no-pointer-here");

        // /tmp is itself a link on macOS, and its volumes ignore case: two spellings of one
        // directory that an ordinal comparison calls different places.
        Assert.NotNull(HomeMigration.DiscardProblem(alias, live, pointer));
        Assert.NotNull(HomeMigration.DiscardProblem(Path.Combine(alias, "live"), live, pointer));
        Assert.NotNull(HomeMigration.DiscardProblem(old.ToUpperInvariant(), live, pointer));
    }

    [Fact]
    public void A_folder_holding_the_pointer_deeper_than_its_top_is_refused()
    {
        // Kept only as a direct child, so a home directory holding ~/.cairn/home would lose it
        // to the recursive delete of .cairn, and Cairn would start in an empty default root.
        var homeDir = Path.Combine(_tmp, "user");
        var pointer = Path.Combine(homeDir, ".cairn", CairnHome.PointerName);
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, To);

        Assert.NotNull(HomeMigration.DiscardProblem(homeDir, To, pointer));

        // The ordinary case is still allowed: the old default root, pointer and all.
        Assert.Null(HomeMigration.DiscardProblem(Path.Combine(homeDir, ".cairn"), To, pointer));
    }

    [Fact]
    public void Discarding_unlinks_a_link_rather_than_deleting_through_it()
    {
        // What it points at is somewhere else and not ours to remove.
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keepme.txt"), "not ours");

        Directory.CreateSymbolicLink(Path.Combine(From, "linked"), elsewhere);

        HomeMigration.DeleteOldRoot(From, keep: null);

        Assert.False(Directory.Exists(From));
        Assert.True(File.Exists(Path.Combine(elsewhere, "keepme.txt")));
    }

    [Fact]
    public void A_refused_plan_repoints_nothing()
    {
        Assert.Throws<MoveFailed>(() => Move(PlanTo(To, environment: "/somewhere/else")));

        Assert.Null(_repointedTo);
    }

    [Fact]
    public void A_pack_s_recorded_install_directory_moves_with_it()
    {
        // An absolute path under the old root, which after the move names a copy Cairn no
        // longer reads.
        var local = Path.Combine(From, "packs", "demo", "local.json");
        new PackLocalState { InstallDirectory = Path.Combine(From, "games", "1.22.5-optimum") }
            .Save(local);

        var result = Move(PlanTo(To));

        Assert.Equal(1, result.Rewritten);

        var moved = PackLocalState.Load(Path.Combine(To, "packs", "demo", "local.json"));
        Assert.Equal(Path.Combine(To, "games", "1.22.5-optimum"), moved.InstallDirectory);
    }

    [Fact]
    public void An_install_directory_outside_the_root_is_left_alone()
    {
        // Somebody pointing a pack at a game they installed themselves. Rewriting that would
        // invent a path that never existed.
        var outside = OperatingSystem.IsWindows() ? @"C:\Games\Vintagestory" : "/opt/vintagestory";
        var local = Path.Combine(From, "packs", "demo", "local.json");
        new PackLocalState { InstallDirectory = outside }.Save(local);

        var result = Move(PlanTo(To));

        Assert.Equal(0, result.Rewritten);
        Assert.Equal(outside,
            PackLocalState.Load(Path.Combine(To, "packs", "demo", "local.json")).InstallDirectory);
    }

    [Fact]
    public void A_plan_that_cannot_go_ahead_is_refused_rather_than_attempted()
    {
        var plan = PlanTo(To, environment: "/somewhere/else");

        Assert.Throws<MoveFailed>(() => Move(plan));
        Assert.False(Directory.Exists(To));
    }

    // ---- choosing a location before there is anything at it ----

    /// <summary>
    /// A root that does not exist yet can still be pointed somewhere, and it was refused.
    ///
    /// Nothing creates the root until Cairn writes something — a setting changed, a pack
    /// made, a ModDB page browsed — so a fresh install has none. That is exactly when
    /// somebody who cares where their files go opens Preferences to say where, and they were
    /// told there was nothing to move: choosing a location apparently required something to
    /// have been put in the wrong place first.
    /// </summary>
    [Fact]
    public void A_root_that_is_not_there_yet_can_still_be_pointed_somewhere()
    {
        var fresh = Path.Combine(_tmp, "never-used");
        var plan = HomeMigration.Plan(fresh, To, null, PlentyOfRoom);

        Assert.True(plan.CanMove);
        Assert.Equal(0, plan.Files);
        Assert.Equal(0, plan.Bytes);
    }

    /// <summary>
    /// And doing it makes the directory and repoints, which is the whole of a move when
    /// there is nothing to copy. Walk would throw on the missing source rather than yielding
    /// nothing, and it is reached twice — once to copy and once to verify.
    /// </summary>
    [Fact]
    public void Pointing_an_empty_root_somewhere_makes_it_and_repoints()
    {
        var fresh = Path.Combine(_tmp, "never-used");
        var result = Move(HomeMigration.Plan(fresh, To, null, PlentyOfRoom));

        Assert.Equal(0, result.Files);
        Assert.Equal(To, _repointedTo);
        Assert.True(Directory.Exists(To));
    }

    /// <summary>
    /// An existing but empty root is the same case, and was already allowed — worth holding
    /// alongside the missing one so the two cannot drift apart.
    /// </summary>
    [Fact]
    public void An_empty_root_is_the_same_as_a_missing_one()
    {
        var empty = Path.Combine(_tmp, "empty");
        Directory.CreateDirectory(empty);

        var plan = HomeMigration.Plan(empty, To, null, PlentyOfRoom);

        Assert.True(plan.CanMove);
        Assert.Equal(0, plan.Files);
    }

    /// <summary>
    /// The refusals that still apply. Being allowed to point an empty root somewhere is not
    /// permission to point it at an occupied folder, at itself, or past CAIRN_HOME.
    /// </summary>
    [Fact]
    public void An_empty_root_is_still_refused_everything_a_full_one_is()
    {
        var fresh = Path.Combine(_tmp, "never-used");

        Assert.False(HomeMigration.Plan(fresh, fresh, null, PlentyOfRoom).CanMove);
        Assert.False(HomeMigration.Plan(fresh, To, "/somewhere", PlentyOfRoom).CanMove);

        var occupied = Path.Combine(_tmp, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "someone-elses.txt"), "hello");

        Assert.False(HomeMigration.Plan(fresh, occupied, null, PlentyOfRoom).CanMove);
    }
}
