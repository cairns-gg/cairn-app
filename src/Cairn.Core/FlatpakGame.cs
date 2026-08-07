using Cairn.Core.Runtime;

namespace Cairn.Core;

/// <summary>
/// Vintage Story installed as a Flatpak.
///
/// This is an ordinary Linux install in an unusual place. The deploy directory holds the
/// game exactly as the tarball does — Vintagestory, VintagestoryAPI.dll, a runtimeconfig
/// asking for Microsoft.NETCore.App 10.0 — so <see cref="GameInstall.TryAt"/> accepts it
/// verbatim once something points at it. All that is missing is the path, and the .NET
/// the Flatpak brings with it.
///
/// The second part matters more than the first. An immutable host such as Bazzite can
/// have no system .NET whatsoever: the runtime inside the app deploy is the only one on
/// the machine, and without it Cairn concludes the game cannot start and downloads a
/// private runtime to sit beside the perfectly good one it did not look at.
///
/// Nothing here launches through <c>flatpak run</c>. The apphost and every native library
/// the game bundles resolve against the host, so the sandbox — which grants the game no
/// access to a pack directory in $HOME, and so would make --addModPath point at nothing —
/// is stepped around rather than negotiated with.
/// </summary>
public static class FlatpakGame
{
    /// <summary>The Flathub application id. Also the directory name under an installation.</summary>
    public const string AppId = "at.vintagestory.VintageStory";

    /// <summary>
    /// Installation roots to look in: user, then system, then any configured elsewhere.
    ///
    /// Deliberately not <c>flatpak info --show-location</c>, which the obvious shell script
    /// reaches for. It resolves user and system installs alike, but answers with the
    /// content-hashed deploy path — a directory whose name changes on every
    /// <c>flatpak update</c>. Reading the roots off disk costs no process and yields a path
    /// that survives the game being updated underneath it.
    /// </summary>
    public static IEnumerable<string> InstallationRoots()
    {
        // Flatpak's own precedence for a user installation: the explicit override, then
        // the XDG data dir, then its default.
        var userDir = Environment.GetEnvironmentVariable("FLATPAK_USER_DIR");
        if (!string.IsNullOrWhiteSpace(userDir)) yield return userDir;

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData)) yield return Path.Combine(xdgData, "flatpak");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "flatpak");

        yield return "/var/lib/flatpak";

        foreach (var extra in ConfiguredInstallations()) yield return extra;
    }

    /// <summary>
    /// Installation roots declared in /etc/flatpak/installations.d, which is how a Flatpak
    /// ends up somewhere other than the two standard places.
    ///
    /// Worth reading rather than treating as exotic: it is what "install to the SD card"
    /// does on a Steam Deck, and a handheld running an immutable image is the machine most
    /// likely to have the game as a Flatpak in the first place — so the configuration
    /// exists precisely where missing it would hurt most.
    /// </summary>
    /// <param name="dir">Overridden only by the tests; there is one such directory.</param>
    public static IEnumerable<string> ConfiguredInstallations(
        string dir = "/etc/flatpak/installations.d")
    {
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) yield break;
            files = Directory.GetFiles(dir, "*.conf");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            // Parsed by hand rather than as a general ini: one key is wanted, and a file
            // that is malformed in any other way should cost nothing.
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Path", StringComparison.Ordinal)) continue;

                var equals = trimmed.IndexOf('=');
                if (equals < 0 || trimmed[..equals].Trim() != "Path") continue;

                var path = trimmed[(equals + 1)..].Trim();
                if (path.Length > 0) yield return path;
            }
        }
    }

    /// <summary>
    /// Where the game's files sit inside a deploy, relative to it. <c>/app</c> in the
    /// sandbox is <c>files</c> on the host, and the Flatpak unpacks the game tarball as
    /// extra data rather than building it.
    /// </summary>
    private static readonly string[] GameSubdirectory = ["files", "extra", "vintagestory"];

    /// <summary>The .NET the Flatpak ships, relative to a deploy. <c>/app/lib/dotnet</c>.</summary>
    private static readonly string[] RuntimeSubdirectory = ["files", "lib", "dotnet"];

    /// <summary>
    /// Candidate game directories, one per installation root.
    ///
    /// <c>current/active</c> rather than the hashed deploy directory: both are symlinks
    /// Flatpak repoints as it updates, and following them by name is what keeps a path
    /// Cairn has already recorded from going stale the next time the game is updated.
    /// </summary>
    public static IEnumerable<string> GameDirectories()
    {
        foreach (var root in InstallationRoots())
        {
            yield return Path.Combine(
                [root, "app", AppId, "current", "active", .. GameSubdirectory]);
        }
    }

    /// <summary>
    /// The .NET root shipped alongside a game directory, or null when there is none.
    ///
    /// Keyed off the layout rather than off having found the directory ourselves, so an
    /// install named by VINTAGE_STORY or picked by hand out of a Flatpak deploy gets its
    /// runtime too — those are the paths a person reaches for once Cairn has failed them
    /// once, and they should not be the ones that stay broken.
    ///
    /// Confirmed to be a usable root before it is offered: the sibling directory existing
    /// says nothing, and a root with no shared framework in it would take precedence over
    /// the machine's real .NET and resolve to nothing.
    /// </summary>
    public static string? BundledRuntime(string gameDirectory)
    {
        var deploy = AncestorOf(gameDirectory, GameSubdirectory.Length);
        if (deploy is null) return null;

        var root = Path.Combine([deploy, .. RuntimeSubdirectory]);
        return DotnetRuntimeLocator.Inspect(root) is null ? null : root;
    }

    /// <summary>The directory <paramref name="levels"/> above this one, or null past the top.</summary>
    private static string? AncestorOf(string path, int levels)
    {
        try
        {
            var current = Path.GetFullPath(path);

            for (var i = 0; i < levels; i++)
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent)) return null;
                current = parent;
            }

            return current;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
