namespace Cairn.Core.Games;

/// <summary>
/// The game versions Cairn has installed itself, under ~/.cairn/games/&lt;version&gt;.
///
/// Deliberately NOT named "*.app" on macOS: the shipped tarball has a flat layout with
/// Info.plist at the top level and no Contents/ directory, so giving the directory an
/// .app suffix makes codesign treat it as a bundle, fail to find _CodeSignature/CodeResources,
/// and report the install as damaged. A plain directory has no such problem.
/// </summary>
public sealed class GameStore
{
    private readonly string _root;

    public GameStore(string? root = null) => _root = root ?? CairnPaths.GamesRoot;

    public string Root => _root;

    public string InstallDir(string version)
    {
        if (!IsValidVersion(version))
            throw new ArgumentException($"'{version}' is not a usable version directory name.", nameof(version));

        return Path.Combine(_root, version);
    }

    /// <summary>Versions become directory names, so they are constrained like pack ids.</summary>
    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.Length <= 32
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && version is not ("." or "..");

    public IEnumerable<GameInstall> ListInstalled()
    {
        if (!Directory.Exists(_root)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var install = GameInstall.TryAt(dir);
            if (install is not null) yield return Named(install, dir);
        }
    }

    /// <summary>
    /// Falls back to the directory name when the install's own metadata cannot be read.
    ///
    /// This store names its directories by version, so a directory called "1.22.5" is far
    /// better evidence than the "unknown" GameInstall reports for an assembly it could not
    /// parse — and "unknown" is what would otherwise reach the version picker and the
    /// installed list. Only applied when the name is itself a plausible version, so a
    /// directory named anything else still reports honestly.
    /// </summary>
    private static GameInstall Named(GameInstall install, string dir)
    {
        if (GameVersions.IsPlausibleVersion(install.Version)) return install;

        var name = Path.GetFileName(dir);
        if (!GameVersions.IsPlausibleVersion(name)) return install;

        return new GameInstall
        {
            Directory = install.Directory,
            Executable = install.Executable,
            Version = name,
            Architecture = install.Architecture,
            RequiredFramework = install.RequiredFramework,
        };
    }

    /// <summary>A managed install whose reported version matches, or null.</summary>
    public GameInstall? Find(string version)
    {
        if (!IsValidVersion(version)) return null;

        var dir = InstallDir(version);
        var install = GameInstall.TryAt(dir);
        if (install is not null && !install.IsVariant) return install;

        // The directory name is what we asked for, but trust the assembly metadata: fall
        // back to scanning in case a version was installed under a differing folder name.
        //
        // Variants are skipped, and that exclusion is the whole reason this reads the
        // marker at all. A modified client reports the version it was forked from, so an
        // Optimum build of 1.22.5 answers this scan exactly as the stock game does — and
        // would then be handed to every 1.22.5 pack on the machine, silently, the moment
        // the plain install was missing from its expected folder. Running something other
        // than the game is a choice, never a fallback.
        return ListInstalled().FirstOrDefault(i => i.Version == version && !i.IsVariant);
    }

    public bool IsInstalled(string version) => Find(version) is not null;

    public void Remove(string version)
    {
        var dir = InstallDir(version);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Removes an install this store listed, by the directory it was found in.
    ///
    /// Not by its version: Find already allows for a version whose directory name differs
    /// from the version its assembly reports, and deriving the path back from the reported
    /// version in that case deletes nothing while reporting success — leaving a version
    /// that looks removed and goes on working.
    /// </summary>
    public void Remove(GameInstall install)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.Directory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));

        // Only ever inside the store, and never the store itself: this deletes recursively,
        // and an install Cairn merely found is not Cairn's to delete.
        if (!dir.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || dir == root)
            throw new InvalidOperationException($"'{install.Directory}' is not a managed install.");

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Linux file systems are case-sensitive; macOS and Windows are not.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// Everything Cairn can launch: versions it manages, plus any pre-existing install found
/// on the machine.
/// </summary>
public sealed class GameLibrary(GameStore store, GameInstall? system)
{
    public GameStore Store => store;

    /// <summary>An install already on the machine, not managed by Cairn.</summary>
    public GameInstall? System => system;

    public IReadOnlyList<GameInstall> Managed => store.ListInstalled().ToList();

    /// <summary>
    /// The install a pack targeting <paramref name="version"/> should launch: a managed
    /// install of exactly that version, else the system install if it happens to match.
    /// Returns null when nothing matches, which is what turns into "install it" in the UI.
    /// </summary>
    public GameInstall? ForVersion(string version)
    {
        var managed = store.Find(version);
        if (managed is not null) return managed;

        return system?.Version == version ? system : null;
    }

    /// <summary>Best available install when a pack's exact version is not present.</summary>
    public GameInstall? Fallback => system ?? Managed.FirstOrDefault(i => !i.IsVariant);

    /// <summary>
    /// Everything a pack targeting <paramref name="version"/> could be launched with: the
    /// stock install, plus any modified build of the same version.
    ///
    /// Offered rather than chosen. <see cref="ForVersion"/> answers "what runs by default"
    /// and never returns a variant; this answers "what could you pick", which is a question
    /// only a person can settle.
    /// </summary>
    public IReadOnlyList<GameInstall> ChoicesFor(string version)
    {
        var choices = new List<GameInstall>();

        if (ForVersion(version) is { } stock) choices.Add(stock);

        choices.AddRange(Managed.Where(
            i => i.IsVariant && string.Equals(i.Version, version, StringComparison.OrdinalIgnoreCase)));

        return choices;
    }

    /// <summary>The install in a particular directory, whatever it is. Null if not one.</summary>
    public GameInstall? At(string directory) => GameInstall.TryAt(directory);
}
