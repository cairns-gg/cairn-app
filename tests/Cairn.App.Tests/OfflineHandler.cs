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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;

        var url = request.RequestUri?.ToString() ?? "";

        foreach (var (ending, reply) in Replies)
            if (url.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(reply);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"statuscode":"404"}"""),
        });
    }
}
