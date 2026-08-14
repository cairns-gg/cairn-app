namespace Cairn.Core.Packs;

/// <summary>A world sitting in a Vintage Story install.</summary>
/// <param name="Name">What the game calls it, which is its filename without the extension.</param>
public sealed record InstalledWorld(string Path, string Name, long Size, DateTime LastPlayed)
{
    /// <summary>"Awesome Kingdom Tales — 1.4 GB", for a row somebody chooses from.</summary>
    public string Describe => $"{Name} — {Bytes.Human(Size)}";
}

/// <summary>What happened to one world, for a caller that has to say so.</summary>
public sealed record WorldCopy(InstalledWorld World, bool Copied, string? Problem);

/// <summary>
/// Copies a world out of a plain Vintage Story install and into a pack.
///
/// Packs have their own data path, so a world in the player's own install is not reachable
/// from the pack holding the mods it was made with — and a world generally cannot be opened
/// without them. Until there was a way to import an install, Cairn could not have known
/// which pack a given world belonged to, and said so rather than guessing. Importing is
/// exactly the moment that stops being true: those worlds were played with those mods.
///
/// Copied, never moved. Cairn does not write to the player's own data path — that is what
/// makes "your plain Vintage Story goes on working" a fact rather than an intention — and a
/// world removed from it would open nowhere else, since the pack it moved into is the only
/// thing that has its mods.
/// </summary>
public static class InstalledWorlds
{
    /// <summary>The game's own save format: one SQLite file per world.</summary>
    private const string Extension = ".vcdbs";

    public static string DefaultSavesDir =>
        Path.Combine(GameInstall.DefaultDataPath, "Saves");

    public static string SavesIn(string dataPath) => Path.Combine(dataPath, "Saves");

    /// <summary>
    /// Every world in a folder, most recently played first — which is the one somebody is
    /// looking for, and the order the game's own list uses.
    /// </summary>
    public static IReadOnlyList<InstalledWorld> Scan(string savesDir)
    {
        if (!Directory.Exists(savesDir)) return [];

        try
        {
            return Directory
                .EnumerateFiles(savesDir, "*" + Extension)
                .Select(Read)
                .Where(w => w is not null)
                .Select(w => w!)
                .OrderByDescending(w => w.LastPlayed)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static InstalledWorld? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);

            return new InstalledWorld(
                path, Path.GetFileNameWithoutExtension(path), file.Length,
                file.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a world into a pack's data path.
    ///
    /// Refuses to write over one already there. A world is somebody's save, months of it in
    /// some cases; overwriting one is not a thing to do as a side effect of a checkbox, and
    /// silently landing a second copy under an invented name is not much better.
    /// </summary>
    /// <param name="progress">Bytes copied so far, for a file that can be several gigabytes.</param>
    public static async Task<WorldCopy> CopyIntoAsync(
        InstalledWorld world, string dataPath, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var saves = SavesIn(dataPath);
        var target = Path.Combine(saves, world.Name + Extension);

        if (File.Exists(target))
            return new WorldCopy(world, false, Lang.Get("worldimport-already-here", world.Name));

        // Copied beside the target and moved, so a cancelled or failed copy never leaves a
        // half-written world for the game to open.
        var staging = target + ".partial";

        try
        {
            Directory.CreateDirectory(saves);

            await using (var source = File.OpenRead(world.Path))
            await using (var destination = File.Create(staging))
            {
                var buffer = new byte[1024 * 1024];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(done += read);
                }
            }

            File.Move(staging, target);
            return new WorldCopy(world, true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WorldCopy(world, false, e.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover .partial is untidy, not a failure anybody can act on.
            }
        }
    }
}
