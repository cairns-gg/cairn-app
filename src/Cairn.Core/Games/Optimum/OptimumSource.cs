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
    /// What Cairn builds unless told otherwise.
    ///
    /// This is dizzyd/Optimum's main plus one commit: the archive-root fix, without which
    /// the macOS build fails on the client tarball unpacking to "Vintage Story.app" where
    /// bootstrap expects "vintagestory". The pin moves to plain main once that lands there.
    /// </summary>
    public static readonly OptimumSource Pinned = new(
        Url: "https://github.com/dizzyd/Optimum.git",
        Ref: "19faa307ddefe10a0d692e349a8e740aa1064b4f",
        GameVersion: "1.22.5",
        Version: "0.3.5");

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
