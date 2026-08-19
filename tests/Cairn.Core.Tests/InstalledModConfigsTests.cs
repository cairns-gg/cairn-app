using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Bringing somebody's mod settings across with their mods.
///
/// The third thing an import found and left behind, after the mods and the worlds. Plenty of
/// mods only get along once a value has been changed — Terrain Slabs wants Footprints named
/// in a list before the two behave — so a pack with the right mods and the authors' defaults
/// is not the thing that was being played, and the person who imported it has no way of
/// knowing which of forty mods lost the setting that made it work.
///
/// What this does not do is make the pack carry those settings *for other people*. That is
/// the manifest's modConfig and the Mod config tab, chosen a value at a time on purpose.
/// These files are what that tab then has to offer.
/// </summary>
public class InstalledModConfigsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-modconf-" + Guid.NewGuid().ToString("n")[..8]);

    private string Install => Path.Combine(_root, "VintagestoryData");
    private string Pack => Path.Combine(_root, "packs", "mine", "data");

    public InstalledModConfigsTests() =>
        Directory.CreateDirectory(InstalledModConfigs.DirectoryIn(Install));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string text = "{}")
    {
        var path = Path.Combine(InstalledModConfigs.DirectoryIn(Install), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string InPack(string relative) =>
        Path.Combine(InstalledModConfigs.DirectoryIn(Pack), relative);

    [Fact]
    public void What_is_there_is_counted_and_weighed()
    {
        Write("terrainslabs.json", new string('x', 100));
        Write("watersheds.yaml", new string('y', 50));

        var found = InstalledModConfigs.Measure(Install);

        Assert.Equal(2, found.Files);
        Assert.Equal(150, found.Bytes);
        Assert.True(found.Any);
    }

    /// <summary>
    /// An install with no settings at all is the ordinary state of a fresh one, and the offer
    /// is simply not made. Nothing here is a failure.
    /// </summary>
    [Fact]
    public void A_folder_with_nothing_in_it_offers_nothing()
    {
        Assert.False(InstalledModConfigs.Measure(Install).Any);
        Assert.False(InstalledModConfigs.Measure(Path.Combine(_root, "nowhere")).Any);
        Assert.Equal(0, InstalledModConfigs.CopyInto(Path.Combine(_root, "nowhere"), Pack));
    }

    [Fact]
    public void They_are_copied_into_the_packs_own_data_path()
    {
        Write("terrainslabs.json", """{"compatibleMods":["footprints"]}""");

        Assert.Equal(1, InstalledModConfigs.CopyInto(Install, Pack));

        Assert.Equal("""{"compatibleMods":["footprints"]}""",
            File.ReadAllText(InPack("terrainslabs.json")));
    }

    /// <summary>
    /// Copied, never moved — the rule everywhere Cairn touches somebody's own data path. The
    /// plain Vintage Story they had goes on working exactly as it did, which is the promise
    /// the import screen makes in as many words.
    /// </summary>
    [Fact]
    public void The_originals_stay_where_they_were()
    {
        Write("terrainslabs.json");

        InstalledModConfigs.CopyInto(Install, Pack);

        Assert.True(File.Exists(
            Path.Combine(InstalledModConfigs.DirectoryIn(Install), "terrainslabs.json")));
    }

    /// <summary>
    /// Some mods keep a folder rather than a file, so the walk goes all the way down. Copying
    /// only the top level would take half a mod's settings and leave no sign of it.
    /// </summary>
    [Fact]
    public void A_mod_that_keeps_a_folder_has_all_of_it_brought_across()
    {
        Write(Path.Combine("carryon", "blocks.json"));
        Write(Path.Combine("carryon", "nested", "more.json"));

        Assert.Equal(2, InstalledModConfigs.CopyInto(Install, Pack));

        Assert.True(File.Exists(InPack(Path.Combine("carryon", "nested", "more.json"))));
    }

    /// <summary>
    /// What is already in the pack was put there deliberately and this is a convenience, so
    /// it never writes over one. The same rule the world copy keeps.
    /// </summary>
    [Fact]
    public void Nothing_the_pack_already_has_is_written_over()
    {
        Write("terrainslabs.json", "from the install");

        Directory.CreateDirectory(InstalledModConfigs.DirectoryIn(Pack));
        File.WriteAllText(InPack("terrainslabs.json"), "the pack's own");

        Assert.Equal(0, InstalledModConfigs.CopyInto(Install, Pack));
        Assert.Equal("the pack's own", File.ReadAllText(InPack("terrainslabs.json")));
    }

    /// <summary>
    /// One unreadable file does not abandon the other forty. None of this is load-bearing:
    /// a mod writes its own defaults when a settings file is missing, which is exactly the
    /// state the pack would have been in without any of this.
    /// </summary>
    [Fact]
    public void One_file_that_cannot_be_read_does_not_stop_the_rest()
    {
        Write("good.json");
        Write("locked.json");

        var locked = Path.Combine(InstalledModConfigs.DirectoryIn(Install), "locked.json");

        // A link pointing nowhere: copied by following it, this throws; it is skipped
        // instead, along with every other link — what one points at is not ours to copy.
        File.Delete(locked);
        File.CreateSymbolicLink(locked, Path.Combine(_root, "not-there.json"));

        Assert.Equal(1, InstalledModConfigs.CopyInto(Install, Pack));
        Assert.True(File.Exists(InPack("good.json")));
        Assert.False(File.Exists(InPack("locked.json")));
    }
}
