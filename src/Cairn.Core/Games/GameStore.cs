namespace Cairn.Core.Games;

/// <summary>
/// The game versions Cairn has installed itself, under ~/.cairn/games/&lt;version&gt;.
///
/// Named "&lt;version&gt;.app" on macOS, which the shipped tarball's flat layout — Info.plist
/// at the top level, no Contents/ directory — is exactly the old-style form of. The suffix
/// is what makes it a bundle rather than a directory that happens to contain a plist, and
/// the game needs to be one: its Info.plist sets NSHighResolutionCapable to false, and the
/// window server reads that only from a bundle. Without it the game gets a Retina drawable
/// it did not ask for, sizes its viewport in points, and renders into the bottom-left
/// quarter of its own window. Fullscreen hides this; windowed mode does not.
///
/// A symlink will not do — the window server resolves it and answers for the real path —
/// so the install directory itself carries the suffix, matching what /Applications holds
/// after an ordinary install.
///
/// The cost is that codesign now reads these as bundles and reports "code has no resources
/// but signature indicates they must be present", the game's binary being ad-hoc signed
/// with no _CodeSignature. That is true of the official install for the same reason, and it
/// only becomes a refusal to launch for a *quarantined* copy — which a download Cairn made
/// itself is not. Verified both ways round: the flat bundle launches by exec and through
/// LaunchServices.
/// </summary>
public sealed class GameStore
{
    private readonly string _root;

    public GameStore(string? root = null) => _root = root ?? CairnPaths.GamesRoot;

    public string Root => _root;

    /// <summary>What makes a directory a bundle, on the one platform that has the notion.</summary>
    private const string BundleSuffix = ".app";

    private static bool Bundled => OperatingSystem.IsMacOS();

    /// <summary>The directory name a version gets, bundle suffix and all.</summary>
    public static string DirectoryNameFor(string version) =>
        Bundled ? version + BundleSuffix : version;

    /// <summary>The version a directory name is for, whether or not it is a bundle.</summary>
    private static string NameWithoutBundleSuffix(string name) =>
        Bundled && name.EndsWith(BundleSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^BundleSuffix.Length]
            : name;

    public string InstallDir(string version)
    {
        if (!IsValidVersion(version))
            throw new ArgumentException($"'{version}' is not a usable version directory name.", nameof(version));

        return Path.Combine(_root, DirectoryNameFor(version));
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

        // Without the suffix off first, "1.22.5.app" is not a plausible version and neither
        // is the "1.22.5" that a variant's "1.22.5-optimum.app" is hiding behind two
        // suffixes — so the fallback this whole method exists to be would never fire on the
        // platform where installs are bundles.
        var name = NameWithoutBundleSuffix(Path.GetFileName(dir));

        if (!GameVersions.IsPlausibleVersion(name))
        {
            // Cairn names a variant's directory "<version>-<label>" — "1.22.5-optimum".
            // The whole name is deliberately not a plausible version, because "optimum" is
            // not a release kind, but the part in front of the label is one and this store
            // is what wrote it. Without this a variant whose metadata could not be read
            // reports no version at all, so no pack matches it and the choice is quietly
            // discarded as being for something else.
            var dash = name.IndexOf('-');
            var prefix = dash > 0 ? name[..dash] : null;

            if (prefix is null || !GameVersions.IsPlausibleVersion(prefix)) return install;

            name = prefix;
        }

        return new GameInstall
        {
            Directory = install.Directory,
            Executable = install.Executable,
            Version = name,
            Architecture = install.Architecture,
            RequiredFramework = install.RequiredFramework,

            // Rebuilding without this drops an install back to whatever .NET the machine
            // has, which on a host whose only runtime is the one the install brought is
            // none at all.
            DotnetRoot = install.DotnetRoot,

            // Carried, emphatically. Rebuilding the install without this turns a modified
            // client whose metadata could not be read into one indistinguishable from the
            // stock game — which is the single outcome the variant marker exists to make
            // impossible, arrived at by an unrelated fallback.
            Variant = install.Variant,
        };
    }

    /// <summary>
    /// The install in a directory, named the way <see cref="ListInstalled"/> names one.
    ///
    /// Used wherever a directory is looked up by path rather than found by listing — a
    /// pack's recorded choice, most of all — so that the same install does not report one
    /// version when listed and another when addressed.
    ///
    /// A recorded path that predates installs being bundles is followed to the bundle it
    /// became. A pack records its choice as a directory, and <see cref="MigrateToBundles"/>
    /// renames the directory out from under it — without this, a pack that chose a client
    /// somebody spent twenty minutes building would quietly fall back to the stock game the
    /// first time it ran after an update.
    /// </summary>
    public GameInstall? At(string directory)
    {
        var dir = Bundled && !Directory.Exists(directory)
                  && Directory.Exists(directory + BundleSuffix)
            ? directory + BundleSuffix
            : directory;

        return GameInstall.TryAt(dir) is { } install ? Named(install, dir) : null;
    }

    /// <summary>
    /// A managed install a pack can launch: that version, stock, with a client in it.
    ///
    /// The client check is the same rule as the variant one and exists for the same reason.
    /// A dedicated server download reports the version it is of exactly as a client does,
    /// so without this a machine that has one would hand it to every pack asking for that
    /// version — starting a server while every message said the game was launching. Use
    /// <see cref="FindServer"/> to ask the other question.
    /// </summary>
    public GameInstall? Find(string version)
    {
        if (!IsValidVersion(version)) return null;

        var dir = InstallDir(version);
        var install = GameInstall.TryAt(dir);
        if (install is not null && !install.IsVariant && install.HasClient) return install;

        // The directory name is what we asked for, but trust the assembly metadata: fall
        // back to scanning in case a version was installed under a differing folder name.
        //
        // Variants are skipped, and that exclusion is the whole reason this reads the
        // marker at all. A modified client reports the version it was forked from, so an
        // Optimum build of 1.22.5 answers this scan exactly as the stock game does — and
        // would then be handed to every 1.22.5 pack on the machine, silently, the moment
        // the plain install was missing from its expected folder. Running something other
        // than the game is a choice, never a fallback.
        return ListInstalled().FirstOrDefault(
            i => i.Version == version && !i.IsVariant && i.HasClient);
    }

    public bool IsInstalled(string version) => Find(version) is not null;

    /// <summary>
    /// A managed install that can run a server for this version, pointed at its server
    /// binary — or null when there is none.
    ///
    /// Takes either shape, because both are ordinary on the machines that host a server: a
    /// dedicated server download is 51 MB against the client's 600 and is all a headless
    /// box needs, while a machine somebody also plays on already has a client, and every
    /// client ships VintagestoryServer beside its own binary. Downloading a second copy of
    /// a server that is already there would be the Flatpak mistake again.
    ///
    /// Variants are skipped on the same rule as everywhere else: a modified client is
    /// something to be chosen, never something arrived at.
    /// </summary>
    public GameInstall? FindServer(string version)
    {
        if (!IsValidVersion(version)) return null;

        var here = GameInstall.TryAt(InstallDir(version));
        if (here is { IsVariant: false, HasServer: true }) return here.AsServer();

        return ListInstalled()
            .FirstOrDefault(i => i.Version == version && !i.IsVariant && i.HasServer)
            ?.AsServer();
    }

    /// <summary>
    /// Renames installs made before they were bundles, returning what moved.
    ///
    /// Cheap enough to run at every start — a directory listing and, on all but the first
    /// one, nothing. It has to be a rename rather than something done on next install: an
    /// install that stays as it is keeps rendering into a quarter of its window, and the
    /// only other cure is re-downloading 600 MB of a client already on the disk.
    ///
    /// Never fatal, and never partial in a way that loses an install. A directory it cannot
    /// rename is left exactly as it was and still runs, one suffix short of scaling
    /// correctly; a name already taken is left alone rather than merged, because two
    /// directories claiming one version is a thing to look at, not to resolve by deleting
    /// one of them.
    /// </summary>
    public IReadOnlyList<string> MigrateToBundles()
    {
        if (!Bundled || !Directory.Exists(_root)) return [];

        var moved = new List<string>();

        foreach (var dir in SafeDirectories())
        {
            var name = Path.GetFileName(dir);
            if (name.EndsWith(BundleSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            // Only things that are actually installs: the store also holds the staging
            // directories an interrupted download left behind, and renaming one of those
            // into place would produce a bundle with half a game in it.
            if (GameInstall.TryAt(dir) is null) continue;

            var target = dir + BundleSuffix;
            if (Directory.Exists(target)) continue;

            try
            {
                Directory.Move(dir, target);
                moved.Add(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left where it is, and still launchable.
            }
        }

        return moved;
    }

    private IEnumerable<string> SafeDirectories()
    {
        try
        {
            return Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

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
    public GameInstall? At(string directory) => store.At(directory);

    /// <summary>What became of a pack's recorded install choice.</summary>
    public enum ChoiceState
    {
        /// <summary>Nothing was recorded; the pack follows the stock install.</summary>
        None,

        /// <summary>The recorded install is there and is for this pack's version.</summary>
        Honoured,

        /// <summary>Something was recorded and the directory is no longer an install.</summary>
        Missing,

        /// <summary>The recorded install is for a different game version than the pack.</summary>
        WrongVersion,
    }

    /// <param name="Install">What the pack will actually launch, stock or otherwise.</param>
    /// <param name="State">Why, so a front-end can say so rather than silently differ.</param>
    /// <param name="Chosen">The recorded install, when it is real — even if unusable here.</param>
    public sealed record InstallResolution(
        GameInstall? Install, ChoiceState State, GameInstall? Chosen = null);

    /// <summary>
    /// The install a pack runs: its recorded choice when that still fits, otherwise the
    /// stock install for its version.
    ///
    /// The version check is the whole reason this exists rather than each front-end reading
    /// the recorded path. A choice is a directory, and a pack's game version can move after
    /// it was made — so a pack that chose the 1.22.5 Optimum build and then retargeted
    /// 1.22.4 went on launching 1.22.5, which is precisely the "a variant silently
    /// satisfying every pack that asks for a version" outcome that variants are constructed
    /// to prevent. Worse, the pack's mods were resolved against the version it now targets,
    /// so nothing in it was chosen for the client it was still running.
    ///
    /// A mismatched choice is ignored rather than erased, so retargeting back to the
    /// version it was made for picks it up again. Somebody trying two game versions should
    /// not lose the client they spent twenty minutes building.
    /// </summary>
    public InstallResolution ResolveFor(string version, string? chosenDirectory)
    {
        if (string.IsNullOrWhiteSpace(chosenDirectory))
            return new InstallResolution(ForVersion(version), ChoiceState.None);

        var chosen = store.At(chosenDirectory);

        if (chosen is null)
            return new InstallResolution(ForVersion(version), ChoiceState.Missing);

        if (!string.Equals(chosen.Version, version, StringComparison.OrdinalIgnoreCase))
            return new InstallResolution(ForVersion(version), ChoiceState.WrongVersion, chosen);

        return new InstallResolution(chosen, ChoiceState.Honoured, chosen);
    }
}
