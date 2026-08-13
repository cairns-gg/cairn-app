namespace Cairn.Core;

/// <summary>
/// How much of a response Cairn will hold in memory before giving up on it.
///
/// Everything Cairn buffers is small: a pack document, a ModDB search page, a version
/// manifest, a mod icon. Everything large is streamed to disk instead —
/// <c>HttpCompletionOption.ResponseHeadersRead</c> in the three installers and the mod
/// downloader — so a cap on buffering costs those nothing and bounds everything else.
///
/// Without it there was no ceiling anywhere. An import URL is fetched with
/// <c>ReadAsStringAsync</c> and points wherever somebody pasted; a followed pack's host is
/// asked for a document every two hours, unattended, on a machine that may be a server. A
/// reply that never ends is a launcher that grows until it dies, and the interesting part
/// is that nothing in the response has to be valid for that to work.
///
/// Generous on purpose. The largest thing that legitimately arrives this way is a pack
/// document listing a few hundred mods, which is tens of kilobytes; sixteen megabytes is
/// three orders of magnitude of headroom and still a bound. Exceeding it throws
/// <see cref="HttpRequestException"/>, which every caller here already handles as an
/// unreachable server.
/// </summary>
public static class HttpLimits
{
    public const long MaxBufferedResponse = 16L * 1024 * 1024;

    /// <summary>
    /// Applies the cap to a client. A method rather than a factory because the three
    /// front-ends build their clients differently — one takes a test handler, two set a
    /// long timeout for the downloads they drive — and the point is that they agree about
    /// this one number, not that they agree about everything.
    /// </summary>
    public static HttpClient Bounded(this HttpClient http)
    {
        http.MaxResponseContentBufferSize = MaxBufferedResponse;
        return http;
    }
}
