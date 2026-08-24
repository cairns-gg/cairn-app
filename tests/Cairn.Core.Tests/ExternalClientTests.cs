using Cairn.Core;
using Cairn.Core.Games;
using Cairn.Core.Runtime;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A client somebody built themselves and pointed Cairn at.
///
/// Two rules are being held here and they pull against each other. A modified client must
/// never run because Cairn guessed — so an unrecorded directory reads as whatever it looks
/// like, and looking like the stock game is not a variant. And a recorded one must go on
/// being read as what they said it was across a rebuild — so the record lives on Cairn's
/// side rather than as a marker in a tree whose packager rewrites it.
/// </summary>
public class ExternalClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-external-" + Guid.NewGuid().ToString("n")[..8]);

    public ExternalClientTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string GamesRoot => Path.Combine(_root, "games");

    private static string StockName =>
        OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory";

    private static string LauncherName =>
        OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum";

    /// <summary>
    /// A directory somebody built in: the stock client's files, plus Optimum's launcher
    /// beside them. That layout is the point — the vanilla binary really is sitting there,
    /// which is what makes "run the game in this directory" the wrong answer.
    /// </summary>
    private string Built(string name, bool withLauncher = true)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, StockName), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        if (withLauncher) File.WriteAllText(Path.Combine(dir, LauncherName), "");

        return dir;
    }

    /// <summary>
    /// An install with a version the rules can be applied to. A fake VintagestoryAPI.dll
    /// carries no metadata, so a located install always reports "unknown" — see the second
    /// <c>Inspect</c> overload for why the policy is separable from the lookup.
    /// </summary>
    private static GameInstall Found(string dir, string version) => new()
    {
        Directory = dir,
        Executable = Path.Combine(dir, StockName),
        Version = version,
        Architecture = ExecutableArch.X64,
        RequiredFramework = new Version(10, 0, 0),
    };

    [Fact]
    public void An_unrecorded_directory_is_not_a_variant()
    {
        // The rule that everything else here has to not break: a build sitting on the disk
        // is the stock game as far as Cairn is concerned until somebody says otherwise.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        var install = store.At(dir);

        Assert.NotNull(install);
        Assert.False(install.IsVariant);
        Assert.Equal(Path.Combine(dir, StockName), install.Executable);
    }

    [Fact]
    public void A_recorded_directory_runs_the_launcher_it_was_recorded_with()
    {
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        var install = store.At(dir);

        Assert.NotNull(install);
        Assert.True(install.IsVariant);
        Assert.Equal("Optimum", install.Variant);

        // The whole reason the record carries an executable. The vanilla binary is right
        // there in the same directory, and starting it gets you the stock game while every
        // message says Optimum.
        Assert.Equal(Path.Combine(dir, LauncherName), install.Executable);
    }

    [Fact]
    public void A_record_survives_the_marker_being_rewritten_away()
    {
        // Which is what a rebuild does: Optimum's packager rewrites its output directory, so
        // a .cairn-variant left in there does not survive one. This is the failure the whole
        // registry exists for — the pack goes on pointing at a directory that has quietly
        // gone back to reading as the stock game.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        File.Delete(Path.Combine(dir, GameInstall.VariantMarker));
        Assert.False(File.Exists(Path.Combine(dir, GameInstall.VariantMarker)));

        var install = store.At(dir);

        Assert.NotNull(install);
        Assert.True(install.IsVariant);
        Assert.Equal(Path.Combine(dir, LauncherName), install.Executable);
    }

    [Fact]
    public void A_record_whose_launcher_is_gone_is_refused_rather_than_fallen_back_from()
    {
        // A rebuild that renames the launcher is the case. Falling back to the stock binary
        // beside it would launch vanilla under the name Optimum, which is the one outcome
        // worth refusing to start over.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));
        File.Delete(Path.Combine(dir, LauncherName));

        Assert.Null(store.At(dir));
    }

    [Fact]
    public void A_record_never_points_outside_its_own_directory()
    {
        // The record names a binary Cairn hands to a process launcher, so a name carrying a
        // path could point the launch anywhere on the machine.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum",
            Path.Combine("..", "..", "bin", "anything")));

        Assert.Null(store.At(dir));
    }

    [Fact]
    public void Re_pointing_at_a_directory_replaces_its_record()
    {
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", StockName));
        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        Assert.Single(store.External.All);
        Assert.Equal(LauncherName, store.External.All[0].Executable);
    }

    [Fact]
    public void Forgetting_leaves_the_directory_alone()
    {
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        Assert.True(store.External.Forget(dir));
        Assert.False(store.External.Forget(dir));

        // Their twenty minutes, and never Cairn's to spend again.
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, LauncherName)));

        // And it reads as the stock game again, which is what drops the packs that were
        // pointed at it back onto it.
        Assert.False(store.At(dir)!.IsVariant);
    }

    [Fact]
    public void A_record_for_a_directory_that_is_gone_is_kept_but_not_listed()
    {
        // An external drive that is not plugged in right now is the case. Dropping the
        // record would forget a choice nobody un-made; listing it would offer an install
        // that cannot start.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));
        Directory.Delete(dir, recursive: true);

        Assert.Single(store.External.All);
        Assert.Empty(store.ListExternal());
    }

    [Fact]
    public void A_pack_pointed_at_one_launches_it()
    {
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        var library = new GameLibrary(store, system: null);
        var resolved = library.ResolveFor(store.At(dir)!.Version, dir);

        Assert.Equal(GameLibrary.ChoiceState.Honoured, resolved.State);
        Assert.Equal(Path.Combine(dir, LauncherName), resolved.Install!.Executable);
        Assert.True(library.IsExternal(resolved.Install));
    }

    [Fact]
    public void A_pack_pointed_at_a_forgotten_client_falls_back_to_stock()
    {
        // The sharp case, and the one this was found by. Their tree holds a copy of the
        // vanilla client — that is what Optimum's output is — so a stale choice that is
        // still honoured runs the stock game out of somebody's build directory, silently,
        // right after they asked Cairn to stop using it.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        var library = new GameLibrary(store, system: null);
        var version = store.At(dir)!.Version;

        Assert.Equal(GameLibrary.ChoiceState.Honoured, library.ResolveFor(version, dir).State);

        store.External.Forget(dir);

        var after = library.ResolveFor(version, dir);

        Assert.Equal(GameLibrary.ChoiceState.NotAVariant, after.State);
        Assert.NotEqual(dir, after.Install?.Directory);
    }

    [Fact]
    public void A_pack_that_retargets_stops_using_it()
    {
        // The rule that already applies to Cairn's own builds, and it has to apply here for
        // the same reason: the pack's mods were resolved against the version it now targets,
        // so a client nothing in it was chosen for is a mismatch rather than an override.
        var dir = Built("publish");
        var store = new GameStore(GamesRoot);

        store.External.Remember(new ExternalClient(dir, "Optimum", LauncherName));

        var library = new GameLibrary(store, system: null);
        var resolved = library.ResolveFor("1.21.5", dir);

        Assert.Equal(GameLibrary.ChoiceState.WrongVersion, resolved.State);
    }

    // ---- what a picked directory has to be ----

    [Fact]
    public void A_folder_that_is_not_an_install_is_refused()
    {
        var empty = Path.Combine(_root, "not-a-game");
        Directory.CreateDirectory(empty);

        var found = ClientAdoption.Inspect(empty, gameVersion: null);

        Assert.Equal(AdoptionProblem.NotAnInstall, found.Problem);
        Assert.Null(found.Client);
    }

    [Fact]
    public void The_stock_game_is_refused()
    {
        // Not pedantry: without a launcher there is nothing to run but the vanilla binary,
        // so recording it would produce a pack announcing Optimum and playing the stock
        // game. The message says exactly that, because "this looks fine to me" is what the
        // person picking it is thinking.
        var dir = Built("vanilla", withLauncher: false);

        var found = ClientAdoption.Inspect(Found(dir, "1.22.7"), dir, gameVersion: null);

        Assert.Equal(AdoptionProblem.NoLauncher, found.Problem);
        Assert.Contains("stock game", found.Message);
    }

    [Fact]
    public void A_client_for_another_version_is_refused_at_the_picker()
    {
        // Rather than recorded and then silently ignored at launch, which is what
        // ResolveFor would do with it — correct, and impossible to work out from the
        // outside.
        var dir = Built("publish");

        var found = ClientAdoption.Inspect(Found(dir, "1.22.5"), dir, gameVersion: "1.22.7");

        Assert.Equal(AdoptionProblem.WrongVersion, found.Problem);
        Assert.Contains("1.22.5", found.Message);
        Assert.Contains("1.22.7", found.Message);
    }

    [Fact]
    public void A_client_naming_no_version_is_refused()
    {
        var dir = Built("publish");

        var found = ClientAdoption.Inspect(Found(dir, "unknown"), dir, gameVersion: "1.22.7");

        Assert.Equal(AdoptionProblem.NoVersion, found.Problem);
    }

    [Fact]
    public void A_client_for_this_version_is_taken_with_its_launcher()
    {
        var dir = Built("publish");

        var found = ClientAdoption.Inspect(Found(dir, "1.22.7"), dir, gameVersion: "1.22.7");

        Assert.True(found.Ok);
        Assert.Equal(dir, found.Client!.Directory);
        Assert.Equal(LauncherName, found.Client.Executable);
        Assert.Equal("Optimum", found.Client.Label);

        // What the confirmation shows: the directory, the version the rules were applied
        // to, and the binary that will actually start.
        Assert.Contains(dir, found.Message);
        Assert.Contains("1.22.7", found.Message);
        Assert.Contains(LauncherName, found.Message);
    }

    [Fact]
    public void A_folder_holding_the_install_is_accepted()
    {
        // On macOS a folder picker will not enter a bundle, so the only thing that can be
        // chosen is the folder above the install. Picking the parent by mistake is the
        // commonest way to get this wrong on every other platform.
        var dir = Built(Path.Combine("outer", "vintagestory"));
        var outer = Path.GetDirectoryName(dir)!;

        var found = ClientAdoption.Inspect(outer, gameVersion: null);

        // The version cannot be read from a forged dll, so this stops at NoVersion — which
        // is already past the lookup, and the lookup is what is being asserted.
        Assert.Equal(AdoptionProblem.NoVersion, found.Problem);
        Assert.Contains(dir, found.Message);
    }
}
