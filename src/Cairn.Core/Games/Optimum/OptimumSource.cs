using System.Text.Json;

namespace Cairn.Core.Games.Optimum;

/// <summary>
/// Which Optimum to build, and which game version that build is for.
///
/// Pinned to a commit rather than a branch. Optimum reconstructs the client by decompiling
/// it and applying ~95 patches against pinned upstream refs, so a commit that patches
/// cleanly today is the only evidence that anything patches cleanly at all — tracking a
/// branch would turn somebody else's push into a Cairn feature that stopped working, with
/// no release of Cairn involved and nothing on this side to bisect.
///
/// The game version travels with the pin for the same reason. Optimum targets exactly one
/// Vintage Story version at a time (<c>forks.json</c> says which), so Cairn has to know
/// before cloning whether it can offer Optimum for the pack in front of it — a 400 MB clone
/// and a decompile are too expensive a way to find out the answer is no.
/// </summary>
/// <param name="Url">Repository to clone.</param>
/// <param name="Ref">Commit to check out. A sha, never a branch name.</param>
/// <param name="GameVersion">The Vintage Story version this commit builds against.</param>
/// <param name="Version">Optimum's own version, for naming the install.</param>
public sealed record OptimumSource(string Url, string Ref, string GameVersion, string Version)
{
    /// <summary>
    /// Every Optimum build Cairn knows how to make, one per game version.
    ///
    /// A list rather than a single pin because Optimum supports one game version at a time
    /// and drops the previous one, while packs do not move that quickly: a pack stays on
    /// the version its mods have releases for, which is routinely the version Optimum has
    /// just stopped targeting. With one pin, shipping a Cairn release took Optimum away
    /// from those packs — an update they did not ask for, removing something that was
    /// working, to no one's benefit.
    ///
    /// Old entries keep working by construction. Optimum pins the upstream refs it patches
    /// against, so a revision that built a year ago builds the same client today; what ages
    /// is not the entry but the evidence for it.
    ///
    /// Which is what limits the length of this list, and it is an editorial limit rather
    /// than a structural one. Every entry is the claim that a 20–30 minute build finishes
    /// on a real machine, and nothing here can be checked in CI: it needs a client
    /// download, a decompiler and most of an hour. Keep the entries somebody is prepared to
    /// re-run, and delete the ones nobody is — an entry no one has built is a button that
    /// fails twenty minutes in, which is worse than the button not being there.
    ///
    /// A retired entry wants an upstream tag rather than a fork branch: a sha is only
    /// fetchable while some ref still reaches it, and branches on a fork get tidied up.
    /// </summary>
    public static readonly IReadOnlyList<OptimumSource> Known =
    [
        // Plain upstream main. Both of the commits the fork entry below carries — the
        // macOS archive-root fix and atomic client downloads — were merged upstream in
        // August 2026, so from 1.22.7 on there is nothing to carry.
        new(Url: "https://github.com/Zaldaryon/Optimum.git",
            Ref: "ca04e0cce99e4f746591725c765f4f1e7f7a6a99",
            GameVersion: "1.22.7",
            Version: "0.3.11"),

        // dizzyd/Optimum's main plus two commits, both of which Cairn needs and neither of
        // which existed upstream at the time:
        //
        // - the archive-root fix, without which the macOS build fails on the client tarball
        //   unpacking to "Vintage Story.app" where bootstrap expects "vintagestory";
        // - atomic client downloads, without which cancelling a build during the ~500 MB
        //   download leaves a short file at the cache path that every later build reuses and
        //   fails on. That one matters more here than upstream, because Cairn makes stopping
        //   a build a button rather than a Ctrl-C.
        //
        // Kept on the fork rather than moved to the upstream tag it descends from, because
        // this is the revision that was actually built and the point of a pin is that it
        // was. It goes when 1.22.5 does.
        new(Url: "https://github.com/dizzyd/Optimum.git",
            Ref: "98f26d60eb9bb8b11b9e4955f7acbb6e4c58fb34",
            GameVersion: "1.22.5",
            Version: "0.3.5"),
    ];

    /// <summary>
    /// The build for a game version, or null when there is none for it.
    ///
    /// The question every caller actually has — "may I offer Optimum for this pack" — and
    /// the reason it is answered here rather than by each front-end holding the list.
    /// </summary>
    public static OptimumSource? ForGame(string gameVersion) =>
        Known.FirstOrDefault(s => s.Supports(gameVersion));

    /// <summary>
    /// The newest build known, for a caller with no pack in hand — the CLI's default.
    ///
    /// Compared rather than taken as the first entry, so getting the list out of order
    /// cannot quietly make an old build the one a bare <c>cairn-cli optimum build</c>
    /// produces.
    /// </summary>
    public static OptimumSource Newest =>
        Known.OrderByDescending(s => s.GameVersion, GameVersionComparer.Ascending).First();

    /// <summary>Whether this build is the right one for a pack on <paramref name="gameVersion"/>.</summary>
    public bool Supports(string gameVersion) =>
        string.Equals(GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>The directory name an install of this build gets, e.g. "1.22.5-optimum".</summary>
    public string InstallName => $"{GameVersion}-optimum";

    /// <summary>
    /// The game version a checkout actually declares, read from its forks.json.
    ///
    /// Checked against <see cref="GameVersion"/> after cloning rather than trusted. The pin
    /// records what a commit built against when somebody tested it; this reads what the
    /// commit says. They disagree when a pin is bumped and the constant above is not — a
    /// mistake that otherwise surfaces as an install named for one version containing
    /// another, which is precisely the confusion variants exist to prevent.
    /// </summary>
    public static string? ReadGameVersion(string repoDir)
    {
        try
        {
            var path = Path.Combine(repoDir, "forks.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            return doc.RootElement.TryGetProperty("vintageStoryVersion", out var v)
                ? v.GetString()
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Optimum's own version, from the VERSION file. Null if it cannot be read.</summary>
    public static string? ReadVersion(string repoDir)
    {
        try
        {
            var path = Path.Combine(repoDir, "VERSION");
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
