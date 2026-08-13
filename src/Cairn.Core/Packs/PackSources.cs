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

    /// <summary>
    /// The address a response actually came from, which is not always the one it was asked
    /// for.
    ///
    /// HttpClient follows redirects without saying so, and a redirect to another https host
    /// is allowed — only a downgrade to http is refused. So a pack asked for at one host
    /// can be answered by another, and every front-end here records the address it fetched
    /// from as the pack's origin and shows it to somebody deciding whether to trust the
    /// thing. Asking the response rather than remembering the request is the difference
    /// between recording where a document came from and recording where somebody hoped it
    /// would.
    ///
    /// <para>Falls back to the requested address when the response cannot say — which is
    /// no worse than the behaviour this replaces, and is what a stubbed handler in a test
    /// produces.</para>
    ///
    /// <para>Deliberately not a refusal. A redirect is how a host moves, and refusing one
    /// would break an ordinary move to make a rare case visible; naming the real host does
    /// both.</para>
    /// </summary>
    public static string LandingAddress(HttpResponseMessage response, string requested) =>
        response.RequestMessage?.RequestUri?.ToString() is { Length: > 0 } landed
            ? landed
            : requested;
}
