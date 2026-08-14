using Cairn.Core.Games;
using Cairn.Core.Launch;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the stores address after the root has moved underneath them.
///
/// Preferences → Move… repoints Cairn while it is running and then deletes the tree it came
/// from, so anything holding the root it read at start-up is left addressing a directory
/// that is no longer there. That shipped: a launcher that had just moved went on showing the
/// old paths in the pack pane, offered them for a new pack, and re-downloaded every mod into
/// the directory the move had emptied — all of it correct again after a restart, which is
/// the tell.
///
/// Sandboxed with CAIRN_HOME rather than the pointer file, because what is under test is a
/// root that changes mid-process and not how the change was decided. SetPointer would write
/// to the running user's own home directory.
/// </summary>
[Collection(HomeEnvironment.Collection)]
public class MovedRootTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("cairn-moved-root-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    private string First => Path.Combine(_tmp, "first");
    private string Second => Path.Combine(_tmp, "second");

    public MovedRootTests() => Environment.SetEnvironmentVariable("CAIRN_HOME", First);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void A_pack_store_built_before_the_move_follows_it()
    {
        // The one the bug report was about: the pack pane shows this string, and PackSyncer
        // installs into it. Held to the old root, a launch downloads the whole pack again
        // into a directory nothing will read after the next start.
        var store = new PackStore();

        Assert.Equal(Path.Combine(First, "packs", "demo", "Mods"), store.ModsDir("demo"));

        Environment.SetEnvironmentVariable("CAIRN_HOME", Second);

        Assert.Equal(Path.Combine(Second, "packs"), store.PacksRoot);
        Assert.Equal(Path.Combine(Second, "packs", "demo", "Mods"), store.ModsDir("demo"));
    }

    [Fact]
    public void The_game_and_runtime_stores_follow_it_too()
    {
        // These decide what a launch runs, so a stale one launches from a directory the move
        // has already deleted.
        var games = new GameStore();
        var runtimes = new RuntimeStore();

        Environment.SetEnvironmentVariable("CAIRN_HOME", Second);

        Assert.Equal(Path.Combine(Second, "games"), games.Root);
        Assert.Equal(Path.Combine(Second, "runtimes"), runtimes.Root);
    }

    [Fact]
    public void So_do_the_caches_and_the_session()
    {
        // The session least visibly of all: written to the old root, one login goes missing
        // at the next restart and nothing says why.
        var icons = new ModIconCache(new HttpClient());
        var updates = new ModUpdateCache();
        var data = new PackData(new PackStore());

        Environment.SetEnvironmentVariable("CAIRN_HOME", Second);

        Assert.Equal(Path.Combine(Second, "cache", "icons"), icons.Root);
        Assert.Equal(Path.Combine(Second, "cache", "update-checks"), updates.Root);
        Assert.Equal(Path.Combine(Second, "session.json"), data.SessionPath);
    }

    [Fact]
    public void A_root_that_was_given_explicitly_is_still_honoured()
    {
        // Reading through must not turn into ignoring the argument: cairn-server passes
        // ServersRoot to keep its installs apart from the client's, and the tests pass temp
        // directories.
        var store = new PackStore(Path.Combine(_tmp, "elsewhere"));
        var games = new GameStore(CairnPaths.ServersRoot);
        var serversWas = games.Root;

        Environment.SetEnvironmentVariable("CAIRN_HOME", Second);

        Assert.Equal(Path.Combine(_tmp, "elsewhere"), store.PacksRoot);
        Assert.Equal(serversWas, games.Root);
    }
}
