using Cairn.Core.Launch;

namespace Cairn.Core.Packs;

/// <summary>
/// The mod settings somebody already has, out of a plain Vintage Story install.
///
/// The companion to <see cref="InstalledMods"/> and <see cref="InstalledWorlds"/>, and there
/// for the same reason those are: an import that brings the mods and leaves everything that
/// made them work behind has done half a job. Plenty of mods only get along once a value has
/// been changed — Terrain Slabs wants Footprints named in a list before the two behave — and
/// a pack whose mods are right and whose settings are the mod authors' defaults is not the
/// thing that was being played.
///
/// Read-only about the player's own folder, like everything else Cairn does there: this
/// copies, and their plain Vintage Story goes on working exactly as it did.
///
/// <para><b>Copying files is not the same as a pack carrying settings.</b> What lands here is
/// this copy's own <c>ModConfig</c>, which nothing shares and nobody else sees. A pack that
/// carries settings *to other people* declares them in its manifest — see
/// <c>PackManifest.ModConfig</c> and the Mod config tab, which is where that is chosen, one
/// value at a time, on purpose. These files are what that tab then has to offer.</para>
/// </summary>
public static class InstalledModConfigs
{
    /// <summary>
    /// What a folder holds. Nested rather than a type of its own, because
    /// <c>ModConfigSurvey</c> is already taken by the half of this that reads settings
    /// *values* out of a pack — a different job on the same files, and two types a letter
    /// apart would be read as the same one.
    /// </summary>
    /// <param name="Files">How many there are to copy, at any depth.</param>
    /// <param name="Bytes">What they weigh, so the offer can say what it costs.</param>
    public sealed record Contents(int Files, long Bytes)
    {
        public bool Any => Files > 0;
    }

    /// <summary>Where the game keeps them, under a data path.</summary>
    public static string DirectoryIn(string dataPath) => ModConfigFiles.DirectoryIn(dataPath);

    /// <summary>
    /// What is there, or nothing. Never throws: a folder that cannot be read is one with
    /// nothing to offer, and an import is not the place to fail over a settings file.
    /// </summary>
    public static Contents Measure(string dataPath)
    {
        var root = DirectoryIn(dataPath);

        try
        {
            if (!Directory.Exists(root)) return new Contents(0, 0);

            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();

            return new Contents(files.Count, files.Sum(Length));

            static long Length(string path)
            {
                try { return new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Contents(0, 0);
        }
    }

    /// <summary>
    /// Copies them into a pack's data path.
    ///
    /// Subdirectories and all, because a mod may keep more than one file and some keep a
    /// folder. Links are not followed: one in a config folder points at something outside it
    /// which is not ours to copy, and following it into a loop would never finish.
    ///
    /// Nothing already in the pack is overwritten. A pack being imported into is new and has
    /// none of this, so the guard is for the case that arrives later — the same rule
    /// <see cref="InstalledWorlds"/> keeps, and for the same reason: what is in a pack was put
    /// there deliberately, and this is a convenience.
    /// </summary>
    /// <returns>How many files arrived.</returns>
    public static int CopyInto(string dataPath, string packDataPath)
    {
        var from = DirectoryIn(dataPath);
        if (!Directory.Exists(from)) return 0;

        var to = DirectoryIn(packDataPath);
        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var target = Path.Combine(to, relative);

            try
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                if (File.Exists(target)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One unreadable settings file is not a reason to abandon the other forty,
                // and none of this is load-bearing: the mod writes its own defaults when a
                // file is missing, which is exactly the state the pack would have been in.
            }
        }

        return copied;
    }
}
