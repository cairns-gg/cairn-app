using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What a delete prompt gets to say. Measured, not described: it is the last thing read
/// before something irreversible.
/// </summary>
public class PackContentsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-contents-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public PackContentsTests()
    {
        _store = new PackStore(Path.Combine(_root, "packs"));
        _store.Create("anego", "1.22.5");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void AddMod(string name, int bytes)
    {
        var dir = _store.ModsDir("anego");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
    }

    private void AddWorld(string name, int bytes)
    {
        var saves = Path.Combine(_store.DataDir("anego"), "Saves");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, name + ".vcdbs"), new byte[bytes]);
    }

    private PackContents Contents() => PackContents.Of(_store, "anego");

    [Fact]
    public void An_empty_pack_has_nothing_to_itemise()
    {
        var contents = Contents();

        Assert.Equal(0, contents.Mods);
        Assert.Empty(contents.Worlds);
        Assert.Empty(contents.Describe());
    }

    [Fact]
    public void Mods_are_counted_and_measured()
    {
        AddMod("olla_1.1.0.zip", 2048);
        AddMod("glassview_1.3.0.zip", 1024);

        var contents = Contents();

        Assert.Equal(2, contents.Mods);
        Assert.Equal(3072, contents.ModsBytes);
        Assert.Contains(contents.Describe(), l => l.Contains("2 downloaded mods (3 KB)"));
    }

    [Fact]
    public void Worlds_are_named_because_which_ones_is_the_question()
    {
        AddWorld("Homestead", 4096);
        AddWorld("Test Flats", 2048);

        var line = Assert.Single(Contents().Describe(), l => l.Contains("world"));

        Assert.Contains("2 worlds", line);
        Assert.Contains("Homestead", line);
        Assert.Contains("Test Flats", line);
    }

    [Fact]
    public void Many_worlds_name_a_few_and_count_the_rest()
    {
        foreach (var i in Enumerable.Range(1, 7)) AddWorld($"World {i}", 512);

        var line = Assert.Single(Contents().Describe(), l => l.Contains("world"));

        Assert.Contains("7 worlds", line);
        Assert.Contains("and 4 more", line);
    }

    [Fact]
    public void Worlds_come_before_mods_because_they_are_what_cannot_be_refetched()
    {
        AddMod("olla_1.1.0.zip", 2048);
        AddWorld("Homestead", 4096);

        var lines = Contents().Describe();

        Assert.Contains("world", lines[0]);
        Assert.Contains("downloaded mod", lines[1]);
    }

    [Fact]
    public void The_total_is_everything_under_the_pack_not_just_the_parts_listed()
    {
        AddMod("olla_1.1.0.zip", 2048);
        AddWorld("Homestead", 4096);

        var contents = Contents();

        // pack.json and the lockfile are going too, so the total exceeds mods + worlds.
        Assert.True(contents.TotalBytes > contents.ModsBytes + contents.WorldsBytes,
            $"total {contents.TotalBytes} did not exceed {contents.ModsBytes} + {contents.WorldsBytes}");
    }

    [Fact]
    public void A_world_sized_pack_reads_in_the_units_people_use()
    {
        Assert.Equal("0 B", Bytes.Human(0));
        Assert.Equal("2 KB", Bytes.Human(2048));
        Assert.Equal("29 MB", Bytes.Human(29L * 1024 * 1024));
        Assert.Equal("5.9 GB", Bytes.Human(6_334_115_840L));
    }
}
