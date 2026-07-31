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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"statuscode":"404"}"""),
        });
    }
}
