namespace Cairn.Core.Packs;

/// <summary>
/// The <c>cairn://</c> links a pack page puts behind "Open in Cairn".
///
/// The form deliberately mirrors the web address with the scheme swapped —
/// <c>https://cairns.gg/dizzyd/anego</c> becomes <c>cairn://cairns.gg/dizzyd/anego</c> —
/// so a link is readable, and so the launcher never has to pull one URL out of another.
/// That matters more than it looks: anyone's web page can contain one of these, and a
/// nested URL would mean parsing an attacker's string and deciding which schemes to
/// honour. Here there is nothing to smuggle. A host and two path segments is all the
/// grammar there is, and everything else is refused.
///
/// Following one still only ever *offers* the pack. Import creates a manifest; nothing is
/// downloaded until a sync the person asks for.
/// </summary>
public static class PackUri
{
    public const string Scheme = "cairn";

    /// <summary>
    /// Turns a link into the document URL to fetch, or refuses it.
    ///
    /// https, except on loopback — the same rule <see cref="PackSources"/> applies to a
    /// typed-in address, for the same reason, and it is what lets a link on a local server
    /// be followed while testing.
    /// </summary>
    public static bool TryGetDocumentUrl(string link, out string documentUrl)
    {
        documentUrl = "";

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;

        // Exactly a user and a slug. Anything deeper, shallower, or bearing a query is not
        // a pack address, and guessing at what someone meant is how a parser grows holes.
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!string.IsNullOrEmpty(uri.Query)) return false;
        if (!parts.All(IsPlainSegment)) return false;

        var scheme = uri.IsLoopback ? "http" : "https";
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        documentUrl = $"{scheme}://{authority}/{parts[0]}/{parts[1]}.json";
        return true;
    }

    /// <summary>
    /// Conservative on purpose: these two segments are pasted into a URL, so "." and ".."
    /// and anything needing escaping have no business here.
    /// </summary>
    private static bool IsPlainSegment(string part) =>
        part is not ("." or "..")
        && part.Length <= 64
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
