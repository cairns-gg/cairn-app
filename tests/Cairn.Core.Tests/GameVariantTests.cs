using Cairn.Core;
using Cairn.Core.Games;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// A modified game build living beside the stock one.
///
/// The whole point is that it never arrives by accident. A fork reports the version it was
/// forked from, so it is indistinguishable from the real game by metadata alone — and one
/// silently satisfying every pack that asks for that version is the failure worth ruling
/// out by construction, not by care.
/// </summary>
public class GameVariantTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-variant-" + Guid.NewGuid().ToString("n")[..8]);

    public GameVariantTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Enough of an install for TryAt to accept it. The version comes from the API dll's
    /// metadata in real life, which a test cannot forge — so these read as version "" and
    /// the assertions here are about the variant marker rather than version matching.
    /// </summary>
    private string Install(string name, string? variant = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");

        if (variant is not null)
            File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), variant);

        return dir;
    }

    [Fact]
    public void A_plain_install_is_not_a_variant()
    {
        var install = GameInstall.TryAt(Install("1.22.5"));

        Assert.NotNull(install);
        Assert.Null(install.Variant);
        Assert.False(install.IsVariant);
    }

    [Fact]
    public void A_marked_install_carries_its_label()
    {
        var install = GameInstall.TryAt(Install("1.22.5-optimum", "Optimum"));

        Assert.NotNull(install);
        Assert.True(install.IsVariant);
        Assert.Equal("Optimum", install.Variant);
        Assert.Contains("Optimum", install.Describe);
    }

    [Fact]
    public void An_empty_marker_still_means_not_the_stock_game()
    {
        // The file being there is the statement; what it says is only the label. A blank
        // one reading as ordinary would be the marker failing at its single job.
        var install = GameInstall.TryAt(Install("1.22.5-optimum", ""));

        Assert.NotNull(install);
        Assert.True(install.IsVariant);
        Assert.Equal("1.22.5-optimum", install.Variant);
    }

    /// <summary>
    /// A build that replaces the client runs its own launcher, not the game's.
    ///
    /// Optimum's install is byte-identical vanilla files plus its own executable — it
    /// patches at startup from there. Launching Vintagestory.exe out of that folder runs
    /// the stock game while every message on screen says Optimum, which is the worst of
    /// both: no optimisations and no way to tell.
    /// </summary>
    [Fact]
    public void A_variant_may_name_the_executable_to_launch()
    {
        var dir = Install("1.22.5-optimum");
        var launcher = OperatingSystem.IsWindows() ? "Optimum.exe" : "Optimum";

        File.WriteAllText(Path.Combine(dir, launcher), "");
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker),
            $$"""{"label":"Optimum","executable":"{{launcher}}"}""");

        var install = GameInstall.TryAt(dir);

        Assert.NotNull(install);
        Assert.Equal("Optimum", install.Variant);
        Assert.Equal(Path.Combine(dir, launcher), install.Executable);
    }

    [Fact]
    public void A_marker_naming_a_launcher_that_is_not_there_is_refused()
    {
        // Falling back to the stock binary would be the silent wrong answer again, just
        // arrived at from the other direction.
        var dir = Install("1.22.5-optimum");
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker),
            """{"label":"Optimum","executable":"NotThere.exe"}""");

        Assert.Null(GameInstall.TryAt(dir));
    }

    [Fact]
    public void A_marker_executable_cannot_point_outside_the_install()
    {
        // The launch target comes from a file in a directory; a name carrying a path
        // would point it anywhere on the machine.
        var dir = Install("1.22.5-optimum");
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker),
            """{"label":"Optimum","executable":"../../evil"}""");

        // Refused outright, rather than falling back to the stock executable sitting in the
        // same directory — which is what this used to do. The fallback was the substitution
        // the marker exists to prevent, reached by writing something the marker was never
        // allowed to say: an install labelled Optimum, running vanilla, with nothing able to
        // tell. The install being invisible is the loud failure, and the right one.
        Assert.Null(GameInstall.TryAt(dir));
    }

    [Fact]
    public void A_variant_is_never_handed_to_a_pack_that_asked_for_the_version()
    {
        // The failure this exists to prevent: the stock install is missing from its
        // expected folder, and a fork of the same version is sitting right beside it.
        Install("1.22.5-optimum", "Optimum");

        var store = new GameStore(_root);

        Assert.Null(store.Find("1.22.5"));
        Assert.Null(new GameLibrary(store, system: null).ForVersion("1.22.5"));
    }

    [Fact]
    public void A_variant_is_never_the_fallback_either()
    {
        // Fallback is "something is better than nothing", and a modified client is not.
        Install("1.22.5-optimum", "Optimum");

        Assert.Null(new GameLibrary(new GameStore(_root), system: null).Fallback);
    }

    [Fact]
    public void A_variant_is_offered_only_beside_the_version_it_is_a_build_of()
    {
        // Offered for the version it forked from, and for nothing else. An Optimum build
        // of 1.22.5 is not a 1.22.6 install, however much somebody on 1.22.6 might want
        // one — Optimum routinely lags the game by a release, and quietly answering the
        // wrong version is how that lag turns into a pack running a client it was never
        // resolved against.
        Install("1.22.5-optimum", "Optimum");

        var store = new GameStore(_root);
        var library = new GameLibrary(store, system: null);

        // Read back rather than assumed: a stub dll carries no metadata, so what this
        // install reports is whatever ReadVersion falls back to. The pairing under test is
        // "its own version" against "any other", not any particular string.
        var reported = store.ListInstalled().Single().Version;

        Assert.Single(library.ChoicesFor(reported));
        Assert.Empty(library.ChoicesFor(reported + "-and-then-some"));
    }

    [Fact]
    public void A_variant_is_still_listed_as_something_you_could_choose()
    {
        // Excluded from every automatic path, offered on the one deliberate one.
        Install("1.22.5-optimum", "Optimum");

        var installed = new GameStore(_root).ListInstalled().ToList();

        Assert.Single(installed);
        Assert.True(installed[0].IsVariant);
    }

    // ---- a pack's recorded choice, against a version that can move ----

    /// <summary>
    /// A choice is a directory and a pack's game version is not fixed, so the two can come
    /// apart. When they do the choice must stop applying: the pack's mods were resolved
    /// against the version it now targets, and a client nothing in it was chosen for is
    /// exactly what variants exist to prevent.
    /// </summary>
    /// <summary>
    /// A variant whose reported version is the directory's name.
    ///
    /// Planted outside the store, because the store names its directories by version and
    /// two installs cannot share one name. That is also how a real variant reaches this:
    /// by a path recorded in a pack, not by being listed.
    /// </summary>
    private string Elsewhere(string version, string variant)
    {
        var dir = Path.Combine(_root, "elsewhere", version);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows()
            ? "Vintagestory.exe" : "Vintagestory"), "");
        File.WriteAllText(Path.Combine(dir, "VintagestoryAPI.dll"), "");
        File.WriteAllText(Path.Combine(dir, GameInstall.VariantMarker), variant);

        return dir;
    }

    [Fact]
    public void A_choice_for_another_version_is_ignored_rather_than_honoured()
    {
        var stock = Install("1.22.4");
        var variant = Elsewhere("1.22.5", "Optimum");

        var library = new GameLibrary(new GameStore(_root), null);

        var resolved = library.ResolveFor("1.22.4", variant);

        Assert.Equal(GameLibrary.ChoiceState.WrongVersion, resolved.State);
        Assert.Equal(stock, resolved.Install?.Directory);

        // Reported rather than erased, so retargeting back picks it up again — nobody
        // should lose a twenty-minute build by trying another version for a minute.
        Assert.NotNull(resolved.Chosen);
        Assert.Equal("Optimum", resolved.Chosen.Variant);
    }

    [Fact]
    public void A_choice_for_this_version_is_honoured()
    {
        Install("1.22.5");
        var variant = Elsewhere("1.22.5", "Optimum");

        var resolved = new GameLibrary(new GameStore(_root), null).ResolveFor("1.22.5", variant);

        Assert.Equal(GameLibrary.ChoiceState.Honoured, resolved.State);
        Assert.Equal(variant, resolved.Install?.Directory);
        Assert.True(resolved.Install!.IsVariant);
    }

    [Fact]
    public void A_choice_whose_directory_has_gone_falls_back_and_is_reported()
    {
        var stock = Install("1.22.5");

        var resolved = new GameLibrary(new GameStore(_root), null)
            .ResolveFor("1.22.5", Path.Combine(_root, "deleted-build"));

        Assert.Equal(GameLibrary.ChoiceState.Missing, resolved.State);
        Assert.Equal(stock, resolved.Install?.Directory);
        Assert.Null(resolved.Chosen);
    }

    [Fact]
    public void No_choice_follows_the_stock_install()
    {
        var stock = Install("1.22.5");
        Install("1.22.5-optimum", "Optimum");

        var resolved = new GameLibrary(new GameStore(_root), null).ResolveFor("1.22.5", null);

        // Never the variant: it runs because somebody said so, and nobody has.
        Assert.Equal(GameLibrary.ChoiceState.None, resolved.State);
        Assert.Equal(stock, resolved.Install?.Directory);
    }
}
