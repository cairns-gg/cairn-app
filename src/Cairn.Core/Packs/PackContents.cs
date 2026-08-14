using Cairn.Core.Games;

namespace Cairn.Core.Packs;

/// <summary>
/// What a pack holds on disk, itemised.
///
/// Built for the delete prompt: "and its downloaded mods?" is not enough to decide on when
/// the answer might be several gigabytes and a world someone has played for a month. Every
/// figure here is measured rather than estimated, because it is the last thing read before
/// something irreversible.
/// </summary>
public sealed record PackContents(
    int Mods,
    long ModsBytes,
    IReadOnlyList<string> Worlds,
    long WorldsBytes,
    long DataBytes,
    long TotalBytes)
{
    public static PackContents Of(PackStore store, string id)
    {
        var modsDir = store.ModsDir(id);
        var dataDir = store.DataDir(id);

        return new PackContents(
            // Every kind of mod file, not only .zip, so the count matches what the sweep
            // in PackSyncer would clear and what deleting the pack actually removes.
            Mods: CountFiles(modsDir, ModFileName.HasModExtension),
            ModsBytes: DirectoryGrowth.Measure(modsDir),
            Worlds: WorldsIn(dataDir),
            WorldsBytes: DirectoryGrowth.Measure(Path.Combine(dataDir, "Saves")),
            DataBytes: DirectoryGrowth.Measure(dataDir),
            // The pack directory rather than the sum of its parts: manifest, lockfile and
            // anything else in there is going too, and the total is what the disk gets back.
            TotalBytes: DirectoryGrowth.Measure(store.PackDir(id)));
    }

    private static int CountFiles(string dir, Func<string, bool> wanted)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir).Count(f => wanted(Path.GetFileName(f)))
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> WorldsIn(string dataDir)
    {
        try
        {
            var saves = Path.Combine(dataDir, "Saves");
            if (!Directory.Exists(saves)) return [];

            return Directory.GetFiles(saves, "*.vcdbs")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The lines a confirmation should show, most-costly first.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        if (Worlds.Count > 0)
            lines.Add($"{Worlds.Count} world{(Worlds.Count == 1 ? "" : "s")} "
                      + $"({Bytes.Human(WorldsBytes)}): {NameList(Worlds)}");

        if (Mods > 0)
            lines.Add($"{Mods} downloaded mod{(Mods == 1 ? "" : "s")} ({Bytes.Human(ModsBytes)})");

        if (DataBytes > 0)
            lines.Add(Lang.Get("delete-settings-and-configs"));

        return lines;
    }

    /// <summary>Names a few and counts the rest: which worlds is the actual question.</summary>
    private static string NameList(IReadOnlyList<string> names) =>
        names.Count <= 3
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";
}

/// <summary>Byte counts as people read them.</summary>
public static class Bytes
{
    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
