namespace Cairn.Core.Packs;

/// <summary>
/// Where a pack document is allowed to come from.
///
/// A pack decides which mods get installed, and it carries the download URL and SHA-256
/// for each one — so rewriting the document in flight is enough to choose what lands on
/// someone's disk. The hashes are no defence: whoever rewrites the document writes the
/// hashes to match. That makes the transport part of the document's integrity, which is
/// why it is checked here rather than left to whoever is calling.
///
/// Both front-ends ask these questions, and the answers must not drift apart — a rule
/// that is enforced in the launcher and not the CLI is not enforced.
/// </summary>
public static class PackSources
{
    /// <summary>Whether this should be fetched rather than read from disk.</summary>
    public static bool IsRemote(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    /// <summary>
    /// Whether fetching this would cross a network a stranger could be sitting on.
    ///
    /// Loopback is not such a network: those packets never leave the machine, so there is
    /// no path to be on and nothing to rewrite them. Allowing it in a shipped build and
    /// not only a debug one costs nothing — anything that can already serve on your
    /// loopback can write to your disk directly — and it is what lets a launcher import
    /// from a server running on the same machine.
    /// </summary>
    public static bool IsRewritableInFlight(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "http"
        && !uri.IsLoopback;
}
