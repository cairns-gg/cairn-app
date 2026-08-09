using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Bringing a world out of a plain Vintage Story install and into a pack.
///
/// A pack has its own data path, so a world in the player's own install is not reachable
/// from the pack holding the mods it was made with — and a world made under a mod set
/// generally cannot be opened without it. Cairn used to leave those worlds alone on the
/// grounds that it could not know which pack, if any, they belonged to. Importing an install
/// is the moment that stops being true.
///
/// Copied, never moved: Cairn does not write to the player's data path, and a world taken
/// out of it would open nowhere else.
/// </summary>
public class InstalledWorldsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-worlds-" + Guid.NewGuid().ToString("n")[..8]);

    private string Saves => Path.Combine(_root, "VintagestoryData", "Saves");
    private string PackData => Path.Combine(_root, "pack", "data");

    public InstalledWorldsTests() => Directory.CreateDirectory(Saves);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteWorld(string name, int bytes = 4096, DateTime? played = null)
    {
        var path = Path.Combine(Saves, name + ".vcdbs");
        File.WriteAllBytes(path, new byte[bytes]);

        if (played is { } when) File.SetLastWriteTimeUtc(path, when);

        return path;
    }

    [Fact]
    public void A_scan_finds_worlds_newest_first()
    {
        WriteWorld("Old World", played: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteWorld("Last Night", played: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        // The one somebody is looking for is the one they last played, which is also the
        // order the game's own list uses.
        Assert.Equal(["Last Night", "Old World"], InstalledWorlds.Scan(Saves).Select(w => w.Name));
    }

    [Fact]
    public void A_world_is_described_by_name_and_size()
    {
        WriteWorld("Awesome Kingdom Tales", bytes: 2 * 1024 * 1024);

        Assert.Equal("Awesome Kingdom Tales — 2 MB",
            Assert.Single(InstalledWorlds.Scan(Saves)).Describe);
    }

    [Fact]
    public void Anything_that_is_not_a_world_is_ignored()
    {
        WriteWorld("Real World");
        File.WriteAllText(Path.Combine(Saves, "notes.txt"), "not a world");
        Directory.CreateDirectory(Path.Combine(Saves, "Backups"));

        Assert.Equal("Real World", Assert.Single(InstalledWorlds.Scan(Saves)).Name);
    }

    [Fact]
    public void A_folder_that_is_not_there_scans_to_nothing() =>
        Assert.Empty(InstalledWorlds.Scan(Path.Combine(_root, "nowhere")));

    [Fact]
    public async Task Copying_a_world_leaves_the_original_where_it_was()
    {
        var original = WriteWorld("Awesome Kingdom Tales", bytes: 3 * 1024 * 1024);
        var world = Assert.Single(InstalledWorlds.Scan(Saves));

        var result = await InstalledWorlds.CopyIntoAsync(world, PackData);

        Assert.True(result.Copied);
        Assert.Null(result.Problem);

        // In the pack...
        var landed = Path.Combine(PackData, "Saves", "Awesome Kingdom Tales.vcdbs");
        Assert.True(File.Exists(landed));
        Assert.Equal(3 * 1024 * 1024, new FileInfo(landed).Length);

        // ...and still in their own install. Cairn does not write to that folder, which is
        // what makes "your plain Vintage Story goes on working" true rather than intended.
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task A_world_the_pack_already_has_is_refused_rather_than_overwritten()
    {
        WriteWorld("Awesome Kingdom Tales");
        var world = Assert.Single(InstalledWorlds.Scan(Saves));

        Directory.CreateDirectory(Path.Combine(PackData, "Saves"));
        var existing = Path.Combine(PackData, "Saves", "Awesome Kingdom Tales.vcdbs");
        File.WriteAllText(existing, "months of somebody's evenings");

        var result = await InstalledWorlds.CopyIntoAsync(world, PackData);

        Assert.False(result.Copied);
        Assert.Contains("already has a world", result.Problem);

        // Untouched. Overwriting a save is not a thing to do as a side effect of a checkbox.
        Assert.Equal("months of somebody's evenings", File.ReadAllText(existing));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_no_half_written_world()
    {
        WriteWorld("Awesome Kingdom Tales", bytes: 8 * 1024 * 1024);
        var world = Assert.Single(InstalledWorlds.Scan(Saves));

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstalledWorlds.CopyIntoAsync(
                world, PackData,
                new Progress<long>(_ => cts.Cancel()),
                cts.Token));

        // Nothing the game could try to open, and no leftover staging file either.
        var saves = Path.Combine(PackData, "Saves");
        Assert.Empty(Directory.Exists(saves) ? Directory.GetFiles(saves) : []);
    }

    [Fact]
    public async Task Copying_reports_progress_for_a_file_worth_watching()
    {
        WriteWorld("Awesome Kingdom Tales", bytes: 5 * 1024 * 1024);
        var world = Assert.Single(InstalledWorlds.Scan(Saves));

        var seen = new List<long>();
        await InstalledWorlds.CopyIntoAsync(world, PackData, new Progress<long>(seen.Add));

        Assert.NotEmpty(seen);
        Assert.Equal(world.Size, seen[^1]);
    }
}
