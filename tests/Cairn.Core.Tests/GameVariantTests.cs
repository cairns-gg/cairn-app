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
        Assert.Contains("Optimum", install.Describe());
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
}
