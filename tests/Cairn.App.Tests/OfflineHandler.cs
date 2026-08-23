using System.Net;

namespace Cairn.App.Tests;

/// <summary>
/// Answers every request with "not found", instantly.
///
/// Showing a pack sends its rows off to ModDB for names and icons, so without this the
/// app suite quietly depends on the network being up — slower, and failing for reasons
/// that have nothing to do with the code. Anything a test actually wants to assert about
/// ModDB is supplied directly rather than fetched.
/// </summary>
public sealed class OfflineHandler : HttpMessageHandler
{
    public int Requests { get; private set; }

    /// <summary>
    /// Every URL asked for, in order. For the tests that care how *often* something was
    /// fetched rather than what came back — publishing used to sweep a pack's mods past
    /// ModDB three times to draw one dialog, and nothing but a count catches that coming
    /// back.
    /// </summary>
    public List<string> Urls { get; } = [];

    /// <summary>
    /// Replies to name it, for the few tests that need a URL to answer with something —
    /// following a pack link, mostly. Keyed by whatever the URL ends with, so a test can
    /// say "/dizzyd/anego.json" without spelling out a host.
    /// </summary>
    public Dictionary<string, HttpResponseMessage> Replies { get; } = [];

    public HttpResponseMessage Serve(string endingWith, string body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        Replies[endingWith] = new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        };

    /// <summary>
    /// The same body every time, rather than one response handed out repeatedly.
    ///
    /// <see cref="Serve"/> keeps the response itself, so reading it consumes the content and
    /// the second request for that URL gets an empty one. Fine where a test fetches once, and
    /// a trap where it does not: selecting a followed pack checks for a revision, and
    /// applying one fetches again on purpose — the second read came back empty and the update
    /// reported the author unreachable, which looks exactly like a bug in the code under test.
    /// </summary>
    public Dictionary<string, (string Body, HttpStatusCode Status)> Bodies { get; } = [];

    public void ServeAlways(string endingWith, string body,
        HttpStatusCode status = HttpStatusCode.OK) =>
        Bodies[endingWith] = (body, status);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;

        var url = request.RequestUri?.ToString() ?? "";
        Urls.Add(url);

        foreach (var (ending, reply) in Replies)
            if (url.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(reply);

        foreach (var (ending, reply) in Bodies)
            if (url.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(reply.Status)
                {
                    Content = new StringContent(reply.Body),
                });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"statuscode":"404"}"""),
        });
    }
}
